//go:build windows

package sender

import "testing"

func TestNormalizeLocalRequestWindowsAbsolutePath(t *testing.T) {
	gotLocal, gotRequested := normalizeLocalRequest(`\\?\`, `D:\go\go-control\bin\ctrlsolve-linux-amd64`)

	if gotLocal != `D:\go\go-control\bin` {
		t.Fatalf("unexpected local: got %q", gotLocal)
	}
	if gotRequested != `ctrlsolve-linux-amd64` {
		t.Fatalf("unexpected requested: got %q", gotRequested)
	}
}
