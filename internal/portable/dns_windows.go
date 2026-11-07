//go:build windows && !routing_only

package portable

import (
	"context"
	_ "embed"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"unicode/utf16"
)

//go:embed system_dns.ps1
var systemDNS []byte

func psPath() string {
	return filepath.Join(os.Getenv("SystemRoot"), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
}
func psQuote(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }
func encodedPS(s string) string {
	words := utf16.Encode([]rune(s))
	b := make([]byte, len(words)*2)
	for i, w := range words {
		binary.LittleEndian.PutUint16(b[i*2:], w)
	}
	return base64.StdEncoding.EncodeToString(b)
}
func dnsStatus(ctx context.Context) (any, error) {
	cmd := exec.CommandContext(ctx, psPath(), "-NoProfile", "-NonInteractive", "-Command", "Get-DnsClientServerAddress -ErrorAction Stop | Select-Object InterfaceAlias,AddressFamily,ServerAddresses | ConvertTo-Json -Depth 4 -Compress")
	prepareProcess(cmd)
	b, e := cmd.Output()
	if e != nil {
		return nil, errors.New("не удалось прочитать DNS Windows")
	}
	if len(b) > 131072 {
		return nil, errors.New("слишком большой отчёт DNS")
	}
	return string(b), nil
}
func dnsChange(ctx context.Context, dir, operation, server, iface string) (string, error) {
	op := map[string]string{"apply": "Apply", "restore": "Restore", "reset": "ResetAll"}[operation]
	if op == "" {
		return "", errors.New("неверная операция DNS")
	}
	request := map[string]any{"JobId": ID(), "Operation": op, "Server": server, "Preset": "Development", "ChangeAdapters": false, "UseNrpt": true, "ReplaceExisting": false, "KeepExternalChanges": true, "ReplaceActiveSetup": false, "ConfirmResetAll": operation == "reset", "Adapters": []any{}, "EncryptedStub": false}
	raw, _ := json.Marshal(request)
	payload := base64.StdEncoding.EncodeToString(raw)
	job := filepath.Join(dir, "dns-"+ID())
	if e := os.Mkdir(job, 0700); e != nil {
		return "", e
	}
	script := filepath.Join(job, "dns.ps1")
	result := filepath.Join(job, "result.txt")
	if e := atomicWrite(script, systemDNS, 0600); e != nil {
		return "", e
	}
	// Existing DNS engine owns backups, transactional verification and rollback.
	inner := "& " + psQuote(psPath()) + " -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + psQuote(script) + " -Operation " + op + " -RequestBase64 " + psQuote(payload) + " *> " + psQuote(result) + "; exit $LASTEXITCODE"
	outer := "$p=Start-Process -FilePath " + psQuote(psPath()) + " -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand'," + psQuote(encodedPS(inner)) + "); exit $p.ExitCode"
	cmd := exec.CommandContext(ctx, psPath(), "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedPS(outer))
	prepareProcess(cmd)
	e := cmd.Run()
	data, _ := os.ReadFile(result)
	if len(data) > 65536 {
		data = data[:65536]
	}
	// PowerShell redirects to UTF-16LE on Windows PowerShell 5.1.
	text := string(data)
	if len(data) >= 2 && data[0] == 255 && data[1] == 254 {
		words := make([]uint16, (len(data)-2)/2)
		for i := range words {
			words[i] = binary.LittleEndian.Uint16(data[2+i*2:])
		}
		text = string(utf16.Decode(words))
	}
	if e != nil {
		code := -1
		if cmd.ProcessState != nil {
			code = cmd.ProcessState.ExitCode()
		}
		return "", errors.New("DNS не подтверждён или отменён UAC. Отчёт сохранён в " + job + "; код: " + strconv.Itoa(code))
	}
	return text, nil
}
