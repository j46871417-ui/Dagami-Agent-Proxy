package managed

import (
	"encoding/json"
	"errors"
	"io"
	"os"
	"sync/atomic"
	"time"
)

type trafficSnapshot struct {
	Since         time.Time `json:"traffic_since"`
	UploadBytes   uint64    `json:"upload_bytes"`
	DownloadBytes uint64    `json:"download_bytes"`
}

type trafficMeter struct {
	file     string
	saved    trafficSnapshot
	upload   atomic.Uint64
	download atomic.Uint64
}

func openTraffic(file string) (*trafficMeter, error) {
	m := &trafficMeter{file: file}
	data, err := os.ReadFile(file)
	if errors.Is(err, os.ErrNotExist) {
		m.saved.Since = time.Now().UTC()
		return m, writeState(file, m.saved)
	}
	if err != nil {
		return nil, err
	}
	if len(data) > 1024 || json.Unmarshal(data, &m.saved) != nil || m.saved.Since.IsZero() {
		return nil, errors.New("не удалось прочитать счётчики трафика Dagami")
	}
	m.upload.Store(m.saved.UploadBytes)
	m.download.Store(m.saved.DownloadBytes)
	return m, nil
}

func (m *trafficMeter) snapshot() trafficSnapshot {
	return trafficSnapshot{m.saved.Since, m.upload.Load(), m.download.Load()}
}

// Checkpoints run from the gateway's main loop and once more after tunnels drain.
func (m *trafficMeter) flush() error {
	value := m.snapshot()
	if value.UploadBytes == m.saved.UploadBytes && value.DownloadBytes == m.saved.DownloadBytes {
		return nil
	}
	if err := writeState(m.file, value); err != nil {
		return err
	}
	m.saved.UploadBytes, m.saved.DownloadBytes = value.UploadBytes, value.DownloadBytes
	return nil
}

type meteredWriter struct {
	io.Writer
	bytes *atomic.Uint64
}

func (w meteredWriter) Write(p []byte) (int, error) {
	n, err := w.Writer.Write(p)
	w.bytes.Add(uint64(n))
	return n, err
}
