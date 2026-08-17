package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gokrazy/rsync"
	"github.com/gokrazy/rsync/rsyncclient"
	"golang.org/x/crypto/ssh"
)

func validateTransferRequest(req *TransferRequest) error {
	if req.Direction != "upload" && req.Direction != "download" {
		return errorCode("invalid_request", fmt.Errorf("direction must be upload or download, got %q", req.Direction))
	}
	if strings.TrimSpace(req.LocalPath) == "" || strings.TrimSpace(req.RemotePath) == "" {
		return errorCode("invalid_request", errors.New("localPath and remotePath are required"))
	}
	if strings.ContainsAny(req.RemotePath, "\x00\r\n") {
		return errorCode("invalid_request", errors.New("remotePath must not contain NUL or line breaks"))
	}
	if !strings.HasPrefix(req.RemotePath, "/") {
		return errorCode("invalid_request", errors.New("remotePath must be an absolute POSIX path"))
	}
	if strings.TrimSpace(req.Remote.Host) == "" || strings.TrimSpace(req.Remote.User) == "" {
		return errorCode("invalid_request", errors.New("remote.host and remote.user are required"))
	}
	if req.Remote.Port < 0 || req.Remote.Port > 65535 {
		return errorCode("invalid_request", errors.New("remote.port must be between 1 and 65535, or 0 for port 22"))
	}
	if req.Options.Partial {
		return errorCode("unsupported_option", errors.New("partial-file retention is unsupported by the pinned Go rsync implementation"))
	}
	if req.Options.DryRun {
		return errorCode("unsupported_option", errors.New("dry-run is disabled because the pinned Go rsync receiver writes non-protocol data to stdout"))
	}
	if req.Options.Delete {
		return errorCode("unsupported_option", errors.New("delete is disabled because the pinned Go rsync implementation does not reliably forward it to the remote receiver"))
	}
	if req.ExactDestination && req.Direction != "download" {
		return errorCode("invalid_request", errors.New("exactDestination is only valid for downloads"))
	}
	if req.ExactDestination && req.CopyContents {
		return errorCode("invalid_request", errors.New("exactDestination cannot be combined with copyContents"))
	}
	if err := validateTransferOptions(req.Options); err != nil {
		return err
	}
	return validateSecurityConfig(req.Remote)
}

func validateTransferOptions(options TransferOptions) error {
	if options.BandwidthLimitKbps < 0 {
		return errorCode("invalid_request", errors.New("bandwidthLimitKbps must be zero or greater"))
	}
	if len(options.ExtraArguments) > 32 {
		return errorCode("invalid_request", errors.New("at most 32 extra rsync arguments are allowed"))
	}
	for _, argument := range options.ExtraArguments {
		if argument == "" || len(argument) > 512 || strings.ContainsAny(argument, "\x00\r\n") {
			return errorCode("invalid_request", errors.New("extra rsync arguments must be non-empty, at most 512 bytes, and contain no NUL or line breaks"))
		}
	}
	return nil
}

func validateSecurityConfig(remote RemoteEndpoint) error {
	switch remote.Auth.Method {
	case "password":
		if remote.Auth.Password == "" {
			return errorCode("invalid_request", errors.New("password authentication requires password"))
		}
	case "private_key":
		if remote.Auth.PrivateKeyPath == "" {
			return errorCode("invalid_request", errors.New("private_key authentication requires privateKeyPath"))
		}
	default:
		return errorCode("unsupported_authentication", fmt.Errorf("authentication method %q is unsupported", remote.Auth.Method))
	}

	mode := remote.HostKey.Mode
	if mode == "" || mode == "known_hosts" {
		return nil
	}
	if mode == "sha256" {
		fingerprintCount := len(remote.HostKey.SHA256Fingerprints)
		if strings.TrimSpace(remote.HostKey.SHA256) != "" {
			fingerprintCount++
		}
		if fingerprintCount == 0 {
			return errorCode("invalid_request", errors.New("sha256 host-key mode requires at least one OpenSSH SHA256:... fingerprint"))
		}
		return nil
	}
	if mode == "log_only" {
		return nil
	}
	return errorCode("unsupported_host_key_policy", fmt.Errorf("host-key mode %q is unsupported", mode))
}

func runTransfer(ctx context.Context, req TransferRequest, reporter *jobReporter) (*TransferStat, error) {
	if req.Direction == "upload" {
		if _, err := os.Stat(req.LocalPath); err != nil {
			return nil, errorCode("local_path", fmt.Errorf("read upload source: %w", err))
		}
	}

	reporter.state("connecting")
	sshClient, err := dialSSH(ctx, req.Remote, reporter)
	if err != nil {
		return nil, err
	}
	defer sshClient.Close()

	reporter.log("warning", "SSH connected without host-key verification")
	reporter.state("transferring")
	return runRsyncOverSSH(ctx, sshClient, req, reporter)
}

