package portable

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestLauncherRecoveryPreservesOriginalAndConflicts(t *testing.T) {
	for _, platform := range []string{"linux", "darwin"} {
		t.Run(platform, func(t *testing.T) {
			root := t.TempDir()
			dir := filepath.Join(root, "state % копия")
			home := filepath.Join(root, "home")
			if err := os.MkdirAll(dir, 0700); err != nil {
				t.Fatal(err)
			}
			if err := os.MkdirAll(home, 0700); err != nil {
				t.Fatal(err)
			}
			source := filepath.Join(root, "engine")
			original := filepath.Join(root, "original-ide")
			for _, path := range []string{source, original} {
				if err := os.WriteFile(path, []byte("fixture"), 0700); err != nil {
					t.Fatal(err)
				}
			}
			app := App{ID: "fixture-ide", Name: "Alpha & Beta", Path: original}
			launchPath, err := enableLauncher(dir, home, platform, source, app)
			if err != nil {
				t.Fatal(err)
			}
			journal, err := loadLauncherJournal(dir, platform)
			if err != nil || len(journal.Apps[app.ID].Files) == 0 {
				t.Fatalf("missing journal: %v", err)
			}
			file := journal.Apps[app.ID].Files[0]
			contents, err := os.ReadFile(file.Path)
			if err != nil {
				t.Fatal(err)
			}
			if platform == "linux" && (!strings.Contains(string(contents), "state %% копия") || !strings.Contains(string(contents), "--launch fixture-ide")) {
				t.Fatalf("incorrect desktop entry: %s", contents)
			}
			if err := os.WriteFile(file.Path, []byte("external edit"), 0600); err != nil {
				t.Fatal(err)
			}
			if err := restoreLaunchers(dir, home, platform, app.ID); err == nil {
				t.Fatal("modified launcher was treated as owned")
			}
			for _, item := range journal.Apps[app.ID].Files {
				if _, err := os.Stat(item.Path); err != nil {
					t.Fatal("part of conflicted launcher was removed", err)
				}
			}
			if err := os.WriteFile(file.Path, contents, 0600); err != nil {
				t.Fatal(err)
			}
			if err := restoreLaunchers(dir, home, platform, app.ID); err != nil {
				t.Fatal(err)
			}
			for _, path := range []string{launchPath, journal.Runner.Path} {
				if _, err := os.Stat(path); !os.IsNotExist(err) {
					t.Fatalf("owned item survived recovery: %s (%v)", path, err)
				}
			}
			if contents, err := os.ReadFile(original); err != nil || string(contents) != "fixture" {
				t.Fatal("original IDE was modified", err)
			}
		})
	}
}

func TestLauncherRejectsRedirectedParent(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("requires Unix symlinks")
	}
	root := t.TempDir()
	home := filepath.Join(root, "home")
	dir := filepath.Join(root, "state")
	foreign := filepath.Join(root, "foreign")
	for _, path := range []string{home, dir, foreign} {
		if err := os.Mkdir(path, 0700); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.Symlink(foreign, filepath.Join(home, ".local")); err != nil {
		t.Fatal(err)
	}
	source := filepath.Join(root, "engine")
	if err := os.WriteFile(source, []byte("fixture"), 0700); err != nil {
		t.Fatal(err)
	}
	if _, err := enableLauncher(dir, home, "linux", source, App{ID: "fixture", Name: "Fixture"}); err == nil {
		t.Fatal("followed launcher parent symlink")
	}
	entries, err := os.ReadDir(foreign)
	if err != nil || len(entries) != 0 {
		t.Fatal("wrote in foreign directory", err)
	}
}
