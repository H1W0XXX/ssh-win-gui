# Third-party notices

The worker source is MIT-licensed. Its Go dependency graph is version-locked in
`go.mod` and `go.sum`. The worker uses the BSD-3-Clause rsync source snapshot in
`third_party/rsync`; no code from the unlicensed outer `rsyncgui` application is
included.

| Module | Pinned version | License |
|---|---:|---|
| github.com/gokrazy/rsync | vendored snapshot based on v0.3.3 development history | BSD-3-Clause |
| github.com/kayrus/putty | v1.0.5 | Apache-2.0 |
| golang.org/x/crypto | v0.46.0 | BSD-3-Clause |
| github.com/BurntSushi/toml | v1.6.0 | MIT |
| github.com/coreos/go-systemd | v0.0.0-20191104093116-d3cd4ed1dbcf | Apache-2.0 |
| github.com/google/renameio/v2 | v2.0.2 | Apache-2.0 |
| github.com/google/shlex | v0.0.0-20191202100458-e7afc7fbc510 | Apache-2.0 |
| github.com/landlock-lsm/go-landlock | v0.0.0-20250303204525-1544bccde3a3 | MIT |
| github.com/mmcloughlin/md4 | v0.1.2 | BSD-3-Clause |
| golang.org/x/sync | v0.19.0 | BSD-3-Clause |
| golang.org/x/sys | v0.39.0 | BSD-3-Clause |
| kernel.org/pub/linux/libs/security/libcap/psx | v1.2.70 | BSD-3-Clause OR GPL-2.0-only; this project selects BSD-3-Clause |

`gokrazy/rsync` copyright notice:

> Copyright (c) 2021 the gokrazy authors. All rights reserved.

The BSD-3-Clause modules permit redistribution in source and binary forms when
their copyright notices, conditions, and warranty disclaimers are retained.
Apache-2.0 and MIT notices must likewise accompany redistributed binaries.
Before release, generate the complete attribution bundle from the locked module
graph and ship it beside the worker executable; the canonical license texts are
stored in each module's root directory in the Go module cache.

The remote GNU rsync process is not linked into or distributed with this
worker. If a product later bundles a GNU rsync binary, that binary's GPL
obligations must be handled separately.