func runRsyncOverSSH(ctx context.Context, sshClient *ssh.Client, req TransferRequest, reporter *jobReporter) (*TransferStat, error) {
	args := buildRsyncArgs(req.Options)
	clientOptions := []rsyncclient.Option{rsyncclient.WithStderr(&rsyncLogWriter{reporter: reporter, level: "info"})}
	if req.Direction == "upload" {
		clientOptions = append(clientOptions, rsyncclient.WithSender())
	} else if req.ExactDestination {
		clientOptions = append(clientOptions, rsyncclient.WithExactDestination())
	}
	client, err := rsyncclient.New(args, clientOptions...)
	if err != nil {
		return nil, errorCode("unsupported_option", fmt.Errorf("create rsync client: %w", err))
	}

	session, err := sshClient.NewSession()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("create SSH session: %w", err))
	}
	defer session.Close()

	stdin, err := session.StdinPipe()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("open SSH stdin: %w", err))
	}
	stdout, err := session.StdoutPipe()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("open SSH stdout: %w", err))
	}
	stderr, err := session.StderrPipe()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("open SSH stderr: %w", err))
	}

	remotePath := req.RemotePath
	if req.Direction == "download" && req.CopyContents {
		remotePath = ensureRemoteTrailingSlash(remotePath)
	}
	serverArgs := forceProtocol(client.ServerCommandOptions(remotePath))
	remoteCommand := "command rsync " + joinShellArgs(serverArgs)
	if err := session.Start("sh -c " + shellQuote(remoteCommand)); err != nil {
		return nil, errorCode("remote_rsync", fmt.Errorf("start remote rsync: %w", err))
	}

	stderrDone := make(chan struct{})
	go func() {
		defer close(stderrDone)
		copyRsyncLogs(stderr, reporter)
	}()

	activity := &activityReadWriter{Reader: stdout, Writer: stdin}
	stopProgress := make(chan struct{})
	progressDone := make(chan struct{})
	go reportProgress(activity, reporter, stopProgress, progressDone)

	cancelWatchDone := make(chan struct{})
	go func() {
		select {
		case <-ctx.Done():
			_ = session.Close()
		case <-cancelWatchDone:
		}
	}()
	defer close(cancelWatchDone)

	localPath := req.LocalPath
	if req.Direction == "upload" && req.CopyContents {
		localPath = ensureLocalTrailingSlash(localPath)
	}
	result, runErr := client.Run(ctx, activity, []string{localPath})
	close(stopProgress)
	<-progressDone

	stats := &TransferStat{
		ProtocolRead:    activity.read.Load(),
		ProtocolWritten: activity.written.Load(),
	}
	if result != nil && result.Stats != nil {
		stats.SourceSize = result.Stats.Size
	}
	if runErr != nil {
		_ = session.Close()
		<-stderrDone
		if ctx.Err() != nil {
			return stats, ctx.Err()
		}
		return stats, errorCode("rsync_protocol", fmt.Errorf("rsync protocol failed: %w", runErr))
	}
	_ = stdin.Close()
	if waitErr := session.Wait(); waitErr != nil {
		<-stderrDone
		if ctx.Err() != nil {
			return stats, ctx.Err()
		}
		return stats, errorCode("remote_rsync", fmt.Errorf("remote rsync exited unsuccessfully: %w", waitErr))
	}
	<-stderrDone
	reporter.progress("transfer", stats.ProtocolRead, stats.ProtocolWritten)
	return stats, nil
}

func buildRsyncArgs(options TransferOptions) []string {
	args := []string{"-r"}
	if options.PreserveTimes {
		args = append(args, "-t")
	}
	if options.PreservePermissions {
		args = append(args, "-p")
	}
	if options.PreserveLinks {
		args = append(args, "-l")
	}
	if options.Compress {
		args = append(args, "-z")
	}
	if options.BandwidthLimitKbps > 0 {
		args = append(args, fmt.Sprintf("--bwlimit=%d", options.BandwidthLimitKbps))
	}
	args = append(args, options.ExtraArguments...)
	return args
}

func forceProtocol(args []string) []string {
	protocolArg := fmt.Sprintf("--protocol=%d", rsync.ProtocolVersion)
	for _, arg := range args {
		if strings.HasPrefix(arg, "--protocol=") {
			return args
		}
	}
	out := make([]string, 0, len(args)+1)
	if len(args) > 0 && args[0] == "--server" {
		out = append(out, args[0], protocolArg)
		return append(out, args[1:]...)
	}
	out = append(out, protocolArg)
	return append(out, args...)
}

func ensureLocalTrailingSlash(path string) string {
	cleaned := filepath.Clean(path)
	return strings.TrimRight(cleaned, `/\\`) + "/"
}

func ensureRemoteTrailingSlash(path string) string {
	if path == "/" {
		return path
	}
	return strings.TrimRight(path, "/") + "/"
}

