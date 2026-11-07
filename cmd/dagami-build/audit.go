package main

import (
	"bufio"
	"bytes"
	"fmt"
	"io"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
)

// Inspect the staged blobs, not files that can change during a commit.
func auditIndex() error {
	ignored, e := exec.Command("git", "ls-files", "-ci", "--exclude-standard", "-z").Output()
	if e != nil {
		return e
	}
	if len(ignored) > 0 {
		return fmt.Errorf("tracked ignored paths: %s", strings.ReplaceAll(string(ignored), "\x00", " "))
	}
	data, e := exec.Command("git", "ls-files", "-s", "-z").Output()
	if e != nil {
		return e
	}
	type entry struct{ name, sha string }
	entries := []entry{}
	present := map[string]bool{}
	queries := strings.Builder{}
	for _, record := range bytes.Split(data, []byte{0}) {
		if len(record) == 0 {
			continue
		}
		metadata, name, ok := strings.Cut(string(record), "\t")
		fields := strings.Fields(metadata)
		if !ok || len(fields) != 3 || fields[2] != "0" {
			return fmt.Errorf("unmerged or malformed staged entry: %s", name)
		}
		if fields[0] == "160000" {
			continue
		}
		if fields[0] == "120000" {
			return fmt.Errorf("tracked source symlink: %s", name)
		}
		if (!strings.Contains(name, "/") && filepath.Ext(name) == ".cs") || strings.HasPrefix(name, "license-issuer/") || name == "generate_license_key.py" || name == "SystemDns.ps1" || name == "app.manifest" {
			return fmt.Errorf("forbidden source path: %s", name)
		}
		entries = append(entries, entry{name, fields[1]})
		present[name] = true
		queries.WriteString(fields[1] + "\n")
	}
	for _, name := range []string{"AGENTS.md", "CHANGELOG.md", "go.mod", "src/DevProxyApp.cs", "src/app.manifest", "src/SystemDns.ps1", "cmd/dagami/main.go", "cmd/dagami-build/main.go", "native/core/src/lib.rs"} {
		if !present[name] {
			return fmt.Errorf("missing staged source: %s", name)
		}
	}
	cmd := exec.Command("git", "cat-file", "--batch")
	cmd.Stdin = strings.NewReader(queries.String())
	pipe, e := cmd.StdoutPipe()
	if e != nil {
		return e
	}
	if e = cmd.Start(); e != nil {
		return e
	}
	defer func() { cmd.Process.Kill(); cmd.Wait() }()
	reader := bufio.NewReader(pipe)
	seen := map[string]string{}
	for _, v := range entries {
		line, e := reader.ReadString('\n')
		if e != nil {
			return e
		}
		fields := strings.Fields(line)
		if len(fields) != 3 || fields[0] != v.sha || fields[1] != "blob" {
			return fmt.Errorf("unexpected staged blob for %s", v.name)
		}
		size, e := strconv.Atoi(fields[2])
		if e != nil || size < 0 || size > 32*1024*1024 {
			return fmt.Errorf("staged source exceeds size limit: %s", v.name)
		}
		body := make([]byte, size)
		if _, e = io.ReadFull(reader, body); e != nil {
			return e
		}
		if b, e := reader.ReadByte(); e != nil || b != '\n' {
			return fmt.Errorf("invalid blob boundary: %s", v.name)
		}
		if size > 0 {
			if previous := seen[v.sha]; previous != "" {
				return fmt.Errorf("duplicate staged files: %s and %s", previous, v.name)
			}
			seen[v.sha] = v.name
		}
		for label, re := range secretPatterns {
			if re.Match(body) {
				return fmt.Errorf("possible %s in %s; value withheld", label, v.name)
			}
		}
	}
	if e = cmd.Wait(); e != nil {
		return e
	}
	fmt.Printf("PASS: %d staged files; no ignored, duplicate or recognized secret material\n", len(entries))
	return nil
}
