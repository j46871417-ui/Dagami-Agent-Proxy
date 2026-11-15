//go:build !windows && !routing_only

package portable

import (
	"context"
	"errors"
	"net"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"time"
)

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
		_, tail, ok := strings.Cut(text, ":")
		if !ok {
			return nil, errors.New("неизвестный ответ resolvectl")
		}
		text = tail
	}
	result := []string{}
	for _, v := range strings.Fields(text) {
		if net.ParseIP(v) == nil {
			return nil, errors.New("не удалось однозначно прочитать прежний DNS; запись отменена")
		}
		result = append(result, net.ParseIP(v).String())
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
	return changeUnixDNS(ctx, dir, runtime.GOOS, operation, server, iface, currentDNS, applyDNS)
}
