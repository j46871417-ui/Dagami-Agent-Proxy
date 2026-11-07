//go:build !routing_only

package portable

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

type BinaryReport struct {
	File       string `json:"file"`
	Bytes      int    `json:"bytes"`
	SHA256     string `json:"sha256"`
	KnownShape bool   `json:"known_shape"`
	Missing    int    `json:"missing_signatures"`
	ProxySites int    `json:"proxy_sites"`
}

var knownSignatures = []string{"\n\x10ineligible_tiers", "R\x0fineligibleTiers", "\n\nineligible\x18\x03", "R\nineligible", "name=ineligible,proto3", "name=ineligible_tiers,", "json=ineligibleTiers,", "json:\"ineligible_tiers,"}

func InspectBinary(path string) (BinaryReport, error) {
	f, e := os.Open(path)
	if e != nil {
		return BinaryReport{}, e
	}
	defer f.Close()
	fi, e := f.Stat()
	if e != nil || !fi.Mode().IsRegular() || fi.Size() > 512*1024*1024 {
		return BinaryReport{}, errors.New("проверка поддерживает обычные файлы до 512 МиБ")
	}
	report, e := scanBinary(io.LimitReader(f, fi.Size()+1))
	if e != nil {
		return BinaryReport{}, e
	}
	after, e := f.Stat()
	if e != nil || int64(report.Bytes) != fi.Size() || after.Size() != fi.Size() || after.ModTime() != fi.ModTime() {
		return BinaryReport{}, errors.New("файл менялся во время чтения")
	}
	report.File = filepath.Base(path)
	return report, nil
}

const inspectChunkSize = 128 * 1024

func scanBinary(r io.Reader) (BinaryReport, error) {
	type signature struct {
		needle []byte
		count  int
		next   int
	}
	signatures := make([]signature, 0, len(knownSignatures)+1)
	overlap := 0
	for _, text := range append(append([]string(nil), knownSignatures...), "https_proxy") {
		signatures = append(signatures, signature{needle: []byte(text)})
		overlap = max(overlap, len(text)-1)
	}
	data := make([]byte, inspectChunkSize+overlap)
	read, retained := 0, 0
	hash := sha256.New()
	for {
		n, err := io.ReadFull(r, data[retained:retained+inspectChunkSize])
		if err != nil && err != io.EOF && err != io.ErrUnexpectedEOF {
			return BinaryReport{}, err
		}
		hash.Write(data[retained : retained+n])
		start := read - retained
		read += n
		window := data[:retained+n]
		for i := range signatures {
			sig := &signatures[i]
			for index := max(0, sig.next-start); index+len(sig.needle) <= len(window); {
				found := bytes.Index(window[index:], sig.needle)
				if found < 0 {
					break
				}
				index += found + len(sig.needle)
				sig.next = start + index
				sig.count++
				if sig.count > 1024 {
					return BinaryReport{}, errors.New("неоднозначная сигнатура: слишком много совпадений")
				}
			}
		}
		if err != nil {
			break
		}
		retained = min(overlap, len(window))
		copy(data, window[len(window)-retained:])
	}
	report := BinaryReport{Bytes: read, SHA256: hex.EncodeToString(hash.Sum(nil)), ProxySites: signatures[len(signatures)-1].count}
	for _, sig := range signatures[:len(signatures)-1] {
		if sig.count != 1 {
			report.Missing++
		}
	}
	report.KnownShape = report.Missing == 0 && report.ProxySites <= 32
	return report, nil
}
func BackendCandidates(exe string) []string {
	name := strings.ToLower(filepath.Base(exe))
	if name == "agy" || name == "agy.exe" || strings.HasPrefix(name, "language_server") {
		return []string{exe}
	}
	if !strings.Contains(name, "antigravity") {
		return nil
	}
	dir := filepath.Dir(exe)
	home, _ := os.UserHomeDir()
	suffix := ""
	if runtime.GOOS == "windows" {
		suffix = ".exe"
	}
	dirs := []string{filepath.Join(dir, "resources", "bin"), filepath.Join(dir, "resources", "app", "extensions", "antigravity", "bin"), filepath.Join(dir, "resources", "app", "extensions", "agy", "bin")}
	if runtime.GOOS == "darwin" {
		dirs = append(dirs, filepath.Join(filepath.Dir(dir), "Resources", "bin"), filepath.Join(filepath.Dir(dir), "Resources", "app", "extensions", "antigravity", "bin"))
	}
	candidates := []string{filepath.Join(home, ".gemini", "bin", "agy"+suffix)}
	if runtime.GOOS == "windows" {
		candidates = append(candidates, filepath.Join(os.Getenv("LOCALAPPDATA"), "agy", "bin", "agy.exe"))
	}
	for _, d := range dirs {
		m, _ := filepath.Glob(filepath.Join(d, "language_server*"))
		candidates = append(candidates, m...)
	}
	seen := map[string]bool{}
	out := []string{}
	for _, p := range candidates {
		if fi, e := os.Stat(p); e == nil && fi.Mode().IsRegular() && !seen[p] {
			seen[p] = true
			out = append(out, p)
		}
	}
	return out
}
func (c *Controller) InspectApp(id string) ([]BinaryReport, error) {
	c.mu.Lock()
	var path string
	for _, a := range c.state.Apps {
		if a.ID == id {
			path = a.Path
			break
		}
	}
	c.mu.Unlock()
	reports := []BinaryReport{}
	paths := BackendCandidates(path)
	if len(paths) == 0 {
		return nil, errors.New("backend не найден; дайте IDE завершить его загрузку")
	}
	if len(paths) > 8 {
		return nil, errors.New("слишком много backend; проверьте один файл отдельно")
	}
	for _, p := range paths {
		r, e := InspectBinary(p)
		if e != nil {
			return nil, e
		}
		reports = append(reports, r)
	}
	return reports, nil
}
