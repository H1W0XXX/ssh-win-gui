# RsyncShell Go worker

This directory contains a headless, versioned NDJSON worker for local-to-remote
uploads and remote-to-local downloads. It uses the BSD-licensed
`github.com/gokrazy/rsync/rsyncclient` over `golang.org/x/crypto/ssh` and starts
the remote machine's `rsync --server` process. It never invokes local `ssh`,
`scp`, `tar`, or `rsync` executables and has no fallback transport.

## Wire protocol

The worker reads one UTF-8 JSON object per line from stdin and writes one JSON
object per line to stdout. Stdout is protocol-only; fatal worker diagnostics go
to stderr. The first stdout record is always `hello` with protocol version 2
and exact capabilities.

Transfer request:

```json
{"type":"transfer","requestId":"r1","transfer":{"direction":"upload","localPath":"D:\\data","remotePath":"/srv/data","copyContents":true,"remote":{"host":"server.example","port":22,"user":"alice","auth":{"method":"private_key","privateKeyPath":"C:\\Users\\alice\\.ssh\\id_ed25519"},"hostKey":{"mode":"log_only"}},"options":{"preserveTimes":true}}}
```

Cancel request:

```json
{"type":"cancel","requestId":"r2","jobId":"job-0123456789abcdef01234567"}
```

Events use `type` values `state`, `log`, `progress`, `completed`, and `error`.
Every accepted transfer first receives a `state=queued` record containing the
new `jobId`. Cancellation closes the active SSH session and ends with one
`completed` record whose state is `cancelled`.

`progress` reports SSH/rsync protocol bytes, not file completion percentage.
Final `stats.sourceSizeBytes` comes from the rsync library when available.

## Security contract

- `known_hosts` and exact `SHA256:...` host-key verification remain available.
  The desktop client deliberately requests `log_only`: the worker accepts the
  negotiated host key and emits its algorithm and SHA-256 fingerprint as a
  warning log event. This mode is vulnerable to active man-in-the-middle
  attacks and is intended only for the requested desktop behavior.
- `insecure`/ignore-host-key modes are rejected.
- Password and private-key authentication stay inside the Go SSH client.
  Passwords and passphrases are never placed in process arguments, the remote
  rsync command, or emitted events.
- Supported authentication methods are `password` and `private_key` (PEM,
  OpenSSH, or PuTTY PPK v2/v3 keys, optionally with `passphrase`). Pageant,
  ssh-agent, PuTTY named sessions, multi-factor keyboard-interactive flows, and
  SSH certificates are explicitly unsupported. Simple keyboard-interactive
  prompts reuse the supplied password.
- Only allowlisted rsync options are accepted. There is no arbitrary argument
  field.

## Compatibility and limits

- The pinned Go implementation speaks rsync protocol 27 and forces the remote
  server to that version. The remote host must provide `sh` and a compatible
  `rsync` executable.
- `compress=true` uses the vendored GNU-compatible zlib token stream and is the
  desktop client's default for uploads and downloads.
- `partial=true` is rejected because v0.3.3 parses the option but does not
  implement the expected partial-file lifecycle.
- `dryRun=true` is rejected because the upstream receiver writes its listing to
  process stdout, which would violate this worker's protocol-only NDJSON stream.
- `delete=true` is rejected because v0.3.3 does not reliably forward that option
  to a remote receiver. It will not claim success for an unverified deletion.
- ACLs, xattrs, owner/group/device preservation, daemon mode, remote-to-remote
  copies, jump hosts, and local-to-local copies are not exposed.
- `copyContents` is explicit so Windows callers do not need to encode directory
  semantics with a trailing backslash.
- Multiple jobs may run concurrently. Closing stdin cancels all active jobs and
  waits for their completion events before the worker exits.

## Development

```powershell
go test ./...
go build -trimpath -o .\artifacts\rsyncworker.exe .
```

Do not commit the built executable. See `THIRD_PARTY_NOTICES.md` for dependency
licenses and pinned versions.
