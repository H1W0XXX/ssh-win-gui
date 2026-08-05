package main

import (
	"bytes"
	"context"
	"crypto/ed25519"
	cryptorand "crypto/rand"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"golang.org/x/crypto/ssh"
)

// Apache-2.0 test fixture from github.com/kayrus/putty v1.0.5.
const puttyV2TestKey = `PuTTY-User-Key-File-2: ssh-rsa
Encryption: none
Comment: a@b
Public-Lines: 2
AAAAB3NzaC1yc2EAAAABJQAAAEEAqexbeyaaBw2rFZc2vwg4DqjOo6fQyOdfo9O2
20y96bUlHRYzRWmIDzHC5gZBzlHQ6M56dprxhCJbsIQig+sQ+w==
Private-Lines: 4
AAAAQBb2bTonz6AWmpQ3B2XsWpoyfMoB68gfREaSO04RShipjkwri4K8DmSX1+Nb
xUyFO7aS7rpsO3mitZtYt3bS3z0AAAAhANvUiZew5AgUZ3peSzSqaVch4vapHml4
7nx03dx4aS5JAAAAIQDF4bDGZq973zNxW62MVA6MsxKdNsIDILMFvhXFNc/VIwAA
ACEAgd1SYGV2aEEMQaMGQ4CnjQeiAuZL4z7OVTBTrtGap1A=
Private-MAC: 3c3a9bd98e8e912f6163be95321676b6103aaed8`

func TestHelloDeclaresSecurityAndTransferLimits(t *testing.T) {
	msg := helloMessage()
	if msg.ProtocolVersion != ipcProtocolVersion {
		t.Fatalf("protocol version = %d, want %d", msg.ProtocolVersion, ipcProtocolVersion)
	}
	if msg.Capabilities == nil {
		t.Fatal("missing capabilities")
	}
	if msg.Capabilities.FallbackTransport {
		t.Fatal("fallback transport must remain disabled")
	}
	if !msg.Capabilities.Compression || msg.Capabilities.PartialFiles {
		t.Fatal("compression must be advertised while partial files remain unsupported")
	}
	if msg.Capabilities.Progress != "protocol_bytes" {
		t.Fatalf("progress = %q", msg.Capabilities.Progress)
	}
	if !contains(msg.Capabilities.HostKeyModes, "log_only") {
		t.Fatalf("log_only host-key mode is not advertised: %q", msg.Capabilities.HostKeyModes)
	}
	if !contains(msg.Capabilities.Operations, "remote_transfer") || !contains(msg.Capabilities.Operations, "probe_routes") {
		t.Fatalf("machine-transfer operations are not advertised: %q", msg.Capabilities.Operations)
	}
}

func TestValidateTransferRejectsUnsupportedFeatures(t *testing.T) {
	base := validRequest()
	base.Options.Partial = true
	assertErrorCode(t, validateTransferRequest(&base), "unsupported_option")

	base = validRequest()
	base.Options.DryRun = true
	assertErrorCode(t, validateTransferRequest(&base), "unsupported_option")

	base = validRequest()
	base.Options.Delete = true
	assertErrorCode(t, validateTransferRequest(&base), "unsupported_option")

	base = validRequest()
	base.Remote.Auth.Method = "putty_session"
	assertErrorCode(t, validateTransferRequest(&base), "unsupported_authentication")

	base = validRequest()
	base.Remote.HostKey.Mode = "insecure"
	assertErrorCode(t, validateTransferRequest(&base), "unsupported_host_key_policy")

	base = validRequest()
	base.RemotePath = "~/relative"
	assertErrorCode(t, validateTransferRequest(&base), "invalid_request")
}

