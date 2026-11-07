package portable

import (
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
	data, e := io.ReadAll(io.LimitReader(f, 512*1024*1024+1))
	if e != nil {
		return BinaryReport{}, e
	}
	if int64(len(data)) != fi.Size() {
		return BinaryReport{}, errors.New("файл менялся во время чтения")
	}
	digest := sha256.Sum256(data)
	report := BinaryReport{File: filepath.Base(path), Bytes: len(data), SHA256: hex.EncodeToString(digest[:])}
	core, e := NewByteCore()
	if e != nil {
		return report, e
	}
	defer core.Close()
	for _, sig := range knownSignatures {
		hits, e := core.Find(data, []byte(sig))
		if e != nil {
			return report, e
		}
		if len(hits) != 1 {
			report.Missing++
		}
	}
	hits, e := core.Find(data, []byte("https_proxy"))
	if e != nil {
		return report, e
	}
	report.ProxySites = len(hits)
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
