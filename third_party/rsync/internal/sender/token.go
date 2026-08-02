package sender

import (
	"bytes"
	"compress/flate"
	"encoding/binary"
)

const (
	endFlag       = 0
	tokenLong     = 0x20
	tokenRunLong  = 0x21
	deflatedData  = 0x40
	tokenRel      = 0x80
	tokenRunRel   = 0xc0
	maxDataCount  = 16383
	syncFlushTail = "\x00\x00\xff\xff"
)

type deflatedTokenSender struct {
	writer     *flate.Writer
	output     deflatedDataWriter
	lastToken  int32
	runStart   int32
	lastRunEnd int32
}

type deflatedDataWriter struct {
	st   *Transfer
	hold []byte
}

func (w *deflatedDataWriter) Write(p []byte) (int, error) {
	written := len(p)
	if len(w.hold) > 0 {
		combined := make([]byte, 0, len(w.hold)+len(p))
		combined = append(combined, w.hold...)
		combined = append(combined, p...)
		p = combined
		w.hold = w.hold[:0]
	}
	if len(p) <= len(syncFlushTail) {
		w.hold = append(w.hold, p...)
		return written, nil
	}
	emit := len(p) - len(syncFlushTail)
	if err := w.writeDeflatedData(p[:emit]); err != nil {
		return 0, err
	}
	w.hold = append(w.hold, p[emit:]...)
	return written, nil
}

func (w *deflatedDataWriter) writeDeflatedData(out []byte) error {
	for len(out) > 0 {
		n := min(len(out), maxDataCount)
		if err := w.st.Conn.WriteByte(byte(deflatedData + (n >> 8))); err != nil {
			return err
		}
		if err := w.st.Conn.WriteByte(byte(n)); err != nil {
			return err
		}
		if _, err := w.st.Conn.Writer.Write(out[:n]); err != nil {
			return err
		}
		out = out[n:]
	}
	return nil
}

func (w *deflatedDataWriter) finishSyncFlush() error {
	if !bytes.Equal(w.hold, []byte(syncFlushTail)) {
		if err := w.writeDeflatedData(w.hold); err != nil {
			return err
		}
	}
	w.hold = w.hold[:0]
	return nil
}

func (st *Transfer) beginDeflatedTokens() error {
	if st.deflated == nil {
		st.deflated = &deflatedTokenSender{}
		st.deflated.output.st = st
		w, err := flate.NewWriter(&st.deflated.output, st.Opts.CompressionLevel())
		if err != nil {
			return err
		}
		st.deflated.writer = w
	} else {
		st.deflated.output.st = st
		st.deflated.output.hold = st.deflated.output.hold[:0]
		st.deflated.writer.Reset(&st.deflated.output)
	}
	st.deflated.lastToken = -1
	st.deflated.runStart = 0
	st.deflated.lastRunEnd = 0
	return nil
}

// rsync/token.c:simple_send_token
func (st *Transfer) simpleSendToken(ms *mapStruct, token int32, offset int64, n int64) error {
	if n > 0 {
		st.Logger.Printf("sending unmatched chunks offset=%d, n=%d", offset, n)
		l := int64(0)
		for l < n {
			n1 := min(int64(chunkSize), n-l)

			chunk, err := ms.ptr(offset+l, int32(n1))
			if err != nil {
				return err
			}

			if err := st.Conn.WriteInt32(int32(n1)); err != nil {
				return err
			}

			if _, err := st.Conn.Writer.Write(chunk); err != nil {
				return err
			}

			l += n1
		}
	}
	if token != -2 {
		return st.Conn.WriteInt32(-(token + 1))
	}
	return nil
}

func (st *Transfer) writeDeflatedInput(data []byte) error {
	if _, err := st.deflated.writer.Write(data); err != nil {
		return err
	}
	return nil
}

func (st *Transfer) flushDeflatedLiteral() error {
	if err := st.deflated.writer.Flush(); err != nil {
		return err
	}
	return st.deflated.output.finishSyncFlush()
}

func (st *Transfer) writeCompressedTokenRun(runStart, lastToken int32) error {
	r := runStart - st.deflated.lastRunEnd
	n := lastToken - runStart
	if r >= 0 && r <= 63 {
		flag := tokenRel + int(r)
		if n != 0 {
			flag = tokenRunRel + int(r)
		}
		if err := st.Conn.WriteByte(byte(flag)); err != nil {
			return err
		}
	} else {
		flag := tokenLong
		if n != 0 {
			flag = tokenRunLong
		}
		if err := st.Conn.WriteByte(byte(flag)); err != nil {
			return err
		}
		if err := st.Conn.WriteInt32(runStart); err != nil {
			return err
		}
	}
	if n != 0 {
		var buf [2]byte
		binary.LittleEndian.PutUint16(buf[:], uint16(n))
		if _, err := st.Conn.Writer.Write(buf[:]); err != nil {
			return err
		}
	}
	st.deflated.lastRunEnd = lastToken
	return nil
}

func (st *Transfer) deflatedSendToken(ms *mapStruct, token int32, offset int64, n int64) error {
	ds := st.deflated
	if ds.lastToken == -1 {
		ds.lastRunEnd = 0
		ds.runStart = token
	} else if ds.lastToken == -2 {
		ds.runStart = token
	} else if n != 0 || token != ds.lastToken+1 || token >= ds.runStart+65536 {
		if err := st.writeCompressedTokenRun(ds.runStart, ds.lastToken); err != nil {
			return err
		}
		ds.runStart = token
	}

	ds.lastToken = token

	for l := int64(0); l < n; {
		n1 := min(int64(chunkSize), n-l)
		chunk, err := ms.ptr(offset+l, int32(n1))
		if err != nil {
			return err
		}
		if err := st.writeDeflatedInput(chunk); err != nil {
			return err
		}
		l += n1
	}
	if n != 0 && token != -2 {
		if err := st.flushDeflatedLiteral(); err != nil {
			return err
		}
	}

	if token == -1 {
		return st.Conn.WriteByte(endFlag)
	}
	return nil
}

// rsync/token.c:send_token
func (st *Transfer) sendToken(ms *mapStruct, i int32, offset int64, n int64) error {
	if st.Opts.Compress() {
		return st.deflatedSendToken(ms, i, offset, n)
	}
	return st.simpleSendToken(ms, i, offset, n)
}
