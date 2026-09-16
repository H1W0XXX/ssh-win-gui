package main

import (
	"bytes"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// Runs on the execution host, between the network payload's producer and
// consumer. With zstd enabled the count is compressed bytes, not image size.
// A separate ticker also reports 0 B/s while read/write is blocked.
const dockerMeterScript = `import os, sys, threading, time
count = 0
stopped = threading.Event()
def emit(phase, speed):
    os.write(2, ('__RSYNCSHELL_DOCKER_PROGRESS__ %s %d %d\n' % (phase, count, speed)).encode('ascii'))
def tick():
    previous, since = 0, time.monotonic()
    while not stopped.wait(0.5):
        now, current = time.monotonic(), count
        emit('stream', max(0, int((current - previous) / max(now - since, 0.001))))
        previous, since = current, now
emit('stream', 0)
thread = threading.Thread(target=tick, daemon=True)
thread.start()
try:
    while True:
        block = os.read(0, 1024 * 1024)
        if not block:
            break
        pending = memoryview(block)
        while pending:
            written = os.write(1, pending)
            if written <= 0:
                raise OSError('image stream write failed')
            count += written
            pending = pending[written:]
finally:
    stopped.set()
    thread.join()
emit('import', 0)
`

const dockerProgressPrefix = "__RSYNCSHELL_DOCKER_PROGRESS__ "
const dockerVerifyMarker = "__RSYNCSHELL_DOCKER_VERIFY__"

type dockerProgressWriter struct {
	reporter    *jobReporter
	fallback    io.Writer
	pending     []byte
	offset      int64
	transferred int64
}

func (w *dockerProgressWriter) emit(phase string, speed int64) {
	_ = w.reporter.out.emit(OutboundMessage{Type: "progress", JobID: w.reporter.jobID, Phase: phase,
		Transferred: w.offset + w.transferred, ProtocolWritten: w.offset + w.transferred, BytesPerSecond: speed})
}

func (w *dockerProgressWriter) line(line []byte) {
	text := strings.TrimSpace(string(line))
	if text == dockerVerifyMarker {
		w.emit("docker_verify", 0)
		return
	}
	if strings.HasPrefix(text, dockerProgressPrefix) {
		fields := strings.Fields(strings.TrimPrefix(text, dockerProgressPrefix))
		if len(fields) == 3 && (fields[0] == "stream" || fields[0] == "import") {
			count, e1 := strconv.ParseInt(fields[1], 10, 64)
			speed, e2 := strconv.ParseInt(fields[2], 10, 64)
			if e1 == nil && e2 == nil && count >= w.transferred && speed >= 0 && count <= int64(^uint64(0)>>1)-w.offset {
				w.transferred = count
				w.emit("docker_"+fields[0], speed)
				return
			}
		}
	}
	_, _ = fmt.Fprintln(w.fallback, string(line))
}

func (w *dockerProgressWriter) Write(data []byte) (int, error) {
	n := len(data)
	for len(data) > 0 {
		end := bytes.IndexByte(data, '\n')
		if end < 0 {
			end = len(data)
		}
		w.pending = append(w.pending, data[:end]...)
		if len(w.pending) > 16384 {
			_, _ = w.fallback.Write(w.pending[:16384])
			w.pending = nil
		}
		if end < len(data) {
			w.line(w.pending)
			w.pending = nil
			data = data[end+1:]
		} else {
			data = nil
		}
	}
	return n, nil
}

func (w *dockerProgressWriter) flush() {
	if len(w.pending) > 0 {
		w.line(w.pending)
		w.pending = nil
	}
}
