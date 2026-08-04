package main

import (
	"bytes"
	"context"
	"crypto/subtle"
	"errors"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/kayrus/putty"
	"golang.org/x/crypto/ssh"
	"golang.org/x/crypto/ssh/knownhosts"
	"golang.org/x/net/proxy"
)

const sshConnectTimeout = 15 * time.Second

func dialSSH(ctx context.Context, remote RemoteEndpoint, reporter *jobReporter) (*ssh.Client, error) {
	port := remote.Port
	if port == 0 {
		port = 22
	}
	address := net.JoinHostPort(remote.Host, fmt.Sprintf("%d", port))

	auth, err := buildAuthMethods(remote.Auth)
	if err != nil {
		return nil, err
	}
	hostKeyCallback, err := buildHostKeyCallback(remote.HostKey, func(algorithm, fingerprint string) {
		reporter.log("warning", fmt.Sprintf(
			"SSH host key NOT VERIFIED: algorithm=%s fingerprint=%s",
			algorithm,
			fingerprint))
	})
	if err != nil {
		return nil, err
	}
	config := &ssh.ClientConfig{
		User:            remote.User,
		Auth:            auth,
		HostKeyCallback: hostKeyCallback,
		Timeout:         sshConnectTimeout,
	}

	netConn, upstream, err := dialSSHTransport(ctx, remote, address, reporter)
	if err != nil {
		return nil, errorCode("network", fmt.Errorf("connect SSH endpoint: %w", err))
	}

	stopClose := make(chan struct{})
	go func() {
		select {
		case <-ctx.Done():
			_ = netConn.Close()
		case <-stopClose:
		}
	}()
	defer close(stopClose)

	_ = netConn.SetDeadline(time.Now().Add(sshConnectTimeout))
	clientConn, chans, reqs, err := ssh.NewClientConn(netConn, address, config)
	if err != nil {
		_ = netConn.Close()
		if upstream != nil {
			_ = upstream.Close()
		}
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return nil, errorCode("ssh_handshake", fmt.Errorf("SSH handshake or host-key verification failed: %w", err))
	}
	_ = netConn.SetDeadline(time.Time{})
	client := ssh.NewClient(clientConn, chans, reqs)
	if upstream != nil {
		go func() {
			_ = client.Wait()
			_ = upstream.Close()
		}()
	}
	return client, nil
}

func dialSSHTransport(ctx context.Context, remote RemoteEndpoint, address string, reporter *jobReporter) (net.Conn, *ssh.Client, error) {
	dialer := &net.Dialer{Timeout: sshConnectTimeout}
	if remote.Proxy == nil || remote.Proxy.Type == "" {
		conn, err := dialer.DialContext(ctx, "tcp", address)
		return conn, nil, err
	}
	switch remote.Proxy.Type {
	case "socks5":
		proxyAddress := net.JoinHostPort(remote.Proxy.Host, fmt.Sprintf("%d", remote.Proxy.Port))
		socksDialer, err := proxy.SOCKS5("tcp", proxyAddress, nil, dialer)
		if err != nil {
			return nil, nil, err
		}
		if contextDialer, ok := socksDialer.(proxy.ContextDialer); ok {
			conn, err := contextDialer.DialContext(ctx, "tcp", address)
			return conn, nil, err
		}
		conn, err := socksDialer.Dial("tcp", address)
		return conn, nil, err
	case "jump":
		if remote.Proxy.Jump == nil {
			return nil, nil, errors.New("jump proxy is missing its SSH endpoint")
		}
		jumpClient, err := dialSSH(ctx, *remote.Proxy.Jump, reporter)
		if err != nil {
			return nil, nil, fmt.Errorf("connect jump SSH: %w", err)
		}
		type result struct {
			conn net.Conn
			err  error
		}
		ready := make(chan result, 1)
		go func() {
			conn, dialErr := jumpClient.Dial("tcp", address)
			ready <- result{conn: conn, err: dialErr}
		}()
		select {
		case <-ctx.Done():
			_ = jumpClient.Close()
			return nil, nil, ctx.Err()
		case result := <-ready:
			if result.err != nil {
				_ = jumpClient.Close()
				return nil, nil, result.err
			}
			return result.conn, jumpClient, nil
		}
	default:
		return nil, nil, fmt.Errorf("unsupported proxy type %q", remote.Proxy.Type)
	}
}

func buildAuthMethods(auth AuthConfig) ([]ssh.AuthMethod, error) {
	switch auth.Method {
	case "password":
		if auth.Password == "" {
			return nil, errorCode("invalid_request", errors.New("password authentication requires a non-empty password"))
		}
		return []ssh.AuthMethod{
			ssh.Password(auth.Password),
			ssh.KeyboardInteractive(func(_ string, _ string, questions []string, _ []bool) ([]string, error) {
				answers := make([]string, len(questions))
				for index := range answers {
					answers[index] = auth.Password
				}
				return answers, nil
			}),
		}, nil
	case "private_key":
		privateKey, err := parsePrivateKeyObject(auth)
		if err != nil {
			return nil, err
		}
		signer, err := ssh.NewSignerFromKey(privateKey)
		if err != nil {
			return nil, errorCode("authentication", fmt.Errorf("create private-key signer: %w", err))
		}
		return []ssh.AuthMethod{ssh.PublicKeys(signer)}, nil
	case "agent", "keyboard_interactive", "certificate", "putty_session":
		return nil, errorCode("unsupported_authentication", fmt.Errorf("authentication method %q is unsupported", auth.Method))
	default:
		return nil, errorCode("unsupported_authentication", fmt.Errorf("authentication method %q is unsupported", auth.Method))
	}
}

