# ssh-win-gui

ssh-win-gui is a new Windows-native SSH workspace with the compact workflow that
makes MobaXterm useful: saved sessions, terminal tabs, a remote browser beside
the active shell, and a visible transfer queue. It has its own
name, implementation and assets; it does not patch or redistribute MobaXterm.

Saved sessions can be created, edited, grouped, favorited and deleted. Endpoint
metadata and an optional private-key path are stored in
`%APPDATA%\RsyncShell\sessions.json`; passwords and key passphrases are never
saved. The UI supports only English and Simplified Chinese, selectable from
Settings > Language.

Terminal text is copied automatically when a left-button selection completes.
When tmux or another application has enabled mouse reporting, hold Shift while
dragging to make a local selection. Choose middle-button or right-button paste
under Settings > Mouse paste; the choice is saved with the language setting.
MobaXterm-style keyword highlighting is enabled by default and can be toggled
under Settings. It colors success/true terms green, failure/false terms red and
warning terms yellow, while preserving foreground colors already supplied by
the remote program. Settings > Keyword highlighting > Customize keywords opens
a three-column editor for literal words or phrases; changes are persisted and
apply to terminals that are already open.

Settings > Export settings writes `ssh-win-gui-settings.json` to a folder chosen
by the user. The file contains saved session structure and UI preferences, but
never passwords, private-key files, key passphrases, or private-key paths.
Settings > Import settings reads that file from a selected folder and replaces
the saved-session list after confirmation; open terminals stay connected.

## Implemented architecture

- .NET 8 WPF owns the window, session tree, tabs, browser and transfer UI.
- The top-level Tunnels command starts and manages local forwarding (`-L`),
  remote forwarding (`-R`), local SOCKS5 (`-D`) and remote SOCKS5 (`-R dynamic`)
  against saved sessions. Tunnels keep running when their manager window closes,
  have independent stop controls and expose a selectable/copyable log.
- Microsoft's Windows Terminal WPF control renders VT/ANSI output; SSH.NET owns
  the SSH session, PTY, resize events and 25-second keepalives.
- The file browser opens a separate bounded SSH.NET connection per refresh. A
  stale browser request can fail or reconnect without closing the terminal tab.
  Browser path, rows and request lifetime belong to one terminal tab; switching
  tabs never reuses another tab's rows. If that tab's SSH terminal disconnects,
  its directory request is cancelled and its rows are cleared immediately.
  Listings are capped at 5,000 entries and 8 MiB, then replaced in the virtualized
  file view as one batch so an abnormally large directory cannot exhaust the UI.
- Upload and download use the headless Go `rsyncworker.exe`. It speaks NDJSON to
  the desktop app and Go-native rsync protocol 27 over SSH. There is no
  SCP/SFTP/tar fallback. Transfers are independent jobs: selections and separate
  tabs can upload/download concurrently, while the transfer log prefixes every
  event with a task number and session name.
- The compact monitor below the active terminal samples Linux `/proc/stat`,
  `/proc/meminfo`, `/sys/class/net`, `/proc/self/mountinfo` and `statvfs` every
  two seconds; it never invokes `top`. The default-route interface and mounted
  disk can both be changed in the monitor. If `nvidia-smi` is present, a second compact row shows
  each GPU's core utilization and used/total VRAM, including eight-GPU hosts.
- File and folder transfers enable rsync zlib compression by default. Folder
  upload has its own toolbar/menu command. Before a transfer merges an existing
  folder or replaces a same-name file, the desktop asks the user to continue or
  cancel. Downloads write a temporary file inside the selected destination
  directory and atomically replace the final file only after validation.
- Passwords and key passphrases live only in the open tab and worker stdin. They
  are never written to `sessions.json`, command-line arguments or transfer logs.
- Opening a session never shows a separate authentication dialog. A configured
  private key is tried immediately; a missing, unreadable, encrypted, or rejected
  key falls back to a masked password prompt inside the terminal. Sessions with
  no key start at that terminal password prompt.
- Terminal and remote-browser host keys are trust-on-first-use with an explicit
  SHA-256 prompt. Accepted fingerprints are stored in
  `%APPDATA%\RsyncShell\known_hosts.json`; changed keys produce a blocking
  warning. Per user configuration, rsync transfer connections do not verify the
  host key; their negotiated algorithm and SHA-256 fingerprint are written to
  the selectable transfer log instead.

## Build and run

Requirements are Windows 10 2004 or newer, x64, .NET 8 SDK and Go 1.25 or newer.
The remote host needs `python3` for directory listing and `sh` plus a compatible
`rsync` for transfers.

```powershell
Push-Location .\src\RsyncShell.RsyncWorker
go test ./...
go build -trimpath -o ..\..\tools\rsync\rsyncworker.exe .
Pop-Location

dotnet restore .\RsyncShell.sln
dotnet build .\RsyncShell.sln
dotnet run --project .\src\ssh-win-gui\ssh-win-gui.csproj -- ubuntu@example.com:22
```

For a clean self-contained x64 package, run the PowerShell 7 publisher:

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1
```

The script limits Go to 8 logical workers and MSBuild to 4 nodes, runs Go
tests/vet plus the .NET build and tests, then atomically replaces the fixed
`artifacts\publish\ssh-win-gui-win-x64` directory and matching `.zip`. It does
not create timestamped package directories. The package includes SHA-256
checksums and only English/Simplified-Chinese UI resources.

The worker binary is deliberately ignored by Git. The app loads it only from
its own `tools\rsync` directory; it does not search environment overrides,
`PATH`, or the current directory because worker stdin contains the active tab's
credentials.

If a virtual display driver or GPU composition issue makes the WPF window blank,
start with `--software-rendering`. Unhandled UI/runtime diagnostics are appended
under `%LOCALAPPDATA%\RsyncShell\logs` without suppressing the original crash.

## Current boundary

- Password and unencrypted OpenSSH, PEM, and PuTTY PPK v2/v3 private-key
  authentication are supported by the desktop flow. A key that needs a
  passphrase falls back to terminal password authentication rather than opening
  a passphrase dialog.
  Single-response keyboard-interactive prompts reuse the entered password;
  multi-factor/OTP prompt flows are not implemented yet.
  Windows Credential Manager and agent/Pageant are not implemented. Saved
  sessions support unauthenticated SOCKS5 and another saved private-key session
  as an SSH jump host; jump routing uses SSH direct-tcpip chaining rather than a
  local forwarded port.
- Tunnels use the same saved direct, unauthenticated SOCKS5-upstream and SSH
  jump routes as sessions. Remote dynamic SOCKS5 works for direct and SSH-jump
  routes; nesting remote dynamic SOCKS5 through an upstream SOCKS5 proxy is
  rejected with a logged error.
- Go rsync currently exposes recursive copy, zlib compression, times,
  permissions and links. Partial files, delete, dry-run, ACLs and xattrs are
  rejected instead of being silently ignored or corrupting the worker protocol
  stream.
- A directory with more than 5,000 visible entries shows a clear capped-listing
  warning. Enter a narrower absolute path to reach entries outside that first
  bounded scan; paged enumeration is not implemented yet.
- `CI.Microsoft.Terminal.Wpf` is a signed development-time repack of Microsoft's
  MIT-licensed source. Before a release, pin an official Windows Terminal commit
  and reproduce the native DLL build; see [architecture](docs/architecture.md).
