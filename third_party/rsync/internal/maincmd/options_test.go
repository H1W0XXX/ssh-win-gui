package maincmd

import (
	"fmt"
	"path/filepath"
	"testing"

	"github.com/gokrazy/rsync"
	"github.com/gokrazy/rsync/internal/receiver"
)

func TestSetExactDestinationRewritesSingleFile(t *testing.T) {
	destination := filepath.Join("root", "renamed.txt")
	rt := &receiver.Transfer{Dest: destination}
	files := []*receiver.File{{Name: "original.txt", Mode: rsync.S_IFREG}}

	if err := setExactDestination(rt, files); err != nil {
		t.Fatal(err)
	}
	if rt.Dest != filepath.Dir(destination) {
		t.Fatalf("destination root = %q, want %q", rt.Dest, filepath.Dir(destination))
	}
	if files[0].Name != filepath.Base(destination) {
		t.Fatalf("file name = %q, want %q", files[0].Name, filepath.Base(destination))
	}
}

func TestSetExactDestinationRejectsDirectory(t *testing.T) {
	rt := &receiver.Transfer{Dest: filepath.Join("root", "renamed")}
	files := []*receiver.File{{Name: "original", Mode: rsync.S_IFDIR}}

	if err := setExactDestination(rt, files); err == nil {
		t.Fatal("directory source unexpectedly accepted as an exact file destination")
	}
}

func TestParseHostspec(t *testing.T) {
	for _, tt := range []struct {
		src        string
		parsingURL bool
		wantHost   string
		wantPath   string
		wantPort   int
	}{
		{
			src:        "localhost",
			parsingURL: true,
			wantHost:   "localhost",
			wantPath:   "",
			wantPort:   0,
		},

		{
			src:        "localhost/path",
			parsingURL: true,
			wantHost:   "localhost",
			wantPath:   "path",
			wantPort:   0,
		},

		{
			src:        "user@localhost/path",
			parsingURL: true,
			wantHost:   "user@localhost",
			wantPath:   "path",
			wantPort:   0,
		},

		{
			src:        "user@[2001:db8::1]:23/path",
			parsingURL: true,
			wantHost:   "user@2001:db8::1",
			wantPath:   "path",
			wantPort:   23,
		},

		{
			src:        "localhost:881",
			parsingURL: true,
			wantHost:   "localhost",
			wantPath:   "",
			wantPort:   881,
		},

		{
			src:        "localhost:881/path",
			parsingURL: true,
			wantHost:   "localhost",
			wantPath:   "path",
			wantPort:   881,
		},

		{
			src:        "localhost:/path",
			parsingURL: true,
			wantHost:   "localhost",
			wantPath:   "path",
			wantPort:   0,
		},

		{
			src:        "localhost:",
			parsingURL: false,
			wantHost:   "localhost",
			wantPath:   "",
			wantPort:   0,
		},

		{
			src:        "localhost:path",
			parsingURL: false,
			wantHost:   "localhost",
			wantPath:   "path",
			wantPort:   0,
		},

		{
			src:        "localhost::path",
			parsingURL: false,
			wantHost:   "localhost",
			wantPath:   ":path",
			wantPort:   0,
		},

		{
			src:        "user@localhost::path",
			parsingURL: false,
			wantHost:   "user@localhost",
			wantPath:   ":path",
			wantPort:   0,
		},
	} {
		t.Run(fmt.Sprintf("src=%s, url=%v", tt.src, tt.parsingURL), func(t *testing.T) {
			host, path, port, err := parseHostspec(tt.src, tt.parsingURL)
			if err != nil {
				t.Fatal(err)
			}
			if host != tt.wantHost {
				t.Errorf("unexpected host: got %q, want %q", host, tt.wantHost)
			}
			if path != tt.wantPath {
				t.Errorf("unexpected path: got %q, want %q", path, tt.wantPath)
			}
			if port != tt.wantPort {
				t.Errorf("unexpected port: got %d, want %d", port, tt.wantPort)
			}
		})
	}
}

func TestCheckForHostspec(t *testing.T) {
	for _, tt := range []struct {
		src      string
		wantHost string
		wantPath string
		wantPort int
	}{
		{
			src:      "rsync://localhost",
			wantHost: "localhost",
			wantPath: "",
			wantPort: -1, // daemon-accessing
		},

		{
			src:      "rsync://localhost/path",
			wantHost: "localhost",
			wantPath: "path",
			wantPort: -1, // daemon-accessing
		},

		{
			src:      "rsync://user@localhost/path",
			wantHost: "user@localhost",
			wantPath: "path",
			wantPort: -1, // daemon-accessing
		},

		{
			src:      "user@[2001:db8::1]:path",
			wantHost: "user@2001:db8::1",
			wantPath: "path",
			wantPort: 0, // non-daemon-accessing
		},

		{
			src:      "localhost:path",
			wantHost: "localhost",
			wantPath: "path",
			wantPort: 0, // non-daemon-accessing
		},

		{
			src:      "user@localhost:path",
			wantHost: "user@localhost",
			wantPath: "path",
			wantPort: 0, // non-daemon-accessing
		},

		{
			src:      "localhost:/path",
			wantHost: "localhost",
			wantPath: "/path",
			wantPort: 0, // non-daemon-accessing
		},

		{
			src:      "localhost:",
			wantHost: "localhost",
			wantPath: "",
			wantPort: 0, // non-daemon-accessing
		},

		{
			src:      "localhost:path",
			wantHost: "localhost",
			wantPath: "path",
			wantPort: 0, // non-daemon-accessing
		},

		{
			src:      "localhost::path",
			wantHost: "localhost",
			wantPath: "path",
			wantPort: -1, // daemon-accessing
		},

		{
			src:      "user@localhost::path",
			wantHost: "user@localhost",
			wantPath: "path",
			wantPort: -1, // daemon-accessing
		},
	} {
		t.Run(tt.src, func(t *testing.T) {
			host, path, port, err := checkForHostspec(tt.src)
			if err != nil {
				t.Fatal(err)
			}
			if host != tt.wantHost {
				t.Errorf("unexpected host: got %q, want %q", host, tt.wantHost)
			}
			if path != tt.wantPath {
				t.Errorf("unexpected path: got %q, want %q", path, tt.wantPath)
			}
			if port != tt.wantPort {
				t.Errorf("unexpected port: got %d, want %d", port, tt.wantPort)
			}
		})
	}
}
