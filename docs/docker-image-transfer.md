# Remote Docker image transfer

Open **Machine transfer**, then choose **Docker image transfer** at the top
right. Select source and destination SSH sessions, choose A → B or B → A,
and select one or more source image tags (up to 200). Refresh buttons reload
the Docker inventory; the search field filters repository/tag names.

Use **Discover routes** and select a successful route, just as for remote file
transfer. Both execution directions, discovered interface addresses, saved
jump routes, and the existing first-hop SOCKS5 transport are reused. In Docker
mode the route probe checks Docker access instead of requiring rsync.
Agent forwarding and the existing transfer host-key policy are unchanged.

The optional **zstd** checkbox compresses the `docker image save` stream with
`zstd -T0 -3 -c` and decompresses it on the receiving machine before
`docker image load`. Both hosts need Bash; both need zstd when enabled.
No image archive is stored on Windows or in remote temporary directories.
Tags are transferred sequentially; shared layers may be sent more than once.
There is no registry push, container restart, daemon reconfiguration, or image
deletion. Progress shows cumulative payload bytes and live speed every 500 ms (compressed
bytes when zstd is enabled), including 0 B/s during stalls. The execution host
needs Python 3 for the streaming meter. Stream EOF changes the status to waiting
for Docker load; success still requires ID verification. There is no percentage:
Docker image size is not the size of the compressed save archive.

**Hide Kubernetes images** is enabled initially. It hides common Kubernetes
infrastructure repository prefixes and image references reported by Docker
containers with the `io.kubernetes.pod.name` label. This is a reversible display
filter, not a complete classification of all cluster images. Disable it to show
all tagged Docker images. Images in containerd/CRI-only stores and dangling
images without a repository/tag are outside this feature.

Before starting, the app reloads both inventories, compares exact
`repository:tag` references against the full, unfiltered destination inventory,
and asks before importing a tag already present. The prompt distinguishes
equal and different IDs. A different ID means Docker load can repoint that tag.
The worker rechecks the observed source and destination IDs immediately before
save/load; if a tag changed since confirmation, refresh and confirm again.
After load, it checks the destination tag's ID against the source. Docker does
not provide an atomic compare-and-load transaction: avoid other tools changing
these tags during a transfer. Cancellation/failure may leave imported layers
or tags; refresh to inspect them. Nothing is automatically removed.

Docker access is attempted as the SSH user first, then through `sudo -n`.
If sudo needs a password or sudoers denies the command, a dialog asks the user
to configure passwordless sudo for Docker and retry. No sudo password is
requested, stored, piped into stdin, or placed in a command line. An unavailable
Docker daemon is reported separately. sudo permission to run Docker grants
powerful host access; configure it according to the host's administration policy.

Implementation uses worker protocol v4's additional `docker_list` and
`docker_transfer` capabilities. Ship the updated app and rsyncworker together;
older workers are rejected by capability checks before transfer.

References: [Docker image save](https://docs.docker.com/reference/cli/docker/image/save/),
[Docker image load](https://docs.docker.com/reference/cli/docker/image/load/),
[Docker image ls](https://docs.docker.com/reference/cli/docker/image/ls/).