func parsePrivateKeyObject(auth AuthConfig) (any, error) {
	if auth.PrivateKeyPath == "" {
		return nil, errorCode("invalid_request", errors.New("private_key authentication requires privateKeyPath"))
	}
	keyBytes, err := os.ReadFile(auth.PrivateKeyPath)
	if err != nil {
		return nil, errorCode("authentication", fmt.Errorf("read private key: %w", err))
	}
	defer clearBytes(keyBytes)
	if bytes.HasPrefix(bytes.TrimSpace(keyBytes), []byte("PuTTY-User-Key-File-")) {
		puttyKey, parseErr := putty.New(keyBytes)
		if parseErr != nil {
			return nil, errorCode("authentication", fmt.Errorf("parse PuTTY private key: %w", parseErr))
		}
		passphrase := []byte(auth.Passphrase)
		defer clearBytes(passphrase)
		privateKey, parseErr := puttyKey.ParseRawPrivateKey(passphrase)
		if parseErr != nil {
			return nil, errorCode("authentication", fmt.Errorf("decrypt PuTTY private key: %w", parseErr))
		}
		return privateKey, nil
	}
	if auth.Passphrase == "" {
		privateKey, parseErr := ssh.ParseRawPrivateKey(keyBytes)
		if parseErr != nil {
			return nil, errorCode("authentication", fmt.Errorf("parse private key: %w", parseErr))
		}
		return privateKey, nil
	}
	passphrase := []byte(auth.Passphrase)
	defer clearBytes(passphrase)
	privateKey, parseErr := ssh.ParseRawPrivateKeyWithPassphrase(keyBytes, passphrase)
	if parseErr != nil {
		return nil, errorCode("authentication", fmt.Errorf("decrypt private key: %w", parseErr))
	}
	return privateKey, nil
}

func buildHostKeyCallback(
	cfg HostKeyConfig,
	logFingerprint func(algorithm, fingerprint string),
) (ssh.HostKeyCallback, error) {
	mode := cfg.Mode
	if mode == "" {
		mode = "known_hosts"
	}
	switch mode {
	case "known_hosts":
		path := cfg.KnownHostsPath
		if path == "" {
			home, err := os.UserHomeDir()
			if err != nil {
				return nil, errorCode("host_key", fmt.Errorf("resolve home for default known_hosts: %w", err))
			}
			path = filepath.Join(home, ".ssh", "known_hosts")
		}
		if _, err := os.Stat(path); err != nil {
			return nil, errorCode("host_key", fmt.Errorf("known_hosts file is required: %w", err))
		}
		callback, err := knownhosts.New(path)
		if err != nil {
			return nil, errorCode("host_key", fmt.Errorf("load known_hosts: %w", err))
		}
		return callback, nil
	case "sha256":
		expected := append([]string(nil), cfg.SHA256Fingerprints...)
		if strings.TrimSpace(cfg.SHA256) != "" {
			expected = append(expected, cfg.SHA256)
		}
		if len(expected) == 0 {
			return nil, errorCode("invalid_request", errors.New("sha256 host-key mode requires at least one OpenSSH SHA256:... fingerprint"))
		}
		for index := range expected {
			expected[index] = strings.TrimSpace(expected[index])
			if !strings.HasPrefix(expected[index], "SHA256:") || len(expected[index]) <= len("SHA256:") {
				return nil, errorCode("invalid_request", errors.New("sha256 host-key mode requires OpenSSH SHA256:... fingerprints"))
			}
		}
		return func(_ string, _ net.Addr, key ssh.PublicKey) error {
			actual := ssh.FingerprintSHA256(key)
			matched := 0
			for _, fingerprint := range expected {
				matched |= subtle.ConstantTimeCompare([]byte(actual), []byte(fingerprint))
			}
			if matched != 1 {
				return errorCode("host_key", fmt.Errorf("host key fingerprint mismatch: got %s", actual))
			}
			return nil
		}, nil
	case "log_only":
		if logFingerprint == nil {
			return nil, errorCode("invalid_request", errors.New("log_only host-key mode requires a fingerprint logger"))
		}
		return func(_ string, _ net.Addr, key ssh.PublicKey) error {
			logFingerprint(key.Type(), ssh.FingerprintSHA256(key))
			return nil
		}, nil
	case "insecure", "ignore":
		return nil, errorCode("unsupported_host_key_policy", errors.New("insecure host-key verification is not supported"))
	default:
		return nil, errorCode("unsupported_host_key_policy", fmt.Errorf("host-key mode %q is unsupported", mode))
	}
}

func clearBytes(value []byte) {
	for i := range value {
		value[i] = 0
	}
}
