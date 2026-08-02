package receiver

import (
	"bytes"
	"compress/flate"
	"io"
	"testing"

	"github.com/gokrazy/rsync/internal/rsyncwire"
)

func writeDeflatedLiteral(t *testing.T, wire *bytes.Buffer, data, dict []byte) {
	t.Helper()

	var compressed bytes.Buffer
	var w *flate.Writer
	var err error
	if len(dict) > 0 {
		w, err = flate.NewWriterDict(&compressed, flate.DefaultCompression, dict)
	} else {
		w, err = flate.NewWriter(&compressed, flate.DefaultCompression)
	}
	if err != nil {
		t.Fatalf("NewWriter: %v", err)
	}
	if _, err := w.Write(data); err != nil {
		t.Fatalf("Write: %v", err)
	}
	if err := w.Flush(); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	out := compressed.Bytes()
	if !bytes.HasSuffix(out, []byte(syncFlushTail)) {
		t.Fatalf("compressed literal does not end with sync flush tail: %x", out)
	}
	out = out[:len(out)-len(syncFlushTail)]
	for len(out) > 0 {
		n := min(len(out), maxDataCount)
		wire.WriteByte(byte(deflatedData + (n >> 8)))
		wire.WriteByte(byte(n))
		wire.Write(out[:n])
		out = out[n:]
	}
}

func newCompressedTokenTransfer(wire []byte) *Transfer {
	return &Transfer{
		Opts: &TransferOpts{Compress: true, CompressMatchedData: true},
		Conn: &rsyncwire.Conn{Reader: bytes.NewReader(wire), Writer: io.Discard},
	}
}

func TestRecvDeflatedTokenLiteral(t *testing.T) {
	var wire bytes.Buffer
	writeDeflatedLiteral(t, &wire, []byte("hello compressed rsync"), nil)
	wire.WriteByte(endFlag)

	rt := newCompressedTokenTransfer(wire.Bytes())
	token, data, err := rt.recvToken()
	if err != nil {
		t.Fatalf("recvToken literal: %v", err)
	}
	if token != int32(len(data)) || string(data) != "hello compressed rsync" {
		t.Fatalf("recvToken literal = token %d data %q", token, data)
	}
	token, data, err = rt.recvToken()
	if err != nil {
		t.Fatalf("recvToken end: %v", err)
	}
	if token != 0 || data != nil {
		t.Fatalf("recvToken end = token %d data %q", token, data)
	}
}

func TestRecvDeflatedTokenUsesMatchedDataHistory(t *testing.T) {
	first := []byte("literal-prefix:")
	matched := []byte("matched-block-")
	second := []byte("matched-block-matched-block-tail")

	var wire bytes.Buffer
	writeDeflatedLiteral(t, &wire, first, nil)
	wire.WriteByte(tokenRel)
	dict := append(append([]byte{}, first...), matched...)
	writeDeflatedLiteral(t, &wire, second, dict)
	wire.WriteByte(endFlag)

	rt := newCompressedTokenTransfer(wire.Bytes())
	token, data, err := rt.recvToken()
	if err != nil {
		t.Fatalf("recvToken first literal: %v", err)
	}
	if token <= 0 || !bytes.Equal(data, first) {
		t.Fatalf("first literal = token %d data %q", token, data)
	}

	token, data, err = rt.recvToken()
	if err != nil {
		t.Fatalf("recvToken matched token: %v", err)
	}
	if token != -1 || data != nil {
		t.Fatalf("matched token = token %d data %q", token, data)
	}
	rt.seeDeflateToken(matched)

	token, data, err = rt.recvToken()
	if err != nil {
		t.Fatalf("recvToken second literal: %v", err)
	}
	if token <= 0 || !bytes.Equal(data, second) {
		t.Fatalf("second literal = token %d data %q", token, data)
	}
}

func TestRecvDeflatedTokenRun(t *testing.T) {
	var wire bytes.Buffer
	wire.WriteByte(tokenRunRel)
	wire.Write([]byte{2, 0})
	wire.WriteByte(endFlag)

	rt := newCompressedTokenTransfer(wire.Bytes())
	for i, want := range []int32{-1, -2, -3} {
		token, data, err := rt.recvToken()
		if err != nil {
			t.Fatalf("recvToken run %d: %v", i, err)
		}
		if token != want || data != nil {
			t.Fatalf("recvToken run %d = token %d data %q, want %d nil", i, token, data, want)
		}
	}
}

func TestSeeDeflateTokenSkippedForZlibx(t *testing.T) {
	rt := &Transfer{Opts: &TransferOpts{Compress: true}}
	rt.seeDeflateToken([]byte("matched"))
	if rt.deflated != nil {
		t.Fatalf("seeDeflateToken initialized history for zlibx-style compression")
	}
}
