package portable

import (
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
	if chromium || gemini {
		args = append(args, "--proxy-server="+local, "--proxy-bypass-list=localhost;127.0.0.1;[::1]", "--disable-quic")
	}
	if gemini {
		flags := "--proxy-server=" + local + " --proxy-bypass-list=localhost;127.0.0.1;[::1] --disable-quic"
		for _, key := range []string{"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "QTWEBENGINE_CHROMIUM_FLAGS"} {
			env = append(env, key+"="+strings.TrimSpace(os.Getenv(key)+" "+flags))
		}
	}
	return args, env
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
