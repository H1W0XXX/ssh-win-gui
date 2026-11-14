# Codex SSH MCP integration

ssh-win-gui ships a local STDIO MCP server that lets Codex and other
MCP-compatible agents reuse the sessions configured in the desktop app. It is a
self-contained executable; it does not open an HTTP port and it exits when its
MCP client closes stdin.

## Install globally for Codex

Publish or install ssh-win-gui, then register the MCP executable from the stable
installation directory:

```powershell
codex mcp add ssh_win_gui -- D:\ssh-win-gui-win-x64\tools\mcp\ssh-win-gui-mcp.exe
```

Long-running SSH commands also need a client-side tool timeout longer than the
server's maximum 600-second command timeout. The resulting user-level section
in `%USERPROFILE%\.codex\config.toml` should be:

```toml
[mcp_servers.ssh_win_gui]
command = 'D:\ssh-win-gui-win-x64\tools\mcp\ssh-win-gui-mcp.exe'
tool_timeout_sec = 620
```

Restart Codex App after adding or changing the global MCP configuration. Check
the registration without connecting to a remote host:

```powershell
codex mcp get ssh_win_gui
codex mcp list
```

## Tools

### `list_sessions`

Arguments: none.

Returns the saved session ID, name, group, endpoint, and route kind. It never
returns private-key paths or credentials. Use the returned ID when saved names
are duplicated; otherwise an exact saved name is accepted by the other tools.

### `run_script`

Arguments:

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `script` | string | required | UTF-8 POSIX shell script sent verbatim to remote `sh -s`. |
| `timeoutSeconds` | integer | `60` | Execution timeout from 1 to 600 seconds. |
| `maxOutputBytes` | integer | `16384` | Bytes retained separately for stdout and stderr, from 1024 to 65536. |
| `outputMode` | string | `head_tail` | Which part of over-long output to keep: `head_tail` (first and last halves), `tail`, or `head`. |
| `grep` | string | none | .NET regular expression; only matching stdout/stderr lines are kept, before the byte limit applies. Use `(?i)` to ignore case. |

The result contains the session ID/name, exit code, stdout, stderr, elapsed
milliseconds, and separate truncation flags for stdout and stderr. Output above
the requested limits is drained so the SSH process can finish, but it is not
returned to the model; an `[... N bytes omitted ...]` line marks where it was
cut.

`outputMode` and `grep` are applied by the MCP server, not by the remote shell,
so the reported exit code is still the script's own. Piping through `tail` or
`grep` on the remote side would report the pipe's exit code instead. Use `tail`
for builds and test runs, whose errors are at the end.

Use `set -eu` when the task should fail on the first unsuccessful command or
unset variable. For example, an agent can call `run_script` with:

```sh
set -eu
uname -a
df -h --output=source,size,used,avail,pcent,target | sed -n '1,12p'
```

The script is an MCP JSON argument and then SSH stdin. It is not embedded in a
PowerShell command line, so shell variables, quotes, command substitutions,
multiline Python heredocs, and pipelines survive the local Windows boundary.

### `run_script_multi`

Runs one script on several sessions in parallel. Arguments match `run_script`,
except that `sessions` is an array of 1 to 32 session IDs or exact names, and
`maxOutputBytes` defaults to `4096` per session with a maximum of `16384`.

Every name is resolved before any host is contacted, so a misspelled session
runs nothing. The result has one group per distinct outcome: sessions whose
exit code, stdout, and stderr are identical are listed together and their
output appears once. A session that cannot be reached is reported in a group
with an `error` message; it does not fail the other sessions.

### `read_file`

Reads a range of lines from a remote text file.

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `path` | string | required | Remote path, absolute or starting with `~/`. |
| `offset` | integer | `1` | 1-based line to start from. |
| `limit` | integer | `400` | Maximum number of lines, from 1 to 5000. |
| `maxBytes` | integer | `32768` | Maximum content bytes, from 1024 to 262144. |
| `lineNumbers` | boolean | `true` | Prefix each line with its number and a tab. |

The result is plain text. Its first line states the range returned, the file's
total line count, and the `offset` to continue from when more remains, for
example `[/srv/app/main.py: lines 1-400 of 950; continue with offset=401]`.

### `edit_file`

Replaces an exact string in a remote text file without resending the file.

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `path` | string | required | Remote path, absolute or starting with `~/`. |
| `oldString` | string | required | Exact text to replace, including indentation. |
| `newString` | string | required | Replacement text; empty deletes `oldString`. |
| `replaceAll` | boolean | `false` | Replace every occurrence instead of requiring a unique match. |

The edit fails, and nothing is written, when `oldString` is not found or when
it matches more than once without `replaceAll`; the error lists the matching
line numbers. A file with CRLF line endings accepts `oldString` written with
plain LF and keeps its CRLF endings.

The new content is written to a temporary file beside the original and renamed
over it, so readers never see a half-written file. The file keeps its mode, a
symbolic link stays a link, and the write is refused if the file changed after
it was read. When the directory is not writable the file is rewritten in
place instead. Files larger than 8 MiB are refused.

