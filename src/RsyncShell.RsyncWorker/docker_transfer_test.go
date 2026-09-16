package main

import (
	"context"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

var testImageID = "sha256:" + strings.Repeat("a", 64)

func TestDockerInventoryFiltersKubernetesWithoutDroppingOtherTags(t *testing.T) {
	rows := []DockerImage{
		{Repository: "registry.k8s.io/pause", Tag: "3.10", ID: testImageID},
		{Repository: "private:5000/app", Tag: "prod", ID: testImageID},
		{Repository: "private:5000/app", Tag: "dev", ID: testImageID},
		{Repository: "<none>", Tag: "<none>", ID: testImageID},
	}
	var output strings.Builder
	for _, row := range rows {
		b, _ := json.Marshal(row)
		output.Write(b)
		output.WriteByte('\n')
	}
	output.WriteString("__K8S_CONTAINERS__\nprivate:5000/app:prod\n")
	images, err := parseDockerImages(output.String())
	if err != nil {
		t.Fatal(err)
	}
	if len(images) != 3 || !images[0].IsKubernetes || !images[1].IsKubernetes || images[2].IsKubernetes {
		t.Fatalf("unexpected inventory: %+v", images)
	}
	if _, err := parseDockerImages(strings.Split(output.String(), "__K8S_CONTAINERS__")[0]); err == nil {
		t.Fatal("accepted incomplete inventory")
	}
}

func TestDockerTransferRejectsShellInjectionAndMissingIDs(t *testing.T) {
	for _, ref := range []string{"app:tag;touch /tmp/bad", "$(id):tag", "--output=x", "app:tag\n", "registry:5000/app"} {
		if err := validateDockerTransfer(&DockerTransferOptions{Images: []DockerImageSelection{{Reference: ref, SourceID: testImageID}}}); err == nil {
			t.Fatalf("accepted %q", ref)
		}
	}
	if err := validateDockerTransfer(&DockerTransferOptions{Images: []DockerImageSelection{{Reference: "registry:5000/app:v1", SourceID: testImageID}}}); err != nil {
		t.Fatal(err)
	}
}

// Execute the actual generated nested Bash pipelines with fake Docker/SSH/zstd
// commands. No Docker daemon, remote host, image import, or sudo change is used.
func TestDockerPipelines(t *testing.T) {
	bash := os.Getenv("RSYNCSHELL_TEST_BASH")
	if bash == "" && runtime.GOOS == "windows" {
		if git, err := exec.LookPath("git"); err == nil {
			candidate := filepath.Join(filepath.Dir(git), "..", "bin", "bash.exe")
			if _, err := os.Stat(candidate); err == nil {
				bash = candidate
			}
		}
		if bash == "" {
			t.Skip("Git Bash unavailable; set RSYNCSHELL_TEST_BASH")
		}
	}
	if bash == "" {
		var err error
		bash, err = exec.LookPath("bash")
		if err != nil {
			t.Skip("bash unavailable; set RSYNCSHELL_TEST_BASH")
		}
	}
	for _, side := range []string{"source", "destination"} {
		for _, tc := range []struct {
			name      string
			zstd      bool
			flags     string
			wantError string
		}{
			{name: "raw"}, {name: "zstd", zstd: true}, {name: "sudo", flags: "NEED_SUDO=1"},
			{name: "sudo_password", flags: "NEED_SUDO=1 REQUIRE_PASSWORD=1", wantError: "SUDO_REQUIRED"},
			{name: "daemon_down", flags: "DAEMON_DOWN=1", wantError: "DOCKER_UNAVAILABLE"},
			{name: "changed_target", flags: "TARGET_CHANGED=1", wantError: "IMAGE_CHANGED"},
			{name: "changed_source", flags: "SOURCE_CHANGED=1", wantError: "IMAGE_CHANGED"},
			{name: "save_fails", flags: "SAVE_FAIL=1", wantError: ""},
			{name: "load_fails", flags: "LOAD_FAIL=1", wantError: ""},
			{name: "zstd_fails", zstd: true, flags: "ZSTD_FAIL=1", wantError: ""},
		} {
			t.Run(side+"/"+tc.name, func(t *testing.T) {
				state := filepath.ToSlash(filepath.Join(t.TempDir(), "loaded"))
				req := RemoteTransferRequest{ExecutionSide: side, Docker: &DockerTransferOptions{Zstd: tc.zstd, Images: []DockerImageSelection{{Reference: "registry:5000/app:stable", SourceID: testImageID}}}}
				innerSide := "destination"
				if side == "destination" {
					innerSide = "source"
				}
				command := buildDockerStreamCommand(req, RemoteEndpoint{Host: "test", User: "test"}, "remote")
				script := `docker() {
 if [ "$DAEMON_DOWN" = 1 ]; then echo 'Cannot connect to Docker daemon' >&2; return 1; fi
 if [ "$NEED_SUDO" = 1 ] && [ "$SUDO_OK" != 1 ]; then echo 'permission denied' >&2; return 1; fi
 case "$1 $2" in
 'image ls') return 0;;
 'image inspect')
  if [ "$SIDE" = source ]; then
   if [ "$SOURCE_CHANGED" = 1 ]; then echo changed; else printf '%s\n' "$IMAGE_ID"; fi
  elif [ "$TARGET_CHANGED" = 1 ]; then echo changed
  elif [ -f "$STATE" ]; then printf '%s\n' "$IMAGE_ID"
  else return 1; fi;;
 'image save') printf 'test-payload'; [ "$SAVE_FAIL" != 1 ];;
 'image load') local value; value=$(cat); [ "$LOAD_FAIL" != 1 ] || return 1; [ "$value" = test-payload ] || return 2; printf loaded > "$STATE";;
 *) return 99;;
 esac
}
sudo() {
 [ "$1" = -n ] || return 98
 if [ "$REQUIRE_PASSWORD" = 1 ]; then echo 'sudo: a password is required' >&2; return 1; fi
 shift; SUDO_OK=1 "$@"
}
zstd() { [ "$ZSTD_FAIL" != 1 ] || return 1; cat; }
remote() { shift; SIDE="$INNER_SIDE" bash -c "$1"; }
export -f docker sudo zstd remote
export SIDE=` + shellQuote(side) + ` INNER_SIDE=` + shellQuote(innerSide) + ` STATE=` + shellQuote(state) + ` IMAGE_ID=` + shellQuote(testImageID) + "\n"
				if tc.flags != "" {
					script += "export " + tc.flags + "\n"
				}
				script += command
				ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
				defer cancel()
				cmd := exec.CommandContext(ctx, bash, "--noprofile", "--norc", "-s")
				cmd.Stdin = strings.NewReader(script)
				output, err := cmd.CombinedOutput()
				fail := tc.wantError != "" || strings.HasSuffix(tc.name, "fails")
				if (err != nil) != fail {
					t.Fatalf("err=%v output=%s", err, output)
				}
				if tc.wantError != "" && !strings.Contains(string(output), tc.wantError) {
					t.Fatalf("missing error marker %s: %s", tc.wantError, output)
				}
				if !fail {
					if _, err := os.Stat(state); err != nil {
						t.Fatalf("image was not loaded: %v; %s", err, output)
					}
				}
				if tc.wantError != "" {
					if _, err := os.Stat(state); err == nil {
						t.Fatal("preflight failure still imported an image")
					}
				}
			})
		}
	}
}
