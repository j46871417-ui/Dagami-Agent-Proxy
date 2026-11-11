package portable

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

func launchProxySettings(app App, local, platform string) ([]string, []string) {
	args := append([]string(nil), app.Args...)
	env := childEnvironment(local)
	if app.Console {
		return args, env
	}
	name := strings.ToLower(strings.TrimSuffix(filepath.Base(app.Path), filepath.Ext(app.Path)))
	gemini := platform == "windows" && (name == "gemini" || name == "geminiapplauncher")
	chromium := false
	switch name {
	case "antigravity", "antigravity ide", "code", "cursor", "windsurf", "vscodium", "claude", "claude-desktop", "codex", "electron":
		chromium = true
	}
	switch app.ID {
	case "antigravity", "antigravity-ide", "vscode", "cursor", "windsurf", "claude-desktop", "codex-desktop":
		chromium = true
	}
	if platform == "linux" && (app.ID == "antigravity" || app.ID == "antigravity-ide" || strings.Contains(strings.ToLower(app.Path), "antigravity")) {
		patchAntigravityBackend(app.Path)
	}
	if chromium || gemini || electronBundle(app.Path, platform) {
		args = append(args, "--proxy-server="+local, "--proxy-bypass-list=localhost;127.0.0.1;[::1]", "--disable-quic")
		if platform == "linux" {
			args = append(args, "--no-sandbox")
		}
	}
	if gemini {
		flags := "--proxy-server=" + local + " --proxy-bypass-list=localhost;127.0.0.1;[::1] --disable-quic"
		for _, key := range []string{"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "QTWEBENGINE_CHROMIUM_FLAGS"} {
			env = append(env, key+"="+strings.TrimSpace(os.Getenv(key)+" "+flags))
		}
	}
	return args, env
}

func electronBundle(executable, platform string) bool {
	resources := filepath.Join(filepath.Dir(executable), "resources")
	if platform == "darwin" {
		resources = filepath.Join(filepath.Dir(executable), "..", "Resources")
	}
	for _, name := range []string{"app.asar", filepath.Join("app", "package.json")} {
		if info, err := os.Stat(filepath.Join(resources, name)); err == nil && info.Mode().IsRegular() {
			return true
		}
	}
	return false
}

func addMissingApps(existing []App) []App {
	registered := make(map[string]bool, len(existing))
	for _, app := range existing {
		registered[app.ID] = true
	}
	for _, app := range DiscoverApps() {
		if !registered[app.ID] {
			existing = append(existing, app)
		}
	}
	return existing
}

func DiscoverApps() []App {
	home, _ := os.UserHomeDir()
	local := os.Getenv("LOCALAPPDATA")
	programs := filepath.Join(local, "Programs")
	entries := []struct {
		id, name, command, folder, windowsExe string
		console                               bool
	}{
		{"antigravity", "Antigravity", "antigravity", "Antigravity", "Antigravity.exe", false},
		{"antigravity-ide", "Antigravity IDE", "antigravity-ide", "Antigravity IDE", "Antigravity IDE.exe", false},
		{"cursor", "Cursor", "cursor", "Cursor", "Cursor.exe", false},
		{"codex", "Codex CLI", "codex", "", "", true},
		{"codex-desktop", "Codex Desktop", "", "Codex", "Codex.exe", false},
		{"claude-code", "Claude Code", "claude", "", "", true},
		{"claude-desktop", "Claude Desktop", "claude-desktop", "Claude", "Claude.exe", false},
		{"gemini-desktop", "Gemini Desktop", "", "Gemini", "Gemini.exe", false},
		{"vscode", "Visual Studio Code", "code", "Visual Studio Code", "Code.exe", false},
		{"windsurf", "Windsurf", "windsurf", "Windsurf", "Windsurf.exe", false},
	}
	apps := make([]App, 0, len(entries))
	for _, entry := range entries {
		app := App{ID: entry.id, Name: entry.name, Console: entry.console}
		var candidates []string
		switch runtime.GOOS {
		case "windows":
			if local != "" && entry.windowsExe != "" {
				folder := entry.folder
				if entry.id == "vscode" {
					folder = "Microsoft VS Code"
				}
				candidates = append(candidates, filepath.Join(programs, folder, entry.windowsExe))
			}
			if home != "" && entry.id == "claude-code" {
				candidates = append(candidates, filepath.Join(home, ".local", "bin", "claude.exe"))
			}
		case "darwin":
			if entry.folder != "" {
				bundle := entry.folder + ".app"
				candidates = append(candidates, filepath.Join("/Applications", bundle), filepath.Join(home, "Applications", bundle))
			}
		}
		if entry.command != "" {
			if path, err := exec.LookPath(entry.command); err == nil {
				if runtime.GOOS != "windows" || strings.EqualFold(filepath.Ext(path), ".exe") {
					candidates = append(candidates, path)
				}
			}
		}
		for _, candidate := range candidates {
			if path, err := resolveExecutable(candidate); err == nil {
				app.Path = path
				break
			}
		}
		apps = append(apps, app)
	}
	return apps
}


func patchAntigravityBackend(appPath string) {
	if appPath == "" {
		return
	}
	dir := filepath.Dir(appPath)
	candidates := []string{
		filepath.Join(dir, "resources", "bin", "language_server"),
		filepath.Join(dir, "resources", "language_server"),
		filepath.Join(dir, "resources", "bin", "language_server.exe"),
	}
	for _, target := range candidates {
		data, err := os.ReadFile(target)
		if err != nil {
			continue
		}
		if bytes.Contains(data, []byte("name=dpeligible,")) {
			continue
		}
		if !bytes.Contains(data, []byte("name=ineligible,")) {
			continue
		}
		_ = os.WriteFile(target+".bak", data, 0755)
		replacements := [][2][]byte{
			{[]byte("https_proxy"), []byte("AG_LS_PROXY")},
			{[]byte("name=ineligible,"), []byte("name=dpeligible,")},
			{[]byte("ineligible_tiers"), []byte("dpeligible_tiers")},
			{[]byte("ineligibleTiers"), []byte("dpeligibleTiers")},
			{[]byte("\x0A\x0Aineligible"), []byte("\x0A\x0Adpeligible")},
			{[]byte("\x52\x0Aineligible\x12"), []byte("\x52\x0Adpeligible\x12")},
		}
		for _, r := range replacements {
			data = bytes.ReplaceAll(data, r[0], r[1])
		}
		_ = os.WriteFile(target, data, 0755)
	}
}
