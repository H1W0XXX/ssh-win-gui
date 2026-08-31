package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"strconv"
	"strings"

	"golang.org/x/crypto/ssh/agent"
)

func validateRemoteTransferRequest(req *RemoteTransferRequest) error {
	if strings.TrimSpace(req.SourcePath) == "" || strings.TrimSpace(req.DestinationPath) == "" {
		return errorCode("invalid_request", errors.New("sourcePath and destinationPath are required"))
	}
	for _, path := range []string{req.SourcePath, req.DestinationPath} {
		if !strings.HasPrefix(path, "/") || strings.ContainsAny(path, "\x00\r\n") {
			return errorCode("invalid_request", errors.New("remote-to-remote paths must be absolute POSIX paths without NUL or line breaks"))
		}
	}
	switch req.ExecutionSide {
	case "", "auto", "source", "destination":
	default:
		return errorCode("invalid_request", fmt.Errorf("executionSide must be auto, source, or destination, got %q", req.ExecutionSide))
	}
	if err := validateTransferOptions(req.Options); err != nil {
		return err
	}
	for _, override := range []struct {
		host string
		port int
	}{
		{req.SourceTransferHost, req.SourceTransferPort},
		{req.DestinationTransferHost, req.DestinationTransferPort},
	} {
		if strings.ContainsAny(override.host, "\x00\r\n") || override.port < 0 || override.port > 65535 {
			return errorCode("invalid_request", errors.New("transfer address override is invalid"))
		}
	}
	if err := validateRemoteEndpoint(req.Source); err != nil {
		return fmt.Errorf("source endpoint: %w", err)
	}
	if err := validateRemoteEndpoint(req.Destination); err != nil {
		return fmt.Errorf("destination endpoint: %w", err)
	}
	return nil
}

func validateRemoteEndpoint(remote RemoteEndpoint) error {
	if strings.TrimSpace(remote.Host) == "" || strings.TrimSpace(remote.User) == "" {
		return errorCode("invalid_request", errors.New("remote host and user are required"))
	}
	if remote.Port < 0 || remote.Port > 65535 {
		return errorCode("invalid_request", errors.New("remote port must be between 1 and 65535, or 0 for port 22"))
	}
	if err := validateSecurityConfig(remote); err != nil {
		return err
	}
	if remote.Proxy != nil && remote.Proxy.Type == "jump" {
		if remote.Proxy.Jump == nil {
			return errorCode("invalid_request", errors.New("jump proxy is missing its SSH endpoint"))
		}
		return validateRemoteEndpoint(*remote.Proxy.Jump)
	}
	return nil
}