func TestRsyncLogWriterBoundsAndContinuesAfterLongLines(t *testing.T) {
	var output bytes.Buffer
	reporter := &jobReporter{jobID: "job-test", out: newEmitter(&output)}
	writer := &rsyncLogWriter{reporter: reporter, level: "error"}
	longLine := strings.Repeat("x", maxDiagnosticLineBytes+1024)
	if _, err := writer.Write([]byte(longLine + "\nnext\n")); err != nil {
		t.Fatal(err)
	}
	writer.flush()

	dec := json.NewDecoder(&output)
	var first, second OutboundMessage
	if err := dec.Decode(&first); err != nil {
		t.Fatal(err)
	}
	if err := dec.Decode(&second); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(first.Message, "truncated") || len(first.Message) > maxDiagnosticLineBytes+128 {
		t.Fatalf("first diagnostic was not bounded: len=%d message tail=%q", len(first.Message), first.Message[len(first.Message)-64:])
	}
	if second.Message != "next" {
		t.Fatalf("second diagnostic = %q, want next", second.Message)
	}
}

func TestRsyncProgressLineIsStructuredInsteadOfLogged(t *testing.T) {
	var output bytes.Buffer
	reporter := &jobReporter{jobID: "job-progress", out: newEmitter(&output)}
	writer := &rsyncLogWriter{reporter: reporter, level: "info", parseProgress: true}
	if _, err := writer.Write([]byte("1,234,567  42%  12.50MB/s  0:00:01 (xfr#1, to-chk=1/2)\r")); err != nil {
		t.Fatal(err)
	}

	var message OutboundMessage
	if err := json.NewDecoder(&output).Decode(&message); err != nil {
		t.Fatal(err)
	}
	if message.Type != "progress" || message.Transferred != 1_234_567 ||
		message.Percent != 42 || message.BytesPerSecond != 12_500_000 {
		t.Fatalf("unexpected progress message: %#v", message)
	}
	if message.Message != "" {
		t.Fatalf("progress leaked into log message: %q", message.Message)
	}
}

func TestRemoteRsyncProgressOptionIsAddedOnlyWhenNeeded(t *testing.T) {
	if hasRsyncProgressOption([]string{"-r", "-P"}) != true {
		t.Fatal("-P was not recognized as a progress option")
	}
	if hasRsyncProgressOption([]string{"-r", "--info=stats2,progress2"}) != true {
		t.Fatal("--info=progress2 was not recognized as a progress option")
	}
	if hasRsyncProgressOption([]string{"-r", "-z"}) {
		t.Fatal("ordinary rsync options were mistaken for progress output")
	}
}