### `write_file`

Creates a remote text file from a string, creating parent directories as needed.

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `path` | string | required | Remote path, absolute or starting with `~/`. |
| `content` | string | required | Complete file content, written verbatim as UTF-8. |
| `overwrite` | boolean | `false` | Permit replacing an existing file. |

Use `edit_file` to change part of an existing file and `rsync_upload` for
binary or large files. The three file tools need only POSIX `sh` and core
utilities on the remote host; they do not require Python.

### `start_job`

Starts a script as a detached background job and returns at once. Use it for
builds, tests, training runs, or downloads that may exceed the 600-second
limit of `run_script`.

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `script` | string | required | POSIX shell script run by remote `sh` from the home directory. |
| `name` | string | `job` | Label used as the job ID prefix: 1 to 24 letters, digits, `_` or `-`. |

The result contains the `jobId` and the remote `logPath`. The job keeps running
after the SSH connection closes. Its script, merged stdout/stderr log, PID, and
exit status live in `~/.cache/ssh-win-gui-mcp/jobs/<jobId>/` on the remote host
(`$XDG_CACHE_HOME` is honoured). Job directories are never removed
automatically; delete them with `run_script` when they are no longer needed.

### `job_status`

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `jobId` | string | none | Job to report. Omit to list the 20 most recent jobs without log output. |
| `sinceOffset` | integer | `0` | Log byte offset to read from. |
| `maxOutputBytes` | integer | `8192` | Maximum log bytes to return, from 0 to 65536. |
| `outputMode` | string | `head_tail` | Which part of an over-long range to keep: `head_tail`, `tail`, or `head`. |

Each job reports `state`, `exitCode`, `logBytes`, and `elapsedSeconds`. The
state is `running`, `exited`, `stopped` (ended by `stop_job`), or `lost` (the
process is gone without an exit status, for example after a reboot).

The result's `nextOffset` is the log size at the time of the call. Passing it
back as `sinceOffset` returns only the output written since, which keeps
polling cheap. The log slice is cut on the remote host, so a very large log
costs no more than a small one. The log is an ordinary file; use `run_script`
to search it.

### `stop_job`

Arguments: `session` and `jobId`. Sends `SIGTERM` to the job's process group
and `SIGKILL` if it is still alive 5 seconds later, then returns the job's
state. A job that already finished is left unchanged.

Jobs get their own process group through `setsid`, so the whole process tree
is stopped. On hosts without `setsid` (macOS, for example) only the job's
shell and its direct children are signalled.

### `rsync_upload`

Uploads a local file or directory through the bundled Go rsync worker. It
reuses the selected session's private key and SOCKS5 or SSH jump chain.
Compression is enabled, and SSH fingerprints are included in the bounded
result log.

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `localSourcePath` | string | required | Absolute path of an existing local file or directory. |
| `remoteDestinationPath` | string | required | Exact absolute remote destination path, including the desired final name. |
| `overwrite` | boolean | `false` | Permit replacement of an existing file or merging into an existing directory of the same type. |
| `timeoutSeconds` | integer | `600` | Transfer timeout from 1 to 600 seconds. |

### `rsync_download`

Downloads a remote file or directory through the bundled Go rsync worker.
Authentication, routing, compression, and fingerprint logging are identical
to `rsync_upload`.

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `session` | string | required | Session ID from `list_sessions`, or an exact saved name. |
| `remoteSourcePath` | string | required | Absolute path of an existing remote file or directory. |
| `localDestinationPath` | string | required | Exact absolute local destination path, including the desired final name. Its parent must exist. |
| `overwrite` | boolean | `false` | Permit replacement of an existing file or merging into an existing directory of the same type. |
| `timeoutSeconds` | integer | `600` | Transfer timeout from 1 to 600 seconds. |

Both tools accept exact destination paths, so an agent can select any writable
location and rename the source during transfer. Files are replaced only when
`overwrite=true`. Directories are recursive; when the exact destination
directory already exists, `overwrite=true` merges into it and replaces
same-name files without deleting unrelated destination files. A file is never
replaced by a directory, or vice versa.

Results include source type, exact local/remote paths, optional single-file
size, rsync protocol progress bytes, overwrite/compression modes, a bounded
worker log, and elapsed milliseconds. File contents are carried by rsync and
never embedded in MCP JSON or model context.

The rsync worker follows the desktop transfer policy: host fingerprints are
logged but are not used to reject a transfer. `run_script` remains strict and
rejects unknown or changed host keys.

## Connection reuse

The server keeps one authenticated SSH connection, and therefore one TCP
stream, per session. `run_script`, `run_script_multi`, the file tools, and the
job tools each open a new exec channel on that connection instead of
connecting again, so a call after the first skips the TCP, key-exchange, and
authentication round trips.

