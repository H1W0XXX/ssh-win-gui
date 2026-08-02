//go:build windows

package receiver

import (
	"os"
	"path/filepath"
	"testing"
)

func TestPendingFileReplacesExistingFileOnWindows(t *testing.T) {
	directory := t.TempDir()
	target := filepath.Join(directory, "existing.txt")
	if err := os.WriteFile(target, []byte("old"), 0o600); err != nil {
		t.Fatal(err)
	}
	root, err := os.OpenRoot(directory)
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()

	pending, err := newPendingFile(root, "existing.txt")
	if err != nil {
		t.Fatal(err)
	}
	defer pending.Cleanup()
	if _, err := pending.Write([]byte("new")); err != nil {
		t.Fatal(err)
	}
	if err := pending.CloseAtomicallyReplace(); err != nil {
		t.Fatal(err)
	}
	got, err := os.ReadFile(target)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != "new" {
		t.Fatalf("target contents = %q, want new", got)
	}
}
