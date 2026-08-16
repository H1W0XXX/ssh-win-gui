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

The result contains the session ID/name, exit code, stdout, stderr, elapsed
milliseconds, and separate truncation flags for stdout and stderr. Output above
the requested limits is drained so the SSH process can finish, but it is not
returned to the model.

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

## Agent usage rules

For a request such as “use the B300 session to inspect GPU processes,” the
preferred flow is:

1. Call `list_sessions` if the exact saved session is uncertain.
2. Select one session by ID/name; do not guess from a partial name.
3. Call `run_script` with a narrowly scoped read-only script first.
4. Summarize the structured result. If output was truncated, rerun a narrower
   query instead of immediately raising the output limit.
5. Run mutating commands only when they are within the user's explicit scope.

To reduce model context usage, prefer commands such as `sed -n`, `tail -n`,
`journalctl --since/--until -n`, `find -maxdepth`, `docker ps --filter`, and
machine-readable summaries. Avoid printing entire logs, recursive trees, large
JSON documents, model manifests, or binary data.

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
- `run_script` is advertised as a destructive/open-world MCP tool because the
  supplied script can change a remote machine. The tool annotation is a safety
  signal, not evidence that every invocation is mutating.

## Common failures

- **Tool missing in a new task:** verify `codex mcp get ssh_win_gui`, confirm the
  executable path exists, and restart Codex App.
- **Unknown or changed host key:** connect once through ssh-win-gui and approve
  only after verifying the displayed SHA-256 fingerprint.
- **No saved private key:** edit the GUI session and select a private key. The
  MCP server will not ask the model for a password.
- **Encrypted key fails:** use a non-interactive authentication method supported
  by the current app; agent/Pageant and passphrase storage are not implemented.
- **Timed out:** narrow the operation or increase `timeoutSeconds` up to 600.
- **Output truncated:** filter or page the remote output; raise
  `maxOutputBytes` only when the additional content is necessary.
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
