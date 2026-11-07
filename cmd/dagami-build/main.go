// dagami-build is the Python-free desktop build, test and packaging entry point.
package main

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"

	"dagami.local/agentproxy/internal/buildinfo"
)

const nativeExe = "DagamiAgentProxy.exe"

var nativeSources = strings.Fields(`DevProxyApp.cs NativeUi.cs FluentUi.cs SimpleUi.cs AntigravityIntegration.cs CliRegionGate.cs ProxyDiagnostics.cs DnsProviders.cs DnsTransport.cs DnsAgent.cs DpiDesync.cs AntigravityUi.cs AntigravityLogin.cs NativeShortcuts.cs NativeLaunchBroker.cs NativeSessions.cs SystemDnsManager.cs NativeCli.cs Brand.cs LicenseManager.cs AppProxyPolicy.cs ApplicationSupport.cs JetBrainsProxySettings.cs ApplicationDiscovery.cs NativeCliApps.cs BugReports.cs NativeUpdates.cs UpdateInstaller.cs UpdateUi.cs NativeStartup.cs ManagedAccess.cs`)
var references = strings.Fields("System.dll System.Core.dll System.Drawing.dll System.Windows.Forms.dll System.Security.dll System.Web.Extensions.dll System.IO.Compression.dll System.Xml.dll System.ServiceProcess.dll")
var targets = []string{"windows/amd64", "darwin/amd64", "darwin/arm64", "linux/amd64", "linux/arm64"}
var secretPatterns = map[string]*regexp.Regexp{
	"GitHub token":       regexp.MustCompile(`\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{40,})`),
	"Telegram bot token": regexp.MustCompile(`\b[0-9]{6,12}:[A-Za-z0-9_-]{30,}\b`),
	"private key":        regexp.MustCompile(`-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----`),
	"OpenAI key":         regexp.MustCompile(`\bsk-(?:proj-|svcacct-)?[A-Za-z0-9]{32,}`),
	"AWS access ID":      regexp.MustCompile(`\bAKIA[A-Z0-9]{16}\b`),
	"Google API key":     regexp.MustCompile(`\bAIza[0-9A-Za-z_-]{35}\b`),
}