func runRemoteTransfer(ctx context.Context, req RemoteTransferRequest, reporter *jobReporter) (*TransferStat, error) {
	execIsSource := req.ExecutionSide != "destination"
	execEndpoint := req.Source
	innerEndpoint := req.Destination
	if !execIsSource {
		execEndpoint = req.Destination
		innerEndpoint = req.Source
	}
	if execIsSource && strings.TrimSpace(req.DestinationTransferHost) != "" {
		innerEndpoint.Host = req.DestinationTransferHost
		innerEndpoint.Port = req.DestinationTransferPort
		innerEndpoint.Proxy = nil
	}
	if !execIsSource && strings.TrimSpace(req.SourceTransferHost) != "" {
		innerEndpoint.Host = req.SourceTransferHost
		innerEndpoint.Port = req.SourceTransferPort
		innerEndpoint.Proxy = nil
	}

	reporter.state("connecting")
	reporter.log("info", fmt.Sprintf("remote-to-remote first hop: %s", endpointLabel(execEndpoint)))
	execClient, err := dialSSH(ctx, execEndpoint, reporter)
	if err != nil {
		return nil, err
	}
	defer execClient.Close()
	reporter.log("warning", "SSH first hop connected without host-key verification")

	keyring, keyCount, err := buildForwardedKeyring(innerEndpoint)
	if err != nil {
		return nil, err
	}
	if keyCount == 0 {
		return nil, errorCode("unsupported_authentication", errors.New("remote-to-remote inner SSH requires a private-key session"))
	}
	if err := agent.ForwardToAgent(execClient, keyring); err != nil {
		return nil, errorCode("ssh_agent", fmt.Errorf("forward private keys to first hop: %w", err))
	}

	session, err := execClient.NewSession()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("create first-hop SSH session: %w", err))
	}
	defer session.Close()
	if err := agent.RequestAgentForwarding(session); err != nil {
		return nil, errorCode("ssh_agent", fmt.Errorf("request SSH agent forwarding: %w", err))
	}

	innerSSH, err := buildInnerSSHCommand(innerEndpoint)
	if err != nil {
		return nil, err
	}
	args := buildRemoteRsyncArgs(req.Options)
	if !hasRsyncProgressOption(args) {
		args = append(args, "--info=progress2")
	}
	args = append(args, "--protect-args", "-e", innerSSH)

	sourcePath := req.SourcePath
	if req.CopyContents {
		sourcePath = ensureRemoteTrailingSlash(sourcePath)
	}
	var sourceSpec, destinationSpec string
	if execIsSource {
		sourceSpec = sourcePath
		destinationSpec = formatRsyncRemoteSpec(innerEndpoint, req.DestinationPath)
	} else {
		sourceSpec = formatRsyncRemoteSpec(innerEndpoint, sourcePath)
		destinationSpec = req.DestinationPath
	}
	args = append(args, sourceSpec, destinationSpec)

	fingerprintCommand := buildInnerFingerprintCommand(innerEndpoint)
	command := fingerprintCommand + "\nexec rsync " + joinShellArgs(args)
	reporter.log("info", "remote-to-remote command: rsync "+joinShellArgs(redactRemoteArgs(args)))

	stdout, err := session.StdoutPipe()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("open first-hop stdout: %w", err))
	}
	stderr, err := session.StderrPipe()
	if err != nil {
		return nil, errorCode("ssh_session", fmt.Errorf("open first-hop stderr: %w", err))
	}
	stdoutWriter := &rsyncLogWriter{reporter: reporter, level: "info", parseProgress: true}
	stderrWriter := &rsyncLogWriter{reporter: reporter, level: "error", parseProgress: true}
	stdoutDone := make(chan struct{})
	stderrDone := make(chan struct{})
	go func() {
		_, _ = io.Copy(stdoutWriter, stdout)
		stdoutWriter.flush()
		close(stdoutDone)
	}()
	go func() {
		_, _ = io.Copy(stderrWriter, stderr)
		stderrWriter.flush()
		close(stderrDone)
	}()

	reporter.state("transferring")
	if err := session.Start("sh -c " + shellQuote(command)); err != nil {
		return nil, errorCode("remote_rsync", fmt.Errorf("start remote-to-remote rsync: %w", err))
	}
	cancelWatchDone := make(chan struct{})
	go func() {
		select {
		case <-ctx.Done():
			_ = session.Close()
		case <-cancelWatchDone:
		}
	}()
	waitErr := session.Wait()
	close(cancelWatchDone)
	<-stdoutDone
	<-stderrDone
	if ctx.Err() != nil {
		return &TransferStat{}, ctx.Err()
	}
	if waitErr != nil {
		return &TransferStat{}, errorCode("remote_rsync", fmt.Errorf("remote-to-remote rsync exited unsuccessfully: %w", waitErr))
	}
	return &TransferStat{}, nil
}

func buildForwardedKeyring(endpoint RemoteEndpoint) (agent.Agent, int, error) {
	keyring := agent.NewKeyring()
	count := 0
	for current := &endpoint; current != nil; {
		if current.Auth.Method != "private_key" {
			return nil, 0, errorCode("unsupported_authentication", fmt.Errorf(
				"inner SSH endpoint %s requires private-key authentication; password forwarding is not supported",
				endpointLabel(*current)))
		}
		privateKey, err := parsePrivateKeyObject(current.Auth)
		if err != nil {
			return nil, 0, err
		}
		if err := keyring.Add(agent.AddedKey{PrivateKey: privateKey}); err != nil {
			return nil, 0, errorCode("ssh_agent", fmt.Errorf("add private key for %s: %w", endpointLabel(*current), err))
		}
		count++
		if current.Proxy == nil || current.Proxy.Type != "jump" {
			break
		}
		current = current.Proxy.Jump
	}
	return keyring, count, nil
}

func buildRemoteRsyncArgs(options TransferOptions) []string {
	args := buildRsyncArgs(options)
	if options.Delete {
		args = append(args, "--delete")
	}
	if options.DryRun {
		args = append(args, "--dry-run")
	}
	if options.Partial {
		args = append(args, "--partial")
	}
	return args
}

func hasRsyncProgressOption(args []string) bool {
	for _, arg := range args {
		if arg == "--progress" || arg == "-P" ||
			(strings.HasPrefix(arg, "--info=") && strings.Contains(arg, "progress")) {
			return true
		}
	}
	return false
}

func buildInnerSSHCommand(endpoint RemoteEndpoint) (string, error) {
	if endpoint.Auth.Method != "private_key" {
		return "", errorCode("unsupported_authentication", errors.New("remote-to-remote inner SSH requires private-key authentication"))
	}
	args := buildInnerSSHBaseArgs(endpoint.Port)
	if endpoint.Proxy != nil {
		switch endpoint.Proxy.Type {
		case "", "none":
		case "socks5":
			return "", errorCode("unsupported_proxy", errors.New("an inner SOCKS5 proxy is not reachable from the first-hop machine"))
		case "jump":
			if endpoint.Proxy.Jump == nil {
				return "", errorCode("invalid_request", errors.New("jump proxy is missing its SSH endpoint"))
			}
			proxyCommand, err := buildJumpProxyCommand(*endpoint.Proxy.Jump)
			if err != nil {
				return "", err
			}
			args = append(args, "-o", "ProxyCommand="+proxyCommand)
		default:
			return "", errorCode("unsupported_proxy", fmt.Errorf("unsupported inner proxy type %q", endpoint.Proxy.Type))
		}
	}
	return joinShellArgs(args), nil
}

