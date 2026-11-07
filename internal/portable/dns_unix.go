//go:build !windows

package portable

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

type dnsBackup struct {
	Version   int      `json:"version"`
	OS        string   `json:"os"`
	Interface string   `json:"interface"`
	Before    []string `json:"before"`
	Applied   string   `json:"applied"`
}

func dnsCommand(parent context.Context, command string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(parent, 60*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, command, args...)
	cmd.Env = append(os.Environ(), "LC_ALL=C", "LANG=C")
	b, e := cmd.CombinedOutput()
	if len(b) > 32768 {
		b = b[:32768]
	}
	if e != nil {
		return "", errors.New("команда DNS не завершилась успешно; проверьте права и состояние сети")
	}
	return strings.TrimSpace(string(b)), nil
}
func dnsStatus(ctx context.Context) (any, error) {
	if runtime.GOOS == "darwin" {
		return dnsCommand(ctx, "/usr/sbin/networksetup", "-listallnetworkservices")
	}
	return dnsCommand(ctx, "/usr/bin/resolvectl", "status")
}
func currentDNS(ctx context.Context, iface string) ([]string, error) {
	var text string
	var e error
	if runtime.GOOS == "darwin" {
		text, e = dnsCommand(ctx, "/usr/sbin/networksetup", "-getdnsservers", iface)
		if e != nil {
			return nil, e
		}
		if strings.HasPrefix(text, "There aren't any DNS Servers set on ") {
			return []string{}, nil
		}
	} else {
		text, e = dnsCommand(ctx, "/usr/bin/resolvectl", "dns", iface)
		if e != nil {
			return nil, e
		}
		_, text, _ = strings.Cut(text, ":")
	}
	result := []string{}
	for _, v := range strings.Fields(text) {
		if net.ParseIP(v) == nil {
			return nil, errors.New("не удалось однозначно прочитать прежний DNS; запись отменена")
		}
		result = append(result, v)
	}
	return result, nil
}
func applyDNS(ctx context.Context, iface string, servers []string) error {
	if runtime.GOOS == "darwin" {
		args := append([]string{"/usr/sbin/networksetup", "-setdnsservers", iface}, servers...)
		if len(servers) == 0 {
			args = append(args, "Empty")
		}
		quote := func(v string) string { return "'" + strings.ReplaceAll(v, "'", "'\\''") + "'" }
		for i := range args {
			args[i] = quote(args[i])
		}
		script := "do shell script " + `"` + strings.NewReplacer(`\`, `\\`, `"`, `\"`).Replace(strings.Join(args, " ")) + `"` + " with administrator privileges"
		_, e := dnsCommand(ctx, "/usr/bin/osascript", "-e", script)
		return e
	}
	args := append([]string{"/usr/bin/resolvectl", "dns", iface}, servers...)
	if len(servers) == 0 {
		args = append(args, "")
	}
	_, e := dnsCommand(ctx, "/usr/bin/pkexec", args...)
	return e
}
func dnsChange(ctx context.Context, dir, operation, server, iface string) (string, error) {
	if operation == "reset" {
		return "", errors.New("общий сброс NRPT доступен только в Windows; здесь используйте восстановление DNS выбранной службы")
	}
	path := filepath.Join(dir, "dns-backup.json")
	if operation == "restore" {
		raw, e := os.ReadFile(path)
		if e != nil {
			return "", errors.New("нет копии прежнего DNS")
		}
		var b dnsBackup
		if json.Unmarshal(raw, &b) != nil || b.Version != 1 || b.OS != runtime.GOOS {
			return "", errors.New("копия DNS не соответствует этой системе")
		}
		now, e := currentDNS(ctx, b.Interface)
		if e != nil {
			return "", e
		}
		if len(now) != 1 || now[0] != b.Applied {
			return "", errors.New("DNS изменён вне Dagami; автоматический откат отменён")
		}
		if e = applyDNS(ctx, b.Interface, b.Before); e != nil {
			return "", e
		}
		restored, e := currentDNS(ctx, b.Interface)
		if e != nil || strings.Join(restored, " ") != strings.Join(b.Before, " ") {
			return "", errors.New("откат не подтверждён; копия сохранена")
		}
		if e = os.Rename(path, path+".restored-"+ID()); e != nil {
			return "", e
		}
		return "Прежний DNS восстановлен", nil
	}
	if iface == "" || len(iface) > 128 || strings.ContainsAny(iface, "\x00\r\n") {
		return "", errors.New("укажите точное имя сетевого интерфейса или службы")
	}
	if _, e := os.Stat(path); e == nil {
		return "", errors.New("сначала восстановите предыдущую настройку DNS")
	}
	before, e := currentDNS(ctx, iface)
	if e != nil {
		return "", e
	}
	backup := dnsBackup{Version: 1, OS: runtime.GOOS, Interface: iface, Before: before, Applied: server}
	raw, _ := json.MarshalIndent(backup, "", "  ")
	if e = atomicWrite(path, raw, 0600); e != nil {
		return "", e
	}
	if e = applyDNS(ctx, iface, []string{server}); e != nil {
		return "", e
	}
	after, e := currentDNS(ctx, iface)
	if e != nil || len(after) != 1 || after[0] != server {
		rollback := applyDNS(context.Background(), iface, before)
		if rollback != nil {
			return "", errors.New("запись и откат не подтверждены; копия сохранена")
		}
		return "", errors.New("DNS не подтвердился; выполнена попытка отката, проверьте статус")
	}
	return "DNS записан и подтверждён; прежние значения сохранены", nil
}