func TestPinnedHostKeyCallback(t *testing.T) {
	_, privateKey, err := ed25519.GenerateKey(cryptorand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	publicKey, err := ssh.NewPublicKey(privateKey.Public())
	if err != nil {
		t.Fatal(err)
	}
	fingerprint := ssh.FingerprintSHA256(publicKey)
	callback, err := buildHostKeyCallback(HostKeyConfig{Mode: "sha256", SHA256: fingerprint}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := callback("example:22", &net.TCPAddr{}, publicKey); err != nil {
		t.Fatalf("matching fingerprint rejected: %v", err)
	}

	callback, err = buildHostKeyCallback(HostKeyConfig{Mode: "sha256", SHA256: "SHA256:not-the-key"}, nil)
	if err != nil {
		t.Fatal(err)
	}
	assertErrorCode(t, callback("example:22", &net.TCPAddr{}, publicKey), "host_key")

	callback, err = buildHostKeyCallback(HostKeyConfig{
		Mode:               "sha256",
		SHA256Fingerprints: []string{"SHA256:not-the-key", fingerprint},
	}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := callback("example:22", &net.TCPAddr{}, publicKey); err != nil {
		t.Fatalf("matching fingerprint in trusted set rejected: %v", err)
	}
}

func TestLogOnlyHostKeyCallbackReportsAndAcceptsFingerprint(t *testing.T) {
	_, privateKey, err := ed25519.GenerateKey(cryptorand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	publicKey, err := ssh.NewPublicKey(privateKey.Public())
	if err != nil {
		t.Fatal(err)
	}

	var gotAlgorithm, gotFingerprint string
	callback, err := buildHostKeyCallback(
		HostKeyConfig{Mode: "log_only"},
		func(algorithm, fingerprint string) {
			gotAlgorithm = algorithm
			gotFingerprint = fingerprint
		})
	if err != nil {
		t.Fatal(err)
	}
	if err := callback("example:22", &net.TCPAddr{}, publicKey); err != nil {
		t.Fatalf("log-only callback rejected host key: %v", err)
	}
	if gotAlgorithm != ssh.KeyAlgoED25519 {
		t.Fatalf("algorithm = %q, want %q", gotAlgorithm, ssh.KeyAlgoED25519)
	}
	if gotFingerprint != ssh.FingerprintSHA256(publicKey) {
		t.Fatalf("fingerprint = %q, want %q", gotFingerprint, ssh.FingerprintSHA256(publicKey))
	}
}

func TestBuildAuthMethodAcceptsPuTTYV2Key(t *testing.T) {
	path := filepath.Join(t.TempDir(), "test.ppk")
	if err := os.WriteFile(path, []byte(puttyV2TestKey), 0o600); err != nil {
		t.Fatal(err)
	}
	methods, err := buildAuthMethods(AuthConfig{Method: "private_key", PrivateKeyPath: path})
	if err != nil {
		t.Fatal(err)
	}
	if len(methods) != 1 || methods[0] == nil {
		t.Fatal("private-key authentication method is missing")
	}
}

func TestCancelEmitsAcknowledgementAndCancelsContext(t *testing.T) {
	var output bytes.Buffer
	worker := NewWorker(strings.NewReader(""), &output)
	ctx, cancel := context.WithCancel(context.Background())
	worker.jobs["job-test"] = &activeJob{cancel: cancel}

	if err := worker.cancelTransfer(InboundMessage{Type: "cancel", RequestID: "r2", JobID: "job-test"}); err != nil {
		t.Fatal(err)
	}
	if !errors.Is(ctx.Err(), context.Canceled) {
		t.Fatal("job context was not cancelled")
	}
	var msg OutboundMessage
	if err := json.Unmarshal(bytes.TrimSpace(output.Bytes()), &msg); err != nil {
		t.Fatal(err)
	}
	if msg.Type != "state" || msg.State != "cancel_requested" || msg.JobID != "job-test" {
		t.Fatalf("unexpected cancel response: %+v", msg)
	}
}

func TestWorkerDoesNotEchoInboundSecret(t *testing.T) {
	input := "{\"type\":\"unknown\",\"requestId\":\"r1\",\"transfer\":{\"remote\":{\"auth\":{\"method\":\"password\",\"password\":\"super-secret\"}}}}\n"
	var output bytes.Buffer
	worker := NewWorker(strings.NewReader(input), &output)
	if err := worker.Run(context.Background()); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(output.String(), "super-secret") {
		t.Fatal("worker output echoed a password")
	}
}

func TestWorkerAcceptsUTF8BOMOnFirstRecord(t *testing.T) {
	input := "\xef\xbb\xbf{\"type\":\"unknown\",\"requestId\":\"r1\"}\n"
	var output bytes.Buffer
	worker := NewWorker(strings.NewReader(input), &output)
	if err := worker.Run(context.Background()); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(output.String(), "invalid_json") {
		t.Fatalf("first record BOM was rejected: %s", output.String())
	}
}

func TestShellQuoteAndProtocolInsertion(t *testing.T) {
	if got := shellQuote("a'b"); got != `'a'"'"'b'` {
		t.Fatalf("shellQuote = %q", got)
	}
	args := forceProtocol([]string{"--server", "-r", ".", "/tmp/a b"})
	if len(args) < 2 || args[1] != "--protocol=27" {
		t.Fatalf("protocol not inserted after --server: %q", args)
	}
	command := "command rsync " + joinShellArgs(args)
	if strings.Contains(command, "password") {
		t.Fatal("remote rsync command unexpectedly contains authentication data")
	}
}

func TestBuildRsyncArgsIsAllowlisted(t *testing.T) {
	got := strings.Join(buildRsyncArgs(TransferOptions{
		PreserveTimes:       true,
		PreservePermissions: true,
		PreserveLinks:       true,
		Compress:            true,
	}), " ")
	if got != "-r -t -p -l -z" {
		t.Fatalf("args = %q", got)
	}
}

func TestRouteProbeRequiresPrivateKeysAndValidCandidates(t *testing.T) {
	req := RouteProbeRequest{
		FirstHop: validRequest().Remote,
		Target:   validRequest().Remote,
		Candidates: []RouteCandidate{
			{Host: "10.0.0.12", Port: 22, InterfaceName: "eno1"},
		},
	}
	if err := validateRouteProbeRequest(&req); err != nil {
		t.Fatalf("valid private-key route probe rejected: %v", err)
	}

	req.Target.Auth = AuthConfig{Method: "password", Password: "secret"}
	assertErrorCode(t, validateRouteProbeRequest(&req), "unsupported_authentication")

	req.Target = validRequest().Remote
	req.Candidates[0].Port = 0
	assertErrorCode(t, validateRouteProbeRequest(&req), "invalid_request")
}

func TestRemoteTransferAcceptsDiscoveredAddressOverrides(t *testing.T) {
	request := RemoteTransferRequest{
		SourcePath:              "/srv/source",
		DestinationPath:         "/srv/destination",
		ExecutionSide:           "source",
		Source:                  validRequest().Remote,
		Destination:             validRequest().Remote,
		DestinationTransferHost: "10.0.0.12",
		DestinationTransferPort: 22,
	}
	if err := validateRemoteTransferRequest(&request); err != nil {
		t.Fatalf("valid transfer override rejected: %v", err)
	}
	request.DestinationTransferHost = "bad\naddress"
	assertErrorCode(t, validateRemoteTransferRequest(&request), "invalid_request")
}

func TestProbeFingerprintParser(t *testing.T) {
	output := "[inner-host-key] 256 SHA256:abc123 host (ED25519)\n__SSH_WIN_GUI_ROUTE_OK__\n"
	if got := parseProbeFingerprint(output); got != "SHA256:abc123" {
		t.Fatalf("fingerprint = %q", got)
	}
}

func TestRouteCandidateKeepsSavedJumpOnlyWhenRequested(t *testing.T) {
	jump := validRequest().Remote
	jump.Host = "jump.example.test"
	target := validRequest().Remote
	target.Proxy = &ProxyConfig{Type: "jump", Jump: &jump}

	direct := targetForRouteCandidate(target, RouteCandidate{Host: "10.0.0.12", Port: 2222})
	if direct.Host != "10.0.0.12" || direct.Port != 2222 || direct.Proxy != nil {
		t.Fatalf("direct candidate did not replace target route: %#v", direct)
	}

	savedJump := targetForRouteCandidate(target, RouteCandidate{
		Host:           target.Host,
		Port:           target.Port,
		UseTargetProxy: true,
	})
	if savedJump.Host != target.Host || savedJump.Port != target.Port || savedJump.Proxy == nil || savedJump.Proxy.Jump == nil {
		t.Fatalf("saved jump candidate did not preserve target route: %#v", savedJump)
	}
}

func validRequest() TransferRequest {
	return TransferRequest{
		Direction:  "upload",
		LocalPath:  `C:\data`,
		RemotePath: "/srv/data",
		Remote: RemoteEndpoint{
			Host:    "example.test",
			Port:    22,
			User:    "user",
			Auth:    AuthConfig{Method: "private_key", PrivateKeyPath: `C:\test\id_ed25519`},
			HostKey: HostKeyConfig{Mode: "log_only"},
		},
	}
}

func assertErrorCode(t *testing.T, err error, want string) {
	t.Helper()
	if err == nil {
		t.Fatalf("expected error code %q", want)
	}
	if got := publicError(err).Code; got != want {
		t.Fatalf("error code = %q, want %q; err=%v", got, want, err)
	}
}

func contains(values []string, want string) bool {
	for _, value := range values {
		if value == want {
			return true
		}
	}
	return false
}
