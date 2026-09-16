package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"regexp"
	"strings"
	"time"

	"golang.org/x/crypto/ssh"
)

type DockerImage struct {
	Repository   string `json:"repository"`
	Tag          string `json:"tag"`
	ID           string `json:"id"`
	Size         string `json:"size"`
	IsKubernetes bool   `json:"isKubernetes"`
}

type DockerImageSelection struct {
	Reference     string `json:"reference"`
	SourceID      string `json:"sourceId"`
	DestinationID string `json:"destinationId"`
}

type DockerTransferOptions struct {
	Images []DockerImageSelection `json:"images"`
	Zstd   bool                   `json:"zstd"`
}

// Never read a sudo password from the image stream. Test the actual Docker
// command, not sudo -n true (which may have a different sudoers policy).
const dockerAccessScript = `export LC_ALL=C
command -v docker >/dev/null 2>&1 || { echo 'DOCKER_MISSING: docker is not installed' >&2; exit 71; }
if docker image ls -q >/dev/null 2>&1; then
 d() { docker "$@"; }
else
 command -v sudo >/dev/null 2>&1 || { echo 'SUDO_REQUIRED: configure passwordless sudo for Docker' >&2; exit 72; }
 if sudo_error=$(sudo -n docker image ls -q 2>&1 >/dev/null); then
  d() { sudo -n docker "$@"; }
 else
  case "$sudo_error" in
   *'password'*|*'terminal'*|*'not allowed'*|*'sudoers'*) echo 'SUDO_REQUIRED: configure passwordless sudo for Docker' >&2;;
   *) printf 'DOCKER_UNAVAILABLE: %s\n' "$sudo_error" >&2;;
  esac
  exit 72
 fi
fi
`

const dockerListScript = dockerAccessScript + `d image ls --no-trunc --format '{{json .}}' || exit $?
printf '__K8S_CONTAINERS__\n'
d container ls -a --filter label=io.kubernetes.pod.name --format '{{.Image}}' || exit $?
`

var dockerReferencePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._/:-]{0,511}$`)
var dockerIDPattern = regexp.MustCompile(`^sha256:[a-f0-9]{64}$`)

func validateDockerTransfer(options *DockerTransferOptions) error {
	if len(options.Images) == 0 || len(options.Images) > 200 {
		return errorCode("invalid_request", errors.New("select between 1 and 200 Docker image tags"))
	}
	seen := map[string]bool{}
	for _, image := range options.Images {
		if !dockerReferencePattern.MatchString(image.Reference) || strings.LastIndex(image.Reference, ":") <= strings.LastIndex(image.Reference, "/") || !dockerIDPattern.MatchString(image.SourceID) || (image.DestinationID != "" && !dockerIDPattern.MatchString(image.DestinationID)) || seen[image.Reference] {
			return errorCode("invalid_request", errors.New("invalid or duplicate Docker image reference / image ID"))
		}
		seen[image.Reference] = true
	}
	return nil
}

func isKubernetesRepository(repository string) bool {
	r := strings.ToLower(repository)
	for _, prefix := range []string{"registry.k8s.io/", "k8s.gcr.io/", "gcr.io/google_containers/", "registry.aliyuncs.com/google_containers/", "registry.cn-hangzhou.aliyuncs.com/google_containers/", "docker.io/rancher/", "rancher/", "docker.io/calico/", "calico/", "quay.io/calico/", "quay.io/cilium/", "quay.io/coreos/", "flannel/", "docker.io/flannel/"} {
		if strings.HasPrefix(r, prefix) {
			return true
		}
	}
	return false
}

func parseDockerImages(output string) ([]DockerImage, error) {
	images := []DockerImage{}
	k8s := map[string]bool{}
	containers := false
	for _, line := range strings.Split(output, "\n") {
		line = strings.TrimSpace(line)
		if line == "__K8S_CONTAINERS__" {
			containers = true
			continue
		}
		if line == "" {
			continue
		}
		if containers {
			k8s[line] = true
			continue
		}
		var image DockerImage
		if err := json.Unmarshal([]byte(line), &image); err != nil {
			return nil, fmt.Errorf("invalid Docker image listing: %w", err)
		}
		if image.Repository == "<none>" || image.Tag == "<none>" {
			continue
		}
		if !dockerIDPattern.MatchString(image.ID) {
			return nil, errors.New("Docker returned an invalid full image ID")
		}
		images = append(images, image)
	}
	if !containers {
		return nil, errors.New("incomplete Docker inventory")
	}
	for i := range images {
		image := &images[i]
		image.IsKubernetes = isKubernetesRepository(image.Repository) || k8s[image.Repository+":"+image.Tag] || k8s[image.ID] || k8s[strings.TrimPrefix(image.ID, "sha256:")] || k8s[strings.TrimPrefix(image.ID, "sha256:")[:12]]
	}
	return images, nil
}

func (w *Worker) startDockerList(parent context.Context, msg InboundMessage) error {
	var err error
	if msg.RequestID == "" || msg.DockerList == nil {
		err = errors.New("requestId and dockerList endpoint are required")
	} else {
		err = validateRemoteEndpoint(*msg.DockerList)
	}
	if err != nil {
		return w.out.emit(OutboundMessage{Type: "error", RequestID: msg.RequestID, Error: &WorkerError{Code: "invalid_request", Message: err.Error()}})
	}
	endpoint := *msg.DockerList
	return w.startJob(parent, msg.RequestID, func(ctx context.Context, reporter *jobReporter) (*TransferStat, error) {
		ctx, cancel := context.WithTimeout(ctx, 45*time.Second)
		defer cancel()
		client, err := dialSSH(ctx, endpoint, reporter)
		if err != nil {
			return nil, err
		}
		defer client.Close()
		session, err := client.NewSession()
		if err != nil {
			return nil, err
		}
		defer session.Close()
		stdout := &boundedBuffer{limit: 8 * 1024 * 1024}
		stderr := &boundedBuffer{limit: 16384}
		session.Stdout = stdout
		session.Stderr = stderr
		done := make(chan struct{})
		defer close(done)
		go func() {
			select {
			case <-ctx.Done():
				_ = client.Close()
			case <-done:
			}
		}()
		if err = session.Run("sh -c " + shellQuote(dockerListScript)); err != nil {
			return nil, dockerCommandError(stderr.String(), err)
		}
		images, err := parseDockerImages(stdout.String())
		if stdout.buffer.Len() >= stdout.limit {
			return nil, errors.New("Docker inventory exceeded 8 MiB; refusing an incomplete listing")
		}
		if err != nil {
			return nil, err
		}
		// Keep each NDJSON record small even on hosts with thousands of tags.
		for len(images) > 0 {
			n := min(len(images), 200)
			if err := reporter.out.emit(OutboundMessage{Type: "docker_images", JobID: reporter.jobID, Images: images[:n]}); err != nil {
				return nil, err
			}
			images = images[n:]
		}
		return &TransferStat{}, nil
	})
}

func dockerCommandError(output string, err error) error {
	code := "docker_failed"
	if strings.Contains(output, "SUDO_REQUIRED:") || strings.Contains(output, "sudo: a password is required") ||
		(strings.Contains(output, "sudo") && (strings.Contains(output, "not allowed") || strings.Contains(output, "sudoers"))) {
		code = "sudo_required"
	}
	return errorCode(code, fmt.Errorf("%v: %s", err, strings.TrimSpace(output)))
}

func dockerPreflight(zstd bool) string {
	s := dockerAccessScript + "command -v bash >/dev/null 2>&1 || { echo 'bash is required' >&2; exit 73; }\n"
	if zstd {
		s += "command -v zstd >/dev/null 2>&1 || { echo 'zstd is required on both hosts' >&2; exit 73; }\n"
	}
	return s
}

func dockerImageGuards(images []DockerImageSelection, destination bool) string {
	var s strings.Builder
	for _, image := range images {
		expected := image.SourceID
		if destination {
			expected = image.DestinationID
		}
		s.WriteString("actual=$(d image inspect --format '{{.Id}}' -- " + shellQuote(image.Reference) + " 2>/dev/null) || actual=''\n")
		s.WriteString("[ \"$actual\" = " + shellQuote(expected) + " ] || { echo " + shellQuote("IMAGE_CHANGED: "+image.Reference+" changed; refresh and confirm again") + " >&2; exit 75; }\n")
	}
	return s.String()
}

func buildDockerStreamCommand(req RemoteTransferRequest, inner RemoteEndpoint, innerSSH string) string {
	options := req.Docker
	preflight := dockerPreflight(options.Zstd)
	refs := []string{}
	for _, image := range options.Images {
		refs = append(refs, image.Reference)
	}
	producer := preflight + dockerImageGuards(options.Images, false) + "d image save -- " + joinShellArgs(refs)
	consumer := preflight + dockerImageGuards(options.Images, true)
	if options.Zstd {
		producer += " | zstd -T0 -3 -c"
		consumer += "zstd -d -c | "
	}
	consumer += "d image load\nprintf '" + dockerVerifyMarker + "\\n' >&2\n" + dockerImageGuards(options.Images, false)
	run := func(script string) string { return "bash -o pipefail -e -c " + shellQuote(script) }
	remote := func(script string) string {
		return innerSSH + " " + shellQuote(formatSshDestination(inner)) + " " + shellQuote(run(script))
	}
	// Validate both sides before opening the data pipeline, then repeat the tag
	// guards immediately before save/load to catch changes since UI confirmation.
	sourceCheck := preflight + dockerImageGuards(options.Images, false)
	destinationCheck := preflight + dockerImageGuards(options.Images, true)
	var script string
	meter := "python3 -u -c " + shellQuote(dockerMeterScript)
	meterCheck := "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required on the execution host for transfer progress' >&2; exit 73; }\n"
	if req.ExecutionSide == "destination" {
		script = meterCheck + run(destinationCheck) + "\n" + remote(sourceCheck) + "\n" + remote(producer) + " | " + meter + " | " + run(consumer)
	} else {
		script = meterCheck + run(sourceCheck) + "\n" + remote(destinationCheck) + "\n" + run(producer) + " | " + meter + " | " + remote(consumer)
	}
	return run(script)
}

func runDockerStream(ctx context.Context, session *ssh.Session, req RemoteTransferRequest, inner RemoteEndpoint, innerSSH string, reporter *jobReporter, offset int64) (*TransferStat, error) {
	command := buildDockerStreamCommand(req, inner, innerSSH)
	reporter.state("transferring")
	reporter.log("info", fmt.Sprintf("Docker save → %s → Docker load; zstd=%t. Verifying image IDs after load.", endpointLabel(req.Destination), req.Docker.Zstd))
	out := &rsyncLogWriter{reporter: reporter, level: "info"}
	logs := &rsyncLogWriter{reporter: reporter, level: "error"}
	diagnostic := &boundedBuffer{limit: 16384}
	progress := &dockerProgressWriter{reporter: reporter, fallback: io.MultiWriter(logs, diagnostic), offset: offset}
	progress.emit("docker_prepare", 0)
	session.Stdout = out
	session.Stderr = progress
	done := make(chan struct{})
	defer close(done)
	go func() {
		select {
		case <-ctx.Done():
			_ = session.Signal(ssh.SIGTERM)
			_ = session.Close()
		case <-done:
		}
	}()
	err := session.Run(command)
	out.flush()
	progress.flush()
	logs.flush()
	if ctx.Err() != nil {
		return nil, ctx.Err()
	}
	if err != nil {
		return nil, dockerCommandError(diagnostic.String(), err)
	}
	reporter.log("info", "Docker image tags and IDs verified on destination.")
	return &TransferStat{ProtocolWritten: progress.transferred}, nil
}
