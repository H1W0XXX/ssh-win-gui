package main

import (
	"bytes"
	"context"
	"encoding/json"
	"os/exec"
	"strings"
	"testing"
	"time"
)

func TestDockerProgressParsesFragmentedLinesAndKeepsDiagnostics(t *testing.T) {
	var output, diagnostic bytes.Buffer
	w := &dockerProgressWriter{reporter: &jobReporter{jobID: "test", out: newEmitter(&output)}, fallback: &diagnostic, offset: 1000}
	text := "warning from Docker\n" + dockerProgressPrefix + "stream 512 256\n" + dockerProgressPrefix + "stream 1024 0\n" + dockerProgressPrefix + "import 1024 0\n" + dockerVerifyMarker + "\n"
	for _, c := range []byte(text) {
		_, _ = w.Write([]byte{c})
	}
	w.flush()
	lines := strings.Split(strings.TrimSpace(output.String()), "\n")
	if len(lines) != 4 {
		t.Fatalf("events: %s", output.String())
	}
	for i, line := range lines {
		var msg OutboundMessage
		if err := json.Unmarshal([]byte(line), &msg); err != nil {
			t.Fatal(err)
		}
		want := int64(2024)
		if i == 0 {
			want = 1512
		}
		if msg.Transferred != want || msg.ProtocolWritten != want {
			t.Fatalf("wrong cumulative progress: %+v", msg)
		}
		if strings.Contains(line, "percent") {
			t.Fatal("must not invent percentage for unknown stream size")
		}
	}
	if !strings.Contains(lines[2], "docker_import") || !strings.Contains(lines[3], "docker_verify") {
		t.Fatal("missing stages")
	}
	if diagnostic.String() != "warning from Docker\n" {
		t.Fatalf("diagnostics polluted by progress: %s", diagnostic.String())
	}
}

func TestDockerProgressRejectsInvalidAndRegressingCounters(t *testing.T) {
	var output, diagnostic bytes.Buffer
	w := &dockerProgressWriter{reporter: &jobReporter{out: newEmitter(&output)}, fallback: &diagnostic, transferred: 20}
	for _, line := range []string{"stream -1 2", "stream 19 2", "stream 20 -1", "stream x 2", "other 20 2", "stream 9223372036854775808 2"} {
		_, _ = w.Write([]byte(dockerProgressPrefix + line + "\n"))
	}
	if output.Len() != 0 || diagnostic.Len() == 0 {
		t.Fatal("invalid counters must stay diagnostics")
	}
}

func TestDockerMeterPreservesBinaryAndReportsStalledProgress(t *testing.T) {
	python, err := exec.LookPath("python3")
	if err != nil {
		t.Skip("python3 unavailable")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, python, "-u", "-c", dockerMeterScript)
	input, err := cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	payload := bytes.Repeat([]byte{0, 1, 10, 13, 26, 127, 128, 255}, 131072)
	done := make(chan error, 1)
	go func() {
		_, err := input.Write(payload)
		if err == nil {
			time.Sleep(1200 * time.Millisecond)
		}
		_ = input.Close()
		done <- err
	}()
	if err := cmd.Wait(); err != nil {
		t.Fatalf("%v: %s", err, stderr.String())
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(payload, stdout.Bytes()) {
		t.Fatalf("meter corrupted binary stream: want %d got %d", len(payload), stdout.Len())
	}
	if strings.Count(stderr.String(), dockerProgressPrefix) < 4 || !strings.Contains(stderr.String(), "import 1048576 0") {
		t.Fatalf("missing periodic and final progress: %s", stderr.String())
	}
	if !strings.Contains(stderr.String(), "stream 1048576 0") {
		t.Fatalf("stall must show zero speed: %s", stderr.String())
	}
}
