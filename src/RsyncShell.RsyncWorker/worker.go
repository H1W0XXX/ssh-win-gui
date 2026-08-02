package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sync"
	"time"
)

const maxMessageBytes = 1024 * 1024

type emitter struct {
	mu  sync.Mutex
	enc *json.Encoder
}

func newEmitter(w io.Writer) *emitter {
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	return &emitter{enc: enc}
}

func (e *emitter) emit(msg OutboundMessage) error {
	if msg.Timestamp.IsZero() {
		msg.Timestamp = time.Now().UTC()
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.enc.Encode(msg)
}

type activeJob struct {
	cancel   context.CancelFunc
	mu       sync.Mutex
	terminal bool
}

type Worker struct {
	in  io.Reader
	out *emitter

	mu   sync.Mutex
	jobs map[string]*activeJob
	wg   sync.WaitGroup
}

func NewWorker(in io.Reader, out io.Writer) *Worker {
	return &Worker{
		in:   in,
		out:  newEmitter(out),
		jobs: make(map[string]*activeJob),
	}
}

func (w *Worker) Run(ctx context.Context) error {
	if err := w.out.emit(helloMessage()); err != nil {
		return fmt.Errorf("write hello: %w", err)
	}

	scanner := bufio.NewScanner(w.in)
	scanner.Buffer(make([]byte, 64*1024), maxMessageBytes)
	firstRecord := true
	for scanner.Scan() {
		line := bytes.TrimSpace(scanner.Bytes())
		if firstRecord {
			line = bytes.TrimPrefix(line, []byte{0xef, 0xbb, 0xbf})
			firstRecord = false
		}
		if len(line) == 0 {
			continue
		}
		if err := w.handleLine(ctx, line); err != nil {
			return err
		}
	}

	w.cancelAll()
	w.wg.Wait()
	if err := scanner.Err(); err != nil {
		return fmt.Errorf("read NDJSON: %w", err)
	}
	return nil
}

func (w *Worker) handleLine(parent context.Context, line []byte) error {
	var msg InboundMessage
	dec := json.NewDecoder(bytes.NewReader(line))
	dec.DisallowUnknownFields()
	if err := dec.Decode(&msg); err != nil {
		return w.out.emit(OutboundMessage{
			Type:  "error",
			Error: &WorkerError{Code: "invalid_json", Message: err.Error()},
		})
	}
	var trailing any
	if err := dec.Decode(&trailing); err != io.EOF {
		return w.out.emit(OutboundMessage{
			Type:  "error",
			Error: &WorkerError{Code: "invalid_json", Message: "multiple JSON values in one NDJSON record"},
		})
	}

	switch msg.Type {
	case "transfer":
		return w.startTransfer(parent, msg)
	case "cancel":
		return w.cancelTransfer(msg)
	default:
		return w.out.emit(OutboundMessage{
			Type:      "error",
			RequestID: msg.RequestID,
			Error:     &WorkerError{Code: "unsupported_operation", Message: fmt.Sprintf("unsupported message type %q", msg.Type)},
		})
	}
}

func (w *Worker) startTransfer(parent context.Context, msg InboundMessage) error {
	if msg.RequestID == "" {
		return w.out.emit(OutboundMessage{
			Type:  "error",
			Error: &WorkerError{Code: "invalid_request", Message: "requestId is required"},
		})
	}
	if msg.Transfer == nil {
		return w.out.emit(OutboundMessage{
			Type:      "error",
			RequestID: msg.RequestID,
			Error:     &WorkerError{Code: "invalid_request", Message: "transfer object is required"},
		})
	}
	if err := validateTransferRequest(msg.Transfer); err != nil {
		return w.out.emit(OutboundMessage{
			Type:      "error",
			RequestID: msg.RequestID,
			Error:     publicError(err),
		})
	}

	jobID, err := newJobID()
	if err != nil {
		return fmt.Errorf("generate job id: %w", err)
	}
	jobCtx, cancel := context.WithCancel(parent)
	w.mu.Lock()
	w.jobs[jobID] = &activeJob{cancel: cancel}
	w.mu.Unlock()

	if err := w.out.emit(OutboundMessage{
		Type:      "state",
		RequestID: msg.RequestID,
		JobID:     jobID,
		State:     "queued",
	}); err != nil {
		cancel()
		w.deleteJob(jobID)
		return err
	}

	w.wg.Add(1)
	go func() {
		defer w.wg.Done()
		defer cancel()
		w.runJob(jobCtx, jobID, w.job(jobID), *msg.Transfer)
	}()
	return nil
}

func (w *Worker) runJob(ctx context.Context, jobID string, job *activeJob, req TransferRequest) {
	reporter := &jobReporter{jobID: jobID, out: w.out}
	job.mu.Lock()
	if ctx.Err() == nil && !job.terminal {
		reporter.state("running")
	}
	job.mu.Unlock()
	stats, err := runTransfer(ctx, req, reporter)
	if err == nil {
		w.finishJob(jobID, job, OutboundMessage{
			Type:  "completed",
			JobID: jobID,
			State: "success",
			Stats: stats,
		})
		return
	}
	if errors.Is(err, context.Canceled) || errors.Is(ctx.Err(), context.Canceled) {
		w.finishJob(jobID, job, OutboundMessage{
			Type:  "completed",
			JobID: jobID,
			State: "cancelled",
			Stats: stats,
			Error: &WorkerError{Code: "cancelled", Message: "transfer cancelled"},
		})
		return
	}
	w.finishJob(jobID, job, OutboundMessage{
		Type:  "completed",
		JobID: jobID,
		State: "failed",
		Stats: stats,
		Error: publicError(err),
	})
}

func (w *Worker) cancelTransfer(msg InboundMessage) error {
	if msg.RequestID == "" || msg.JobID == "" {
		return w.out.emit(OutboundMessage{
			Type:      "error",
			RequestID: msg.RequestID,
			JobID:     msg.JobID,
			Error:     &WorkerError{Code: "invalid_request", Message: "requestId and jobId are required"},
		})
	}
	w.mu.Lock()
	job, ok := w.jobs[msg.JobID]
	w.mu.Unlock()
	if !ok {
		return w.out.emit(OutboundMessage{
			Type:      "error",
			RequestID: msg.RequestID,
			Error:     &WorkerError{Code: "job_not_found", Message: "job is not active"},
		})
	}
	job.mu.Lock()
	defer job.mu.Unlock()
	if job.terminal {
		return w.out.emit(OutboundMessage{
			Type:      "error",
			RequestID: msg.RequestID,
			Error:     &WorkerError{Code: "job_not_found", Message: "job is no longer active"},
		})
	}
	if err := w.out.emit(OutboundMessage{
		Type:      "state",
		RequestID: msg.RequestID,
		JobID:     msg.JobID,
		State:     "cancel_requested",
	}); err != nil {
		job.cancel()
		return err
	}
	job.cancel()
	return nil
}

func (w *Worker) job(jobID string) *activeJob {
	w.mu.Lock()
	defer w.mu.Unlock()
	return w.jobs[jobID]
}

func (w *Worker) finishJob(jobID string, job *activeJob, message OutboundMessage) {
	job.mu.Lock()
	if !job.terminal {
		job.terminal = true
		_ = w.out.emit(message)
	}
	job.mu.Unlock()
	w.deleteJob(jobID)
}

func (w *Worker) cancelAll() {
	w.mu.Lock()
	defer w.mu.Unlock()
	for _, job := range w.jobs {
		job.cancel()
	}
}

func (w *Worker) deleteJob(jobID string) {
	w.mu.Lock()
	delete(w.jobs, jobID)
	w.mu.Unlock()
}

func newJobID() (string, error) {
	var raw [12]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "", err
	}
	return "job-" + hex.EncodeToString(raw[:]), nil
}

type codedError struct {
	code string
	err  error
}

func (e *codedError) Error() string { return e.err.Error() }
func (e *codedError) Unwrap() error { return e.err }

func errorCode(code string, err error) error {
	return &codedError{code: code, err: err}
}

func publicError(err error) *WorkerError {
	var coded *codedError
	if errors.As(err, &coded) {
		return &WorkerError{Code: coded.code, Message: coded.err.Error()}
	}
	return &WorkerError{Code: "transfer_failed", Message: err.Error()}
}

type jobReporter struct {
	jobID string
	out   *emitter
}

func (r *jobReporter) state(state string) {
	_ = r.out.emit(OutboundMessage{Type: "state", JobID: r.jobID, State: state})
}

func (r *jobReporter) log(level, message string) {
	_ = r.out.emit(OutboundMessage{Type: "log", JobID: r.jobID, Level: level, Message: message})
}

func (r *jobReporter) progress(phase string, read, written int64) {
	_ = r.out.emit(OutboundMessage{
		Type:            "progress",
		JobID:           r.jobID,
		Phase:           phase,
		ProtocolRead:    read,
		ProtocolWritten: written,
	})
}
