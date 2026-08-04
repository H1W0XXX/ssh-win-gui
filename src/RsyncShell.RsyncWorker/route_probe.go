package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"golang.org/x/crypto/ssh"
	"golang.org/x/crypto/ssh/agent"
)

const routeProbeOutputLimit = 32 * 1024

func validateRouteProbeRequest(req *RouteProbeRequest) error {
	if len(req.Candidates) == 0 || len(req.Candidates) > 32 {
		return errorCode("invalid_request", errors.New("route probe requires between 1 and 32 candidates"))
	}
	if err := validateRemoteEndpoint(req.FirstHop); err != nil {
		return fmt.Errorf("first-hop endpoint: %w", err)
	}
	if err := validateRemoteEndpoint(req.Target); err != nil {
		return fmt.Errorf("target endpoint: %w", err)
	}
	if err := requirePrivateKeyRoute(req.FirstHop); err != nil {
		return fmt.Errorf("first-hop endpoint: %w", err)
	}
	if err := requirePrivateKeyRoute(req.Target); err != nil {
		return fmt.Errorf("target endpoint: %w", err)
	}
	for _, candidate := range req.Candidates {
		if strings.TrimSpace(candidate.Host) == "" || strings.ContainsAny(candidate.Host, "\x00\r\n") {
			return errorCode("invalid_request", errors.New("route candidate host is invalid"))
		}
		if candidate.Port < 1 || candidate.Port > 65535 {
			return errorCode("invalid_request", errors.New("route candidate port must be between 1 and 65535"))
		}
		if len(candidate.InterfaceName) > 128 || strings.ContainsAny(candidate.InterfaceName, "\x00\r\n") {
			return errorCode("invalid_request", errors.New("route candidate interface name is invalid"))
		}
	}
	return nil
}

func requirePrivateKeyRoute(endpoint RemoteEndpoint) error {
	for current := &endpoint; current != nil; {
		if current.Auth.Method != "private_key" {
			return errorCode("unsupported_authentication", fmt.Errorf(
				"machine-to-machine transfer requires private-key authentication for %s",
				endpointLabel(*current)))
		}
		if current.Proxy == nil || current.Proxy.Type != "jump" {
			return nil
		}
		current = current.Proxy.Jump
	}
	return nil
}

func runRouteProbe(ctx context.Context, req RouteProbeRequest, reporter *jobReporter) (*TransferStat, error) {
	reporter.state("connecting")
	firstHop, err := dialSSH(ctx, req.FirstHop, reporter)
	if err != nil {
		return nil, err
	}
	defer firstHop.Close()
	reporter.log("warning", "SSH first hop connected without host-key verification")

	directTarget := req.Target
	directTarget.Proxy = nil
	keyring, _, err := buildForwardedKeyring(directTarget)
	if err != nil {
		return nil, err
	}
	if err := agent.ForwardToAgent(firstHop, keyring); err != nil {
		return nil, errorCode("ssh_agent", fmt.Errorf("forward target key to first hop: %w", err))
	}
	agentSession, err := firstHop.NewSession()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("create first-hop agent session: %w", err))
	}
	defer agentSession.Close()
	if err := agent.RequestAgentForwarding(agentSession); err != nil {
		return nil, errorCode("ssh_agent", fmt.Errorf("request SSH agent forwarding: %w", err))
	}

	reporter.state("probing")
	probeContext, cancel := context.WithCancel(ctx)
	defer cancel()
	semaphore := make(chan struct{}, 4)
	var wait sync.WaitGroup
	for _, candidate := range req.Candidates {
		candidate := candidate
		wait.Add(1)
		go func() {
			defer wait.Done()
			select {
			case semaphore <- struct{}{}:
				defer func() { <-semaphore }()
			case <-probeContext.Done():
				return
			}
			result := probeRouteCandidate(probeContext, firstHop, directTarget, candidate)
			reporter.probe(result)
		}()
	}
	wait.Wait()
	if ctx.Err() != nil {
		return &TransferStat{}, ctx.Err()
	}
	return &TransferStat{}, nil
}

