package portable

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
)

type ownedLauncherFile struct {
	Path  string `json:"path"`
	Hash  string `json:"sha256"`
	Prior string `json:"prior_sha256,omitempty"`
}
type launcherRecord struct {
	App   string              `json:"app"`
	Files []ownedLauncherFile `json:"files"`
}
type launcherJournal struct {
	Version  int                       `json:"version"`
	Platform string                    `json:"platform"`
	Runner   ownedLauncherFile         `json:"runner"`
	Apps     map[string]launcherRecord `json:"apps"`
}
type LauncherStatus struct {
	Enabled bool   `json:"enabled"`
	Issue   string `json:"issue,omitempty"`
}

var ownedDigest = regexp.MustCompile(`\A[a-f0-9]{64}\z`)
var safeAppID = regexp.MustCompile(`\A[A-Za-z0-9_-]{1,80}\z`)

func fileDigest(path string) (string, error) {
	f, e := os.Open(path)
	if e != nil {
		return "", e
	}
	defer f.Close()
	h := sha256.New()
	if _, e = io.Copy(h, io.LimitReader(f, 64*1024*1024+1)); e != nil {
		return "", e
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
func digestBytes(b []byte) string { h := sha256.Sum256(b); return hex.EncodeToString(h[:]) }
func shellQuote(s string) string  { return "'" + strings.ReplaceAll(s, "'", "'\\''") + "'" }
func desktopQuote(s string) string {
	quoted := strings.NewReplacer("\\", "\\\\", "\"", "\\\"", "$", "\\$", "`", "\\`", "%", "%%").Replace(s)
	return "\"" + strings.ReplaceAll(quoted, "\\", "\\\\") + "\""
}
func xmlText(s string) string {
	var out strings.Builder
	xml.EscapeText(&out, []byte(s))
	return out.String()
}
func launcherRoot(home, platform string) string {
	if platform == "darwin" {
		return filepath.Join(home, "Applications", "Dagami Launchers")
	}
	return filepath.Join(home, ".local", "share", "applications")
}
func launcherPayload(home, platform, runner, dir string, app App) (map[string][]byte, error) {
	if !filepath.IsAbs(home) || !filepath.IsAbs(runner) || !filepath.IsAbs(dir) || !safeAppID.MatchString(app.ID) || strings.ContainsAny(home+runner+dir+app.Name, "\x00\r\n") {
		return nil, errors.New("небезопасное имя или путь запуска")
	}
	root := launcherRoot(home, platform)
	files := map[string][]byte{}
	if platform == "linux" {
		path := filepath.Join(root, "ru.dagami."+app.ID+".desktop")
		files[path] = []byte("[Desktop Entry]\nVersion=1.0\nType=Application\nName=" + strings.NewReplacer("\\", "\\\\").Replace(app.Name) + " — Dagami\nExec=" + desktopQuote(runner) + " --state-dir " + desktopQuote(dir) + " --launch " + app.ID + "\nTerminal=false\nCategories=Development;Network;\nStartupNotify=true\n")
	} else if platform == "darwin" {
		bundle := filepath.Join(root, app.ID+".app")
		files[filepath.Join(bundle, "Contents", "Info.plist")] = []byte(`<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleExecutable</key><string>DagamiLauncher</string><key>CFBundleIdentifier</key><string>ru.dagami.launcher.` + app.ID + `</string><key>CFBundleName</key><string>` + xmlText(app.Name) + ` — Dagami</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleVersion</key><string>1</string></dict></plist>`)
		files[filepath.Join(bundle, "Contents", "MacOS", "DagamiLauncher")] = []byte("#!/bin/sh\nexec " + shellQuote(runner) + " --state-dir " + shellQuote(dir) + " --launch " + shellQuote(app.ID) + "\n")
	} else {
		return nil, errors.New("управляемые запуски доступны в Linux и macOS")
	}
	return files, nil
}
func (c *Controller) readLaunchers() (launcherJournal, error) {
	return loadLauncherJournal(c.dir, runtime.GOOS)
}
func loadLauncherJournal(dir, platform string) (launcherJournal, error) {
	j := launcherJournal{Version: 1, Platform: platform, Apps: map[string]launcherRecord{}}
	path := filepath.Join(dir, "launchers.json")
	info, e := os.Lstat(path)
	if os.IsNotExist(e) {
		return j, nil
	}
	if e != nil {
		return j, e
	}
	if !info.Mode().IsRegular() || info.Size() > 1024*1024 {
		return j, errors.New("небезопасный журнал запуска")
	}
	raw, e := os.ReadFile(path)
	if e != nil {
		return j, e
	}
	if e = json.Unmarshal(raw, &j); e != nil || j.Version != 1 || j.Platform != platform || j.Apps == nil {
		return j, errors.New("журнал запуска повреждён; сохранён для восстановления")
	}
	return j, nil
}
func saveLaunchers(dir string, j launcherJournal) error {
	raw, e := json.MarshalIndent(j, "", "  ")
	if e != nil {
		return e
	}
	return atomicWrite(filepath.Join(dir, "launchers.json"), raw, 0600)
}

// Check each owned output path below its trusted root. A redirected launcher
// directory must never make a recovery edit a different location.
func regularParents(base, path string) error {
	relative, err := filepath.Rel(base, filepath.Dir(path))
	if err != nil || relative == ".." || strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
		return errors.New("путь выходит за каталог Dagami")
	}
	for _, part := range append([]string{"."}, strings.Split(relative, string(filepath.Separator))...) {
		base = filepath.Join(base, part)
		info, err := os.Lstat(base)
		if os.IsNotExist(err) {
			return nil
		}
		if err != nil {
			return err
		}
		if !info.IsDir() {
			return errors.New("каталог управляемого запуска заменён ссылкой или файлом")
		}
	}
	return nil
}

func allowedOwnedFile(file ownedLauncherFile, allowed map[string]string) error {
	path, e := filepath.Abs(file.Path)
	if e != nil || path != file.Path || allowed[path] == "" || !ownedDigest.MatchString(file.Hash) {
		return errors.New("журнал содержит посторонний путь")
	}
	if e = regularParents(allowed[path], path); e != nil {
		return e
	}
	info, e := os.Lstat(path)
	if os.IsNotExist(e) {
		return nil
	}
	if e != nil {
		return e
	}
	if !info.Mode().IsRegular() {
		return errors.New("управляемый файл заменён ссылкой или каталогом")
	}
	hash, e := fileDigest(path)
	if e != nil {
		return e
	}
	if hash != file.Hash && (file.Prior == "" || hash != file.Prior) {
		return errors.New("управляемый файл изменён вне Dagami; сохранён")
	}
	return nil
}
func (c *Controller) EnableLauncher(id string) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.preview || c.recovering {
		return "", errors.New("восстановление выполняется или изменения отключены в превью")
	}
	var app App
	for _, a := range c.state.Apps {
		if a.ID == id {
			app = a
			break
		}
	}
	if app.Path == "" || app.Console {
		return "", errors.New("выберите установленное GUI-приложение; CLI запускайте командой run")
	}
	home, e := os.UserHomeDir()
	if e != nil {
		return "", e
	}
	source, e := os.Executable()
	if e != nil {
		return "", e
	}
	return enableLauncher(c.dir, home, runtime.GOOS, source, app)
}
func enableLauncher(dir, home, platform, source string, app App) (string, error) {
	runner := filepath.Join(dir, "runner", "DagamiAgentProxy")
	payload, e := launcherPayload(home, platform, runner, dir, app)
	if e != nil {
		return "", e
	}
	sourceInfo, e := os.Stat(source)
	if e != nil {
		return "", e
	}
	if !sourceInfo.Mode().IsRegular() || sourceInfo.Size() > 64*1024*1024 {
		return "", errors.New("слишком большой файл движка")
	}
	data, e := os.ReadFile(source)
	if e != nil {
		return "", e
	}
	payload[runner] = data
	j, e := loadLauncherJournal(dir, platform)
	if e != nil {
		return "", e
	}
	old := map[string]ownedLauncherFile{}
	for _, record := range j.Apps {
		for _, f := range record.Files {
			old[f.Path] = f
		}
	}
	if j.Runner.Path != "" {
		old[j.Runner.Path] = j.Runner
	}
	allowed := map[string]string{}
	for path := range payload {
		allowed[path] = home
	}
	allowed[runner] = dir
	for path := range payload {
		if e = regularParents(allowed[path], path); e != nil {
			return "", e
		}
		if f, ok := old[path]; ok {
			if e = allowedOwnedFile(f, allowed); e != nil {
				return "", e
			}
		} else if _, e = os.Lstat(path); !os.IsNotExist(e) {
			return "", errors.New("путь уже занят; посторонний файл не изменён")
		}
	}
	record := launcherRecord{App: app.ID}
	var launchPath string
	for path, content := range payload {
		f := ownedLauncherFile{Path: path, Hash: digestBytes(content)}
		if previous, ok := old[path]; ok {
			if actual, err := fileDigest(path); err == nil && (actual == previous.Hash || actual == previous.Prior) && actual != f.Hash {
				f.Prior = actual
			}
		}
		if path == runner {
			j.Runner = f
		} else {
			record.Files = append(record.Files, f)
			launchPath = path
		}
	}
	j.Apps[app.ID] = record
	// Journal reaches stable storage before any persistent launcher changes.
	if e = saveLaunchers(dir, j); e != nil {
		return "", e
	}
	for path, content := range payload {
		if e = os.MkdirAll(filepath.Dir(path), 0700); e != nil {
			return "", e
		}
		mode := os.FileMode(0600)
		if path == runner || filepath.Base(path) == "DagamiLauncher" {
			mode = 0700
		}
		if e = atomicWrite(path, content, mode); e != nil {
			return "", fmt.Errorf("подготовка не завершена; журнал сохранён: %w", e)
		}
	}
	if platform == "darwin" {
		launchPath = filepath.Join(launcherRoot(home, platform), app.ID+".app")
	}
	return launchPath, nil
}
func (c *Controller) LauncherStatus(id string) LauncherStatus {
	c.mu.Lock()
	defer c.mu.Unlock()
	j, e := c.readLaunchers()
	if e != nil {
		return LauncherStatus{Issue: e.Error()}
	}
	record, ok := j.Apps[id]
	if !ok {
		return LauncherStatus{}
	}
	home, e := os.UserHomeDir()
	if e != nil {
		return LauncherStatus{Issue: e.Error()}
	}
	payload, e := launcherPayload(home, runtime.GOOS, filepath.Join(c.dir, "runner", "DagamiAgentProxy"), c.dir, App{ID: id})
	if e != nil {
		return LauncherStatus{Issue: e.Error()}
	}
	allowed := map[string]string{filepath.Join(c.dir, "runner", "DagamiAgentProxy"): c.dir}
	for path := range payload {
		allowed[path] = home
	}
	status := LauncherStatus{Enabled: true}
	for _, f := range append(record.Files, j.Runner) {
		if e = allowedOwnedFile(f, allowed); e != nil {
			status.Issue = e.Error()
			return status
		}
		if _, e = os.Stat(f.Path); e != nil {
			status.Issue = "файл управляемого запуска отсутствует"
		}
	}
	return status
}
func (c *Controller) RestoreLauncher(id string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.preview || c.recovering {
		return errors.New("восстановление выполняется или изменения отключены в превью")
	}
	home, e := os.UserHomeDir()
	if e != nil {
		return e
	}
	return restoreLaunchers(c.dir, home, runtime.GOOS, id)
}
func restoreLaunchers(dir, home, platform, id string) error {
	j, e := loadLauncherJournal(dir, platform)
	if e != nil {
		return e
	}
	var failures []string
	for app, record := range j.Apps {
		if id != "" && id != app {
			continue
		}
		payload, e := launcherPayload(home, platform, filepath.Join(dir, "runner", "DagamiAgentProxy"), dir, App{ID: app})
		if e != nil {
			return e
		}
		allowed := map[string]string{}
		for path := range payload {
			allowed[path] = home
		}
		good := record.App == app && len(record.Files) == len(payload)
		seen := map[string]bool{}
		for _, f := range record.Files {
			if seen[f.Path] {
				good = false
			}
			seen[f.Path] = true
			if e = allowedOwnedFile(f, allowed); e != nil {
				good = false
				failures = append(failures, e.Error())
			}
		}
		if !good {
			failures = append(failures, "Откат "+app+" остановлен; запись сохранена")
			continue
		}
		for _, f := range record.Files {
			if e = os.Remove(f.Path); e != nil && !os.IsNotExist(e) {
				good = false
				failures = append(failures, e.Error())
			}
		}
		if good {
			delete(j.Apps, app)
			if platform == "darwin" {
				base := filepath.Join(launcherRoot(home, platform), app+".app")
				for _, path := range []string{filepath.Join(base, "Contents", "MacOS"), filepath.Join(base, "Contents"), base} {
					_ = os.Remove(path)
				}
			}
		}
	}
	if len(j.Apps) == 0 && j.Runner.Path != "" {
		allowed := map[string]string{filepath.Join(dir, "runner", "DagamiAgentProxy"): dir}
		if e = allowedOwnedFile(j.Runner, allowed); e == nil {
			e = os.Remove(j.Runner.Path)
			if os.IsNotExist(e) {
				e = nil
			}
		}
		if e != nil {
			failures = append(failures, e.Error())
		} else {
			j.Runner = ownedLauncherFile{}
		}
	}
	if e = saveLaunchers(dir, j); e != nil {
		failures = append(failures, e.Error())
	}
	if len(failures) > 0 {
		return errors.New(strings.Join(failures, "\n"))
	}
	return nil
}
