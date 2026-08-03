# Architecture

## Process and lifetime boundaries

`ssh-win-gui` is the only UI process. Each terminal tab owns an SSH.NET
`SshClient` and `ShellStream`, connected to a Windows Terminal WPF renderer via
`ITerminalConnection`. Terminal input is serialized through a channel; output
uses one UTF-8 decoder so a multi-byte character split across SSH packets is not
corrupted. PTY size changes flow back through `ChangeWindowSize`.
Keyboard messages are translated by Microsoft's native Terminal control, which
tracks application-cursor and other VT keyboard modes used by `vi`, `less` and
`tmux`. The application does not replace arrow, navigation or paging keys with
hard-coded escape strings. WPF attempts to use arrow and tab keys for focus
traversal, so the host consumes only that routed action and sends the original
Win32 virtual-key message back to Terminal's HWND. Its terminal HWND hook is
otherwise limited to clipboard mouse actions and alternate-screen mouse-wheel
adaptation; wheel detents are also sent back as native cursor-key messages so
the same Terminal input engine still owns VT key encoding.
Every queued input chunk is explicitly flushed after `ShellStream.WriteAsync`;
SSH.NET otherwise keeps short interactive writes in its internal stream buffer
instead of sending them to the SSH channel. A regression test covers UTF-8 input
and the required flush.

The browser deliberately does not borrow the shell's channel. Each refresh:

1. creates its own SSH.NET client with the same in-memory credentials;
2. verifies the same persisted host key;
3. executes a bounded `python3` JSON listing command;
4. disposes the connection after parsing the marked response.

If this request times out while SSH remains connected, existing rows stay visible
and the user can retry. A terminal disconnect cancels the tab's browser request,
increments its listing generation and clears its rows, so a late response cannot
restore stale data. Every tab owns its own browser collection, path and request;
tab switching only rebinds that state. Closing or refreshing the browser therefore
cannot tear down a live shell.
The remote scan stops at 5,000 entries and refuses a JSON payload above 8 MiB.
The desktop drains stdout and stderr concurrently while the SSH command runs;
crossing the 8 MiB stdout or 16 KiB stderr boundary cancels the command before
SSH.NET can accumulate unbounded output. The parser rechecks size, entry count
and cancellation, then swaps one new collection into a recycling/virtualized
list instead of raising one UI event per entry. A capped listing is reported in
the browser status bar.

Each transfer starts one `rsyncworker.exe`, and the desktop tracks all active
workers instead of imposing a process-wide single-transfer lock. Multi-selection
starts concurrent jobs; switching or closing a terminal tab does not rebind a job
to another tab. The worker sends a protocol-v2 hello,
accepts one transfer request over stdin, emits structured state/protocol-byte
events on stdout, and receives cancellation over the same NDJSON stream. Closing
stdin cancels active work; the desktop app kills only its own worker after a
bounded shutdown grace period.

SSH tunnels are application-level jobs rather than terminal-tab children. Direct
and upstream-SOCKS routes use SSH.NET forwarded ports; direct and multi-hop SSH
routes use Tmds.Ssh forwarding over the existing `direct-tcpip` proxy chain.
Remote dynamic SOCKS5 combines a remote SSH listener with an in-process no-auth
SOCKS5 handshake and opens each requested destination through the same SSH
client. Closing the manager window leaves jobs running; stopping a job or closing
the main window cancels its listener and disposes only that job's SSH client.

Only the active terminal tab owns a live monitoring connection. Switching tabs,
disconnecting SSH or closing the tab cancels and disposes it. A small Python
probe reads cumulative CPU counters from `/proc/stat`, available memory from
`/proc/meminfo`, per-interface byte counters from `/sys/class/net`, selectable
mounted-disk capacity from `/proc/self/mountinfo` plus `statvfs`, and optional NVIDIA data through the argument-array
form of `nvidia-smi`. CPU and network rates are calculated from consecutive
samples in the desktop process; `top` is never used. Transient failures are
logged once and retried without affecting the shell or file browser.

## Security boundary

- Quick-connect accepts endpoint data only, never inline passwords.
- `sessions.json` contains endpoint metadata and optional key paths, but no
  passwords or passphrases.
- Session opening is non-modal: a configured private key is attempted directly.
  Key loading or authentication failure swaps the terminal control to a local,
  non-echoing password prompt; Enter replaces that prompt with a fresh password
  SSH connection. The password remains only in the open tab.
- SSH.NET host-key events are denied unless the fingerprint is already trusted
  or the user explicitly accepts the native prompt. Key changes are distinct
  from first use.
- The desktop requests the worker's explicit `log_only` host-key policy for
  rsync transfers. The worker accepts the negotiated key without verifying it
  and emits its algorithm and SHA-256 fingerprint as a warning event shown in
  the selectable transfer log. This intentionally does not protect rsync
  transfers from an active man-in-the-middle attack. Terminal and browser
  connections remain verified. Secrets travel through an inherited stdin pipe,
  not process arguments, environment variables or logs.
- Remote paths are shell-quoted, rsync options are allowlisted, and no arbitrary
  `extraArgs` field crosses the IPC boundary.

## Session and language persistence

Saved connection profiles are atomically replaced in
`%APPDATA%\RsyncShell\sessions.json`. They contain endpoint metadata, grouping,
favorite state and an optional private-key path, but no password or passphrase.
Quick Connect is intentionally transient; only the session editor writes a new
profile. The selected `en` or `zh-CN` UI language is stored separately in
`settings.json`. Both resource dictionaries are checked for key parity, and the
publisher rejects/removes satellite-resource languages outside that pair.

## Terminal control supply chain

The WPF adapter source is in Microsoft's MIT-licensed Windows Terminal
repository under `src/cascadia/WpfTerminalControl`. The current development
package `CI.Microsoft.Terminal.Wpf 1.25.260303002` declares upstream commit
`9ae724aa5b080aafbeea2bbf88db630b182cc802`; its managed and native DLLs carry
valid Microsoft Authenticode signatures. The downloaded nupkg used during
development had SHA-256
`3E639A019607432552F8BB3385B51D065FF8CB19608D4C7E17528C9481A10545`.

The NuGet owner is not Microsoft, so a release must reproduce the build from the
declared official commit, pin the native artifacts by hash, and retain the MIT
license. The adapter remains behind `ITerminalConnection` so this provenance
change does not touch SSH or UI state.

## Rsync worker boundary

`RsyncShell.RsyncWorker` depends on the in-repository BSD-3-Clause
`third_party/rsync` snapshot and Apache-2.0 `kayrus/putty v1.0.5` for PuTTY PPK
v2/v3 decoding, plus `golang.org/x/crypto`. The snapshot contains the
GNU-compatible compressed sender/receiver token path and Windows destination
replacement fixes; it does not copy the unlicensed outer application code from
the old `D:\go\rsyncgui` project. See its vendor note, license and third-party
notices for provenance and limitations.
