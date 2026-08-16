# RsyncShell vendor snapshot

This BSD-3-Clause source snapshot was copied from the rsync implementation in
`D:\go\rsyncgui\third_party\rsync` without its Git metadata. The copied tree was
at commit `17b1a7abc26a28d95fb31cbc859442fef88e6e11`, which includes commit
`7f265275b6189bce04176a7168a92caf3a913f31` for GNU-compatible sender-side zlib
compression and Windows path handling. The source worktree also contained the
receiver-side zlib implementation and tests required for compressed downloads.

RsyncShell additionally closes the Windows basis-file handle before committing
a received file and uses `golang.org/x/sys/windows.Rename` so an existing target
can be atomically replaced. `receiverrenameio_windows_test.go` covers that
behavior.

RsyncShell also preserves trailing-slash content-copy semantics for absolute
Windows sender paths and provides an exact single-file receiver destination
mode. The latter writes directly to the requested filename rather than treating
it as a directory.

The upstream copyright and BSD-3-Clause license are retained in `LICENSE`.