func shellQuote(value string) string {
	return "'" + strings.ReplaceAll(value, "'", `'"'"'`) + "'"
}

func joinShellArgs(args []string) string {
	quoted := make([]string, 0, len(args))
	for _, arg := range args {
		quoted = append(quoted, shellQuote(arg))
	}
	return strings.Join(quoted, " ")
}

type activityReadWriter struct {
	io.Reader
	io.Writer
	read    atomic.Int64
	written atomic.Int64
}

func (rw *activityReadWriter) Read(p []byte) (int, error) {
	n, err := rw.Reader.Read(p)
	if n > 0 {
		rw.read.Add(int64(n))
	}
	return n, err
}

func (rw *activityReadWriter) Write(p []byte) (int, error) {
	n, err := rw.Writer.Write(p)
	if n > 0 {
		rw.written.Add(int64(n))
	}
	return n, err
}

func reportProgress(activity *activityReadWriter, reporter *jobReporter, stop <-chan struct{}, done chan<- struct{}) {
	defer close(done)
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()
	var previousRead, previousWritten int64 = -1, -1
	for {
		select {
		case <-ticker.C:
			read := activity.read.Load()
			written := activity.written.Load()
			if read != previousRead || written != previousWritten {
				reporter.progress("transfer", read, written)
				previousRead, previousWritten = read, written
			}
		case <-stop:
			return
		}
	}
}

type rsyncLogWriter struct {
	reporter           *jobReporter
	level              string
	parseProgress      bool
	mu                 sync.Mutex
	buffer             strings.Builder
	truncated          bool
	emittedLines       int
	suppressionEmitted bool
}

const maxDiagnosticLineBytes = 64 * 1024
const maxDiagnosticLines = 200

func (w *rsyncLogWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	n := len(p)
	for _, b := range p {
		if b == '\n' || b == '\r' {
			w.flushLocked()
			continue
		}
		if w.buffer.Len() < maxDiagnosticLineBytes {
			w.buffer.WriteByte(b)
		} else {
			w.truncated = true
		}
	}
	return n, nil
}

func (w *rsyncLogWriter) flushLocked() {
	line := strings.TrimSpace(w.buffer.String())
	w.buffer.Reset()
	truncated := w.truncated
	w.truncated = false
	if truncated {
		line += " ... [diagnostic line truncated]"
	}
	if line != "" {
		if w.parseProgress {
			if progress, ok := parseRsyncProgressLine(line); ok {
				w.reporter.transferProgress(progress.transferred, progress.percent, progress.bytesPerSecond)
				return
			}
		}
		level := w.level
		if level == "" {
			level = "info"
		}
		if w.emittedLines < maxDiagnosticLines {
			w.reporter.log(level, line)
			w.emittedLines++
		} else if !w.suppressionEmitted {
			w.reporter.log(level, "Further rsync diagnostic lines were suppressed.")
			w.suppressionEmitted = true
		}
	}
}

type rsyncProgress struct {
	transferred    int64
	percent        float64
	bytesPerSecond int64
}

var rsyncProgressPattern = regexp.MustCompile(`^\s*([0-9][0-9,]*)\s+([0-9]{1,3})%\s+([0-9]+(?:\.[0-9]+)?)([kKMGTPE]?B/s)(?:\s|$)`)

func parseRsyncProgressLine(line string) (rsyncProgress, bool) {
	matches := rsyncProgressPattern.FindStringSubmatch(line)
	if matches == nil {
		return rsyncProgress{}, false
	}
	transferred, err := strconv.ParseInt(strings.ReplaceAll(matches[1], ",", ""), 10, 64)
	if err != nil {
		return rsyncProgress{}, false
	}
	percent, err := strconv.ParseFloat(matches[2], 64)
	if err != nil || percent < 0 || percent > 100 {
		return rsyncProgress{}, false
	}
	speed, err := strconv.ParseFloat(matches[3], 64)
	if err != nil || speed < 0 {
		return rsyncProgress{}, false
	}
	multipliers := map[byte]float64{
		'k': 1_000,
		'K': 1_000,
		'M': 1_000_000,
		'G': 1_000_000_000,
		'T': 1_000_000_000_000,
		'P': 1_000_000_000_000_000,
		'E': 1_000_000_000_000_000_000,
	}
	unit := matches[4]
	multiplier := 1.0
	if len(unit) > len("B/s") {
		multiplier = multipliers[unit[0]]
	}
	return rsyncProgress{
		transferred:    transferred,
		percent:        percent,
		bytesPerSecond: int64(speed * multiplier),
	}, true
}

func (w *rsyncLogWriter) flush() {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.flushLocked()
}

func copyRsyncLogs(reader io.Reader, reporter *jobReporter) {
	writer := &rsyncLogWriter{reporter: reporter, level: "error"}
	_, err := io.Copy(writer, reader)
	writer.flush()
	if err != nil {
		reporter.log("error", "read remote rsync diagnostics: "+err.Error())
	}
}