func main() {
	if e := run(); e != nil {
		fmt.Fprintln(os.Stderr, e)
		os.Exit(1)
	}
}
func run() error {
	if len(os.Args) == 2 && os.Args[1] == "audit" {
		return auditIndex()
	}
	target := flag.String("target", runtime.GOOS+"/"+runtime.GOARCH, "target GOOS/GOARCH or all")
	out := flag.String("out", "dist-portable/native-next", "output directory")
	check := flag.Bool("test", false, "run Go fixtures before packaging")
	native := flag.Bool("windows-native", false, "also export the standalone WinForms EXE")
	nativeChecks := flag.Bool("native-checks", false, "run selected WinForms preview/fixture checks")
	source := flag.Bool("source", false, "also package desktop source without the legacy Python application")
	routing := flag.Bool("routing-only", false, "build only app routing and connectivity checks, without DNS changes or binary inspection")
	signTool := flag.String("sign-tool", "", "path to Windows SDK SignTool.exe")
	thumbprint := flag.String("sign-thumbprint", "", "thumbprint of a code signing certificate in the current user's store")
	timestamp := flag.String("timestamp-url", "", "HTTPS RFC 3161 timestamp URL for Windows signing")
	flag.Parse()
	if *routing && (*native || *nativeChecks) {
		return errors.New("routing-only cannot be combined with WinForms builds or checks")
	}
	var sign func(string) error
	if *signTool != "" || *thumbprint != "" || *timestamp != "" {
		u, err := url.Parse(*timestamp)
		if runtime.GOOS != "windows" || *signTool == "" || !regexp.MustCompile(`^[[:xdigit:]]{40}$`).MatchString(*thumbprint) || err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil {
			return errors.New("Windows signing requires SignTool, a 40-digit certificate thumbprint and an HTTPS timestamp URL")
		}
		if *target != "windows/amd64" && *target != "all" && !*native && !*nativeChecks {
			return errors.New("signing requires a Windows target")
		}
		sign = func(exe string) error {
			if err := execRun(*signTool, nil, "sign", "/sha1", *thumbprint, "/fd", "SHA256", "/tr", *timestamp, "/td", "SHA256", exe); err != nil {
				return fmt.Errorf("Authenticode signing: %w", err)
			}
			if err := execRun(*signTool, nil, "verify", "/pa", "/tw", exe); err != nil {
				return fmt.Errorf("Authenticode verification: %w", err)
			}
			return nil
		}
	}
	if _, e := os.Stat("go.mod"); e != nil {
		return errors.New("run from the repository root")
	}
	dir, e := filepath.Abs(*out)
	if e != nil {
		return e
	}
	if e = os.MkdirAll(dir, 0755); e != nil {
		return e
	}
	tmp, e := os.MkdirTemp("", "dagami-build-")
	if e != nil {
		return e
	}
	defer os.RemoveAll(tmp)
	if !*routing {
		if e = copyFile("src/SystemDns.ps1", "internal/portable/system_dns.ps1", 0644); e != nil {
			return e
		}
	}
	if *check {
		args := []string{"test"}
		if *routing {
			args = append(args, "-tags", "routing_only")
		}
		if e = execRun("go", nil, append(args, "./...")...); e != nil {
			return e
		}
	}
	all := []string{*target}
	if *target == "all" {
		all = targets
	}
	manifest := map[string]string{}
	var windowsBinary string
	if !*routing && (runtime.GOOS == "windows" && (*target == "all" || *target == "windows/amd64") || *native || *nativeChecks) {
		windowsDir := filepath.Join(tmp, "winforms")
		if *native || *nativeChecks {
			windowsDir = dir
		}
		windowsBinary, e = buildWindows(windowsDir, sign)
		if e != nil {
			return e
		}
		if *native || *nativeChecks {
			if e = addHash(manifest, windowsBinary); e != nil {
				return e
			}
		}
	}
	for _, t := range all {
		valid := false
		for _, known := range targets {
			if t == known {
				valid = true
			}
		}
		if !valid {
			return fmt.Errorf("unsupported target %q", t)
		}
		parts := strings.Split(t, "/")
		bundle, e := buildPortable(tmp, dir, parts[0], parts[1], *routing, windowsBinary, sign)
		if e != nil {
			return e
		}
		if e = addHash(manifest, bundle); e != nil {
			return e
		}
	}
	if *nativeChecks {
		checks := filepath.Join(tmp, "windows-checks")
		if e = checkWindows(windowsBinary, checks); e != nil {
			return e
		}
		previews := filepath.Join(dir, "previews")
		if e = os.MkdirAll(previews, 0755); e != nil {
			return e
		}
		for _, image := range []string{"local-apps.png", "local-page-2.png", "dark-apps.png", "dark-connection.png"} {
			if e = copyFile(filepath.Join(checks, image), filepath.Join(previews, image), 0644); e != nil {
				return e
			}
		}
	}
	if *source {
		zipPath := filepath.Join(dir, "dagami-desktop-source.zip")
		if e = sourceZip(zipPath); e != nil {
			return e
		}
		if e = addHash(manifest, zipPath); e != nil {
			return e
		}
	}
	data, _ := json.MarshalIndent(manifest, "", "  ")
	if e = os.WriteFile(filepath.Join(dir, "SHA256SUMS.json"), append(data, '\n'), 0644); e != nil {
		return e
	}
	names := make([]string, 0, len(manifest))
	for name := range manifest {
		names = append(names, name)
	}
	sort.Strings(names)
	var sums strings.Builder
	for _, name := range names {
		fmt.Fprintf(&sums, "%s  %s\n", manifest[name], name)
	}
	return os.WriteFile(filepath.Join(dir, "SHA256SUMS.txt"), []byte(sums.String()), 0644)
}
func execRun(name string, env []string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Env = append(os.Environ(), env...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}
func buildPortable(tmp, out, osName, arch string, routing bool, winForms string, sign func(string) error) (string, error) {
	label := osName + "-" + arch
	build := filepath.Join(tmp, label)
	if e := os.MkdirAll(build, 0755); e != nil {
		return "", e
	}
	binary := "DagamiAgentProxy"
	if osName == "windows" {
		binary = nativeExe
	}
	path := filepath.Join(build, binary)
	args := []string{"build", "-trimpath", "-buildvcs=false", "-mod=readonly", "-ldflags", "-s -w -buildid="}
	variant := "Portable"
	if routing {
		args = append(args, "-tags", "routing_only")
		variant = "Routing"
	}
	if osName == "windows" && winForms != "" {
		path, variant = winForms, "Desktop"
	} else {
		if e := execRun("go", []string{"GOOS=" + osName, "GOARCH=" + arch, "CGO_ENABLED=0"}, append(args, "-o", path, "./cmd/dagami")...); e != nil {
			return "", e
		}
		if osName == "windows" && sign != nil {
			if e := sign(path); e != nil {
				return "", e
			}
		}
	}
	archive := filepath.Join(out, "Dagami-"+variant+"-"+label+".zip")
	if variant == "Desktop" {
		archive = filepath.Join(out, "Dagami-Agent-Proxy-"+buildinfo.ReleaseTag+"-windows.zip")
	}
	e := writeZIP(archive, func(z *zip.Writer) error {
		if osName == "darwin" {
			base := "Dagami Agent Proxy.app/Contents/"
			if e := zipFile(z, path, base+"MacOS/"+binary, 0755); e != nil {
				return e
			}
			if e := zipText(z, base+"Info.plist", fmt.Sprintf(`<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>ru.dagami.portable</string><key>CFBundleExecutable</key><string>DagamiAgentProxy</string><key>CFBundleName</key><string>Dagami Agent Proxy</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>%[1]s</string><key>CFBundleVersion</key><string>%[1]s</string><key>LSMinimumSystemVersion</key><string>13.0</string></dict></plist>`, buildinfo.Version)); e != nil {
				return e
			}
		} else {
			if e := zipFile(z, path, binary, 0755); e != nil {
				return e
			}
		}
		if variant == "Desktop" {
			hashes := map[string]string{}
			if e := addHash(hashes, path); e != nil {
				return e
			}
			data, err := json.MarshalIndent(hashes, "", "  ")
			if err != nil {
				return err
			}
			if e := zipText(z, "SHA256-native.json", string(data)+"\n"); e != nil {
				return e
			}
		}
		for _, name := range []string{"NATIVE_NEXT.md", "APP_ROUTING.md", "MANAGED_ACCESS.md", "RELEASE_NOTES.md", "LICENSE", "THIRD_PARTY_NOTICES.md"} {
			if e := zipFile(z, name, name, 0644); e != nil {
				return e
			}
		}
		if osName == "windows" {
			return nil
		}
		dep, e := exec.Command("go", "list", "-m", "-f", "{{.Dir}}", "github.com/ebitengine/purego").Output()
		if e != nil {
			return e
		}
		return zipFile(z, filepath.Join(strings.TrimSpace(string(dep)), "LICENSE"), "LICENSE-purego.txt", 0644)
	})
	if e != nil {
		return "", e
	}
	fmt.Println(archive)
	return archive, nil
}
func compiler() string {
	return filepath.Join(os.Getenv("WINDIR"), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe")
}
func buildWindows(dir string, sign func(string) error) (string, error) {
	if runtime.GOOS != "windows" {
		return "", errors.New("WinForms compilation requires Windows")
	}
	if e := os.MkdirAll(dir, 0755); e != nil {
		return "", e
	}
	out := filepath.Join(dir, nativeExe)
	temp, e := os.MkdirTemp(dir, ".compile-")
	if e != nil {
		return "", e
	}
	defer os.RemoveAll(temp)
	ready := filepath.Join(temp, nativeExe)
	args := []string{"/nologo", "/target:winexe", "/platform:anycpu", "/optimize+", "/out:" + ready}
	for _, r := range references {
		args = append(args, "/reference:"+r)
	}
	for _, file := range nativeSources {
		args = append(args, filepath.Join("src", file))
	}
	args = append(args, "/win32manifest:src/app.manifest", "/win32icon:devproxy_pkg/assets/dagami-agent-proxy.ico", "/resource:devproxy_pkg/assets/dagami-agent-proxy.ico,Dagami.AgentProxy.ico", "/resource:devproxy_pkg/assets/icon-64.png,Dagami.AgentProxy.png", "/resource:src/SystemDns.ps1,DevProxy.SystemDns.ps1")
	if e = execRun(compiler(), nil, args...); e != nil {
		return "", e
	}
	if sign != nil {
		if e = sign(ready); e != nil {
			return "", e
		}
	}
	if e = copyFile(ready, out, 0755); e != nil {
		return "", e
	}
	fmt.Println(out)
	return out, nil
}
func checkWindows(exe, dir string) error {
	if e := os.MkdirAll(dir, 0755); e != nil {
		return e
	}
	lib := filepath.Join(dir, nativeExe)
	if e := copyFile(exe, lib, 0755); e != nil {
		return e
	}
	for _, name := range []string{"LocalUiChecks", "ConnectionRecoveryChecks", "SystemDnsUiChecks", "AntigravityPatchChecks", "ManagedAccessChecks"} {
		out := filepath.Join(dir, name+".exe")
		args := []string{"/nologo", "/target:exe", "/out:" + out, "/reference:" + lib}
		for _, r := range references {
			args = append(args, "/reference:"+r)
		}
		args = append(args, filepath.Join("tests", "native", name+".cs"))
		if e := execRun(compiler(), nil, args...); e != nil {
			return e
		}
		cmd := exec.Command(out)
		cmd.Dir = dir
		cmd.Env = append(os.Environ(), "TEMP="+dir, "TMP="+dir)
		data, e := cmd.CombinedOutput()
		fmt.Print(string(data))
		if e != nil {
			return e
		}
		if !strings.Contains(string(data), "PASS") {
			return fmt.Errorf("%s did not report PASS", name)
		}
	}
	return execRun("go", []string{"DAGAMI_NATIVE_MANAGED_CHECKS=" + filepath.Join(dir, "ManagedAccessChecks.exe")}, "test", "./internal/managed", "-run", "^TestWindowsProofInteroperability$", "-count=1")
}
func copyFile(from, to string, mode fs.FileMode) error {
	data, e := os.ReadFile(from)
	if e != nil {
		return e
	}
	return os.WriteFile(to, data, mode)
}
func addHash(m map[string]string, file string) error {
	f, e := os.Open(file)
	if e != nil {
		return e
	}
	defer f.Close()
	h := sha256.New()
	if _, e = io.Copy(h, f); e != nil {
		return e
	}
	m[filepath.Base(file)] = hex.EncodeToString(h.Sum(nil))
	return nil
}
func writeZIP(path string, write func(*zip.Writer) error) error {
	f, e := os.Create(path)
	if e != nil {
		return e
	}
	z := zip.NewWriter(f)
	e = write(z)
	ze := z.Close()
	fe := f.Close()
	if e != nil {
		os.Remove(path)
		return e
	}
	if ze != nil {
		os.Remove(path)
		return ze
	}
	return fe
}
func zipFile(z *zip.Writer, path, name string, mode fs.FileMode) error {
	fi, e := os.Lstat(path)
	if e != nil {
		return e
	}
	if !fi.Mode().IsRegular() {
		return fmt.Errorf("archive refuses a link or device: %s", path)
	}
	f, e := os.Open(path)
	if e != nil {
		return e
	}
	defer f.Close()
	h := &zip.FileHeader{Name: filepath.ToSlash(name), Method: zip.Deflate}
	h.SetMode(mode)
	w, e := z.CreateHeader(h)
	if e != nil {
		return e
	}
	_, e = io.Copy(w, f)
	return e
}
func zipText(z *zip.Writer, name, text string) error {
	w, e := z.Create(name)
	if e != nil {
		return e
	}
	_, e = io.WriteString(w, text)
	return e
}
func sourceZip(path string) error {
	names := []string{"go.mod", "go.sum", "LICENSE", "NATIVE_NEXT.md", "APP_ROUTING.md", "MANAGED_ACCESS.md", "RELEASE_NOTES.md", "THIRD_PARTY_NOTICES.md", "CHANGELOG.md", "src/SystemDns.ps1", "src/app.manifest", "devproxy_pkg/assets/dagami-agent-proxy.ico", "devproxy_pkg/assets/icon-64.png"}
	for _, s := range nativeSources {
		names = append(names, "src/"+s)
	}
	for _, name := range []string{"LocalUiChecks", "ConnectionRecoveryChecks", "SystemDnsUiChecks", "AntigravityPatchChecks", "ManagedAccessChecks"} {
		names = append(names, "tests/native/"+name+".cs")
	}
	for _, dir := range []string{"cmd", "internal"} {
		e := filepath.WalkDir(dir, func(p string, d fs.DirEntry, e error) error {
			if e != nil {
				return e
			}
			if d.Type()&os.ModeSymlink != 0 {
				return fmt.Errorf("source link refused: %s", p)
			}
			switch filepath.Ext(p) {
			case ".go", ".html", ".js", ".css":
				if !d.IsDir() {
					names = append(names, p)
				}
			}
			return nil
		})
		if e != nil {
			return e
		}
	}
	return writeZIP(path, func(z *zip.Writer) error {
		for _, name := range names {
			if filepath.Ext(name) != ".png" && filepath.Ext(name) != ".ico" {
				data, e := os.ReadFile(name)
				if e != nil {
					return e
				}
				for label, re := range secretPatterns {
					if re.Match(data) {
						return fmt.Errorf("possible %s in %s; value withheld", label, name)
					}
				}
			}
			if e := zipFile(z, name, name, 0644); e != nil {
				return e
			}
		}
		return nil
	})
}