func probeRouteCandidate(
	ctx context.Context,
	firstHop interface{ NewSession() (*ssh.Session, error) },
	target RemoteEndpoint,
	candidate RouteCandidate,
) RouteProbeResult {
	started := time.Now()
	result := RouteProbeResult{
		Host:            candidate.Host,
		Port:            candidate.Port,
		InterfaceName:   candidate.InterfaceName,
		IsSavedEndpoint: candidate.IsSavedEndpoint,
	}
	candidateContext, cancel := context.WithTimeout(ctx, 9*time.Second)
	defer cancel()
	target.Host = candidate.Host
	target.Port = candidate.Port
	target.Proxy = nil

	session, err := firstHop.NewSession()
	if err != nil {
		result.Message = "create first-hop session: " + err.Error()
		return result
	}
	defer session.Close()
	innerSSH, err := buildInnerSSHCommand(target)
	if err != nil {
		result.Message = err.Error()
		return result
	}
	remoteCheck := "printf '__SSH_WIN_GUI_ROUTE_OK__\\n'; " +
		"if command -v rsync >/dev/null 2>&1; then printf '__RSYNC_OK__\\n'; else printf '__RSYNC_MISSING__\\n'; exit 74; fi"
	destination := formatSshDestination(target)
	command := buildInnerFingerprintCommand(target) + "\n" +
		innerSSH + " " + shellQuote(destination) + " " + shellQuote(remoteCheck)

	stdout := &boundedBuffer{limit: routeProbeOutputLimit}
	stderr := &boundedBuffer{limit: routeProbeOutputLimit}
	session.Stdout = stdout
	session.Stderr = stderr
	if err := session.Start("sh -c " + shellQuote(command)); err != nil {
		result.Message = "start inner SSH probe: " + err.Error()
		return result
	}
	done := make(chan error, 1)
	go func() { done <- session.Wait() }()
	select {
	case err = <-done:
	case <-candidateContext.Done():
		_ = session.Close()
		err = candidateContext.Err()
	}
	result.LatencyMilliseconds = time.Since(started).Milliseconds()
	output := stdout.String()
	result.Fingerprint = parseProbeFingerprint(output)
	if err == nil && strings.Contains(output, "__SSH_WIN_GUI_ROUTE_OK__") && strings.Contains(output, "__RSYNC_OK__") {
		result.Success = true
		result.Message = "SSH and rsync are ready"
		return result
	}
	message := strings.TrimSpace(stderr.String())
	if message == "" {
		message = strings.TrimSpace(strings.ReplaceAll(strings.ReplaceAll(output, "__SSH_WIN_GUI_ROUTE_OK__", ""), "__RSYNC_MISSING__", "rsync is missing"))
	}
	if message == "" && err != nil {
		message = err.Error()
	}
	if len(message) > 2048 {
		message = message[:2048] + "..."
	}
	result.Message = message
	return result
}

func parseProbeFingerprint(output string) string {
	for _, line := range strings.Split(output, "\n") {
		if !strings.Contains(line, "[inner-host-key]") {
			continue
		}
		for _, field := range strings.Fields(line) {
			if strings.HasPrefix(field, "SHA256:") {
				return field
			}
		}
	}
	return ""
}

func formatSshDestination(endpoint RemoteEndpoint) string {
	host := endpoint.Host
	if strings.Contains(host, ":") && !strings.HasPrefix(host, "[") {
		host = "[" + host + "]"
	}
	return endpoint.User + "@" + host
}

type boundedBuffer struct {
	buffer bytes.Buffer
	limit  int
}

func (b *boundedBuffer) Write(value []byte) (int, error) {
	written := len(value)
	remaining := b.limit - b.buffer.Len()
	if remaining > 0 {
		_, _ = b.buffer.Write(value[:min(remaining, len(value))])
	}
	return written, nil
}

func (b *boundedBuffer) String() string { return b.buffer.String() }
