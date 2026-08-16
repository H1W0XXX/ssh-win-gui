# Agent guidance

## Remote SSH operations

When a task needs a host already configured in ssh-win-gui, prefer the global
`ssh_win_gui` MCP server over launching `ssh.exe` through PowerShell.

1. Call `list_sessions` when the session ID or exact saved name is not already
   known.
2. Call `run_script` with that ID/name and a POSIX shell script. The tool sends
   the script directly to remote `sh -s` over SSH stdin, so local PowerShell
   quoting cannot alter `$`, quotes, backticks, newlines, or pipelines.
   For file or recursive-directory transfer, use `rsync_upload` or
   `rsync_download` instead of embedding file bytes in a script. Both tools take
   exact source and destination paths, support renaming, and default to refusing
   overwrite or directory merge unless `overwrite=true` is explicit.
3. Keep output narrow. Inspect a bounded range, tail, summary, or filtered data
   instead of printing large logs or files. Increase `maxOutputBytes` only when
   the task needs it.
4. Treat remote changes as external mutations. Inspect first and stay within the
   user's stated authorization; using MCP does not broaden permission to restart,
   delete, install, or reconfigure remote systems.
5. Never request, print, or copy private-key paths, passwords, or passphrases.
   The MCP server intentionally supports only saved private-key sessions and
   rejects unknown or changed host keys.
   The rsync transfer tools follow the desktop transfer policy and log host
   fingerprints without rejecting them; mention this distinction for
   security-sensitive transfers.

If the tools are unavailable, read [docs/codex-mcp.md](docs/codex-mcp.md) and
report the missing global MCP configuration or required Codex restart. Do not
silently fall back to a fragile PowerShell-escaped SSH command for a configured
session.