- At most 6 calls run at once on one connection; further calls wait their
  turn. OpenSSH's default `MaxSessions` is 10. A server configured with a
  lower `MaxSessions` refuses the extra channels; the pool then lowers its own
  limit for that connection and queues the calls on it rather than opening
  another connection.
- A connection is closed after 10 minutes without use. Set the
  `SSH_WIN_GUI_MCP_IDLE_SECONDS` environment variable for the MCP process to
  change this, from `0` (close after every call) to `3600`.
- At most 64 connections are kept. The least recently used idle connection is
  closed to make room; a call fails only if all 64 are busy.
- A connection that dropped while idle is replaced before the next call. A
  script is never sent twice: only a failure to open the channel is retried.
- Editing a session in the GUI changes its connection key, so the next call
  connects with the new settings.
- A session reached through a jump host holds its own connection to that jump
  host. Sessions that share a jump host do not share its connection.

The host key is checked when the connection is opened. A fingerprint revoked
in the GUI takes effect for a session when its current connection closes.

The rsync tools do not use the pool; the rsync worker opens its own connection
for each transfer.

## Agent usage rules

For a request such as “use the B300 session to inspect GPU processes,” the
preferred flow is:

1. Call `list_sessions` if the exact saved session is uncertain.
2. Select one session by ID/name; do not guess from a partial name.
3. Call `run_script` with a narrowly scoped read-only script first.
4. Summarize the structured result. If output was truncated, rerun a narrower
   query instead of immediately raising the output limit.
5. Run mutating commands only when they are within the user's explicit scope.

To reduce model context usage:

- Read source with `read_file` and change it with `edit_file` instead of
  printing whole files or rewriting them through a heredoc.
- Let `outputMode` and `grep` select output rather than raising
  `maxOutputBytes`.
- Use `run_script_multi` when the same command goes to several hosts.
- Start anything long with `start_job` and poll `job_status` with
  `sinceOffset`.
- Prefer commands such as `journalctl --since/--until -n`, `find -maxdepth`,
  `docker ps --filter`, and machine-readable summaries. Avoid printing entire
  logs, recursive trees, large JSON documents, model manifests, or binary data.

## Security and authentication

- Profiles are reloaded from `%APPDATA%\RsyncShell\sessions.json` on each tool
  call, so GUI edits are visible without copying configuration into Codex.
- Only a saved private key is used. Password authentication and interactive
  private-key passphrase prompts are intentionally unavailable to the MCP
  process.
- Direct, unauthenticated SOCKS5, and saved SSH jump-host routes reuse the same
  profile model as the GUI.
- Host keys must already be trusted in
  `%APPDATA%\RsyncShell\known_hosts.json`. Unknown, additional-algorithm, or
  changed keys are rejected; connect in the GUI and review the fingerprint
  first.
- Private-key paths are omitted from tool results and redacted from returned
  exception messages.
- `run_script`, `run_script_multi`, `edit_file`, `write_file`, `start_job`, and
  `stop_job` are advertised as destructive/open-world MCP tools because they
  can change a remote machine. The tool annotation is a safety signal, not
  evidence that every invocation is mutating.
- All of these tools, and `read_file` and `job_status`, use the same strict
  host-key check as `run_script`.
- Error messages raised by the tools are returned to the model as written, so
  it can correct a bad argument or a failed edit. They never contain
  private-key paths.

## Common failures

- **Tool missing in a new task:** verify `codex mcp get ssh_win_gui`, confirm the
  executable path exists, and restart Codex App.
- **Unknown or changed host key:** connect once through ssh-win-gui and approve
  only after verifying the displayed SHA-256 fingerprint.
- **No saved private key:** edit the GUI session and select a private key. The
  MCP server will not ask the model for a password.
- **Encrypted key fails:** use a non-interactive authentication method supported
  by the current app; agent/Pageant and passphrase storage are not implemented.
- **Timed out:** narrow the operation, increase `timeoutSeconds` up to 600, or
  run it with `start_job`.
- **Output truncated:** choose `outputMode`, add `grep`, or page the remote
  output; raise `maxOutputBytes` only when the additional content is necessary.
- **`oldString` not found:** the text must match byte for byte. Read the lines
  again with `read_file` and copy them without the line-number prefix.
- **Job state `lost`:** the job's process ended without recording an exit
  status, usually because the host restarted. Its log is still readable.
- **rsync worker missing:** publish/copy the complete application directory so
  `tools\mcp\ssh-win-gui-mcp.exe` and `tools\rsync\rsyncworker.exe` remain in
  their packaged sibling directories.

## Build and package

The MCP project is `src\RsyncShell.Mcp\RsyncShell.Mcp.csproj`. The normal
publisher builds it as a self-contained single file under
`artifacts\publish\ssh-win-gui-win-x64\tools\mcp` and includes it in
`SHA256SUMS.txt`:

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1 -Configuration Release -NoZip
```

The publish workflow runs the Go checks, .NET build, and all tests before
atomically replacing the package directory.