func buildInnerSSHBaseArgs(port int) []string {
	if port == 0 {
		port = 22
	}
	return []string{
		"ssh",
		"-p", strconv.Itoa(port),
		"-o", "BatchMode=yes",
		"-o", "PreferredAuthentications=publickey",
		"-o", "ConnectTimeout=15",
		"-o", "ServerAliveInterval=25",
		"-o", "ServerAliveCountMax=3",
		"-o", "StrictHostKeyChecking=no",
		"-o", "UserKnownHostsFile=/dev/null",
		"-o", "LogLevel=ERROR",
	}
}

func buildJumpProxyCommand(endpoint RemoteEndpoint) (string, error) {
	if endpoint.Auth.Method != "private_key" {
		return "", errorCode("unsupported_authentication", errors.New("remote-to-remote jump SSH requires private-key authentication"))
	}
	args := buildInnerSSHBaseArgs(endpoint.Port)
	if endpoint.Proxy != nil {
		switch endpoint.Proxy.Type {
		case "", "none":
		case "socks5":
			return "", errorCode("unsupported_proxy", errors.New("an inner SOCKS5 proxy is not reachable from the first-hop machine"))
		case "jump":
			if endpoint.Proxy.Jump == nil {
				return "", errorCode("invalid_request", errors.New("jump proxy is missing its SSH endpoint"))
			}
			proxyCommand, err := buildJumpProxyCommand(*endpoint.Proxy.Jump)
			if err != nil {
				return "", err
			}
			args = append(args, "-o", "ProxyCommand="+proxyCommand)
		default:
			return "", errorCode("unsupported_proxy", fmt.Errorf("unsupported inner proxy type %q", endpoint.Proxy.Type))
		}
	}
	args = append(args, "-W", "%h:%p", formatSshDestination(endpoint))
	return joinShellArgs(args), nil
}

func buildProxyJumpChain(endpoint RemoteEndpoint) ([]string, error) {
	var endpoints []RemoteEndpoint
	current := endpoint.Proxy
	for current != nil && current.Type == "jump" {
		if current.Jump == nil {
			return nil, errorCode("invalid_request", errors.New("jump proxy is missing its SSH endpoint"))
		}
		endpoints = append(endpoints, *current.Jump)
		current = current.Jump.Proxy
	}
	if current != nil && current.Type == "socks5" {
		return nil, errorCode("unsupported_proxy", errors.New("an inner SOCKS5 proxy is not reachable from the first-hop machine"))
	}
	result := make([]string, 0, len(endpoints))
	for index := len(endpoints) - 1; index >= 0; index-- {
		result = append(result, formatProxyJumpSpec(endpoints[index]))
	}
	return result, nil
}

func buildInnerFingerprintCommand(endpoint RemoteEndpoint) string {
	port := endpoint.Port
	if port == 0 {
		port = 22
	}
	return "if command -v ssh-keyscan >/dev/null 2>&1 && command -v ssh-keygen >/dev/null 2>&1; then " +
		"ssh-keyscan -T 5 -p " + strconv.Itoa(port) + " " + shellQuote(endpoint.Host) +
		" 2>/dev/null | ssh-keygen -lf - -E sha256 2>/dev/null | sed 's/^/[inner-host-key] /' || true; fi"
}

func endpointLabel(endpoint RemoteEndpoint) string {
	port := endpoint.Port
	if port == 0 {
		port = 22
	}
	return net.JoinHostPort(endpoint.User+"@"+endpoint.Host, strconv.Itoa(port))
}

func formatRsyncRemoteSpec(endpoint RemoteEndpoint, path string) string {
	host := endpoint.Host
	if strings.Contains(host, ":") && !strings.HasPrefix(host, "[") {
		host = "[" + host + "]"
	}
	return endpoint.User + "@" + host + ":" + path
}

func formatProxyJumpSpec(endpoint RemoteEndpoint) string {
	port := endpoint.Port
	if port == 0 {
		port = 22
	}
	host := endpoint.Host
	if strings.Contains(host, ":") && !strings.HasPrefix(host, "[") {
		host = "[" + host + "]"
	}
	return endpoint.User + "@" + host + ":" + strconv.Itoa(port)
}

func redactRemoteArgs(args []string) []string {
	return append([]string(nil), args...)
}
