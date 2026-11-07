package portable

import (
	"context"
	"slices"
	"strings"
	"testing"
)

func TestLaunchProxySettings(t *testing.T) {
	const local = "http://127.0.0.1:12345"
	t.Setenv("HTTPS_PROXY", "http://127.0.0.1:54321")
	t.Setenv("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--existing")
	for _, tc := range []struct {
		name, platform, path       string
		console, chromium, webview bool
	}{
		{"cursor", "windows", "Cursor.exe", false, true, false},
		{"claude-code", "windows", "claude.exe", true, false, false},
		{"codex-cli", "windows", "codex.exe", true, false, false},
		{"gemini-windows", "windows", "GeminiAppLauncher.exe", false, true, true},
		{"gemini-mac", "darwin", "Gemini", false, false, false},
		{"unrelated", "linux", "cursor-tools", false, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			app := App{Name: "Cursor (profile)", Path: tc.path, Console: tc.console, Args: []string{"--existing-arg"}}
			args, env := launchProxySettings(app, local, tc.platform)
			if args[0] != app.Args[0] || slices.Contains(args, "--proxy-server="+local) != tc.chromium {
				t.Fatalf("arguments = %q", args)
			}
			if !slices.Contains(env, "HTTPS_PROXY="+local) || !slices.Contains(env, "no_proxy=localhost,127.0.0.1,::1") || slices.Contains(env, "HTTPS_PROXY=http://127.0.0.1:54321") {
				t.Fatal("inherited proxy was not replaced or loopback bypass is missing")
			}
			for _, key := range []string{"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=", "QTWEBENGINE_CHROMIUM_FLAGS="} {
				index := slices.IndexFunc(env, func(value string) bool {
					return strings.HasPrefix(value, key) && strings.Contains(value, "--proxy-server="+local)
				})
				if (index >= 0) != tc.webview {
					t.Fatalf("unexpected proxy flags for %s", key)
				}
			}
		})
	}
}

func TestAppPresetsPreserveRegisteredPaths(t *testing.T) {
	existing := []App{{ID: "cursor", Name: "My Cursor", Path: "custom-cursor"}, {ID: "custom", Name: "Custom"}}
	merged := addMissingApps(existing)
	for _, id := range []string{"cursor", "custom", "codex", "codex-desktop", "claude-code", "claude-desktop", "gemini-desktop"} {
		count := 0
		for _, app := range merged {
			if app.ID == id {
				count++
			}
		}
		if count != 1 {
			t.Fatalf("%s appears %d times", id, count)
		}
	}
	if merged[0].Name != "My Cursor" || merged[0].Path != "custom-cursor" {
		t.Fatal("discovery overwrote a registered path")
	}
}

func TestProbeRejectsUnknownDestination(t *testing.T) {
	for _, service := range []string{"antigravity", "cursor", "codex", "claude", "gemini"} {
		if len(probeHosts(service)) < 2 {
			t.Fatalf("no diagnostic hosts for %s", service)
		}
	}
	if _, err := (&Controller{}).Probe(context.Background(), "http://127.0.0.1:8080"); err == nil {
		t.Fatal("diagnostics accepted an arbitrary URL")
	}
}
