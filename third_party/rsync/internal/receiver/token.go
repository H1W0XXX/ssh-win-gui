package receiver

import (
	"bytes"
	"compress/flate"
	"encoding/binary"
	"fmt"
	"io"
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
	maxTokenIndex = int32(0x7ffffffe)
	flateWindow   = 32 * 1024
)

type deflatedTokenReceiver struct {
	savedFlag int
	rxToken   int32
	rxRun     int32
	history   []byte
}

// rsync/token.c:recvToken
func (rt *Transfer) recvToken() (token int32, data []byte, _ error) {
	if rt.Opts.Compress {
		return rt.recvDeflatedToken()
	}

	var err error
	token, err = rt.Conn.ReadInt32()
	if err != nil {
		return 0, nil, err
	}
	if token <= 0 {
		return token, nil, nil
	}
	data = make([]byte, int(token))
	if _, err := io.ReadFull(rt.Conn.Reader, data); err != nil {
		return 0, nil, err
	}
	return token, data, nil
}

func (rt *Transfer) deflatedReceiver() *deflatedTokenReceiver {
	if rt.deflated == nil {
		rt.deflated = &deflatedTokenReceiver{savedFlag: -1}
	}
	return rt.deflated
}

func (rt *Transfer) recvDeflatedToken() (int32, []byte, error) {
	dr := rt.deflatedReceiver()
	for {
		if dr.rxRun > 0 {
			return dr.recvCompressedTokenRun()
		}

		flag, err := rt.readCompressedFlag(dr)
		if err != nil {
			return 0, nil, err
		}

		if (flag & 0xc0) == deflatedData {
			data, err := rt.readDeflatedLiteral(dr, flag)
			if err != nil {
				return 0, nil, err
			}
			if len(data) == 0 {
				continue
			}
			dr.appendHistory(data)
			return int32(len(data)), data, nil
		}

		if flag == endFlag {
			rt.deflated = nil
			return 0, nil, nil
		}

		token, err := rt.recvCompressedTokenNum(dr, flag)
		if err != nil {
			return 0, nil, err
		}
		return token, nil, nil
	}
}

func (rt *Transfer) readCompressedFlag(dr *deflatedTokenReceiver) (int, error) {
	if dr.savedFlag >= 0 {
		flag := dr.savedFlag
		dr.savedFlag = -1
		return flag, nil
	}
	flag, err := rt.Conn.ReadByte()
	return int(flag), err
}

func (rt *Transfer) readDeflatedLiteral(dr *deflatedTokenReceiver, flag int) ([]byte, error) {
	var compressed bytes.Buffer
	for {
		n, err := rt.readDeflatedDataFrame(&compressed, flag)
		if err != nil {
			return nil, err
		}
		if n == 0 {
			return nil, fmt.Errorf("invalid empty deflated data frame")
		}
		next, err := rt.Conn.ReadByte()
		if err != nil {
			return nil, err
		}
		flag = int(next)
		if (flag & 0xc0) != deflatedData {
			dr.savedFlag = flag
			break
		}
	}

	compressed.WriteString(syncFlushTail)
	r := flate.NewReaderDict(bytes.NewReader(compressed.Bytes()), dr.history)
	defer r.Close()
	out, err := io.ReadAll(r)
	if err != nil && err != io.ErrUnexpectedEOF {
		return nil, err
	}
	return out, nil
}

func (rt *Transfer) readDeflatedDataFrame(dst *bytes.Buffer, flag int) (int, error) {
	n := ((flag & 0x3f) << 8)
	b, err := rt.Conn.ReadByte()
	if err != nil {
		return 0, err
	}
	n += int(b)
	if n < 0 || n > maxDataCount {
		return 0, fmt.Errorf("invalid deflated data length %d", n)
	}
	_, err = io.CopyN(dst, rt.Conn.Reader, int64(n))
	return n, err
}

func (rt *Transfer) recvCompressedTokenNum(dr *deflatedTokenReceiver, flag int) (int32, error) {
	if flag&tokenRel != 0 {
		incr := int32(flag & 0x3f)
		if dr.rxToken > maxTokenIndex-incr {
			return 0, fmt.Errorf("invalid token number in compressed stream")
		}
		dr.rxToken += incr
		flag >>= 6
	} else {
		token, err := rt.Conn.ReadInt32()
		if err != nil {
			return 0, err
		}
		if token < 0 || token > maxTokenIndex {
			return 0, fmt.Errorf("invalid token number in compressed stream")
		}
		dr.rxToken = token
	}

	if flag&1 != 0 {
		var run [2]byte
		if _, err := io.ReadFull(rt.Conn.Reader, run[:]); err != nil {
			return 0, err
		}
		dr.rxRun = int32(binary.LittleEndian.Uint16(run[:]))
		if dr.rxRun <= 0 || dr.rxToken > maxTokenIndex-dr.rxRun {
			return 0, fmt.Errorf("invalid token run in compressed stream")
		}
	}

	return -1 - dr.rxToken, nil
}

func (dr *deflatedTokenReceiver) recvCompressedTokenRun() (int32, []byte, error) {
	if dr.rxRun <= 0 || dr.rxToken >= maxTokenIndex {
		return 0, nil, fmt.Errorf("invalid token run in compressed stream")
	}
	dr.rxToken++
	dr.rxRun--
	return -1 - dr.rxToken, nil, nil
}

func (rt *Transfer) seeDeflateToken(data []byte) {
	if !rt.Opts.CompressMatchedData || len(data) == 0 {
		return
	}
	rt.deflatedReceiver().appendHistory(data)
}

func (dr *deflatedTokenReceiver) appendHistory(data []byte) {
	if len(data) >= flateWindow {
		dr.history = append(dr.history[:0], data[len(data)-flateWindow:]...)
		return
	}
	if len(dr.history)+len(data) > flateWindow {
		copy(dr.history, dr.history[len(dr.history)+len(data)-flateWindow:])
		dr.history = dr.history[:flateWindow-len(data)]
	}
	dr.history = append(dr.history, data...)
}
