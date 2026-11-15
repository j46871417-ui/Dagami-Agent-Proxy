//go:build !routing_only

package portable

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"slices"
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

func validDNSInterface(iface string) bool {
	return iface != "" && len(iface) <= 128 && !strings.HasPrefix(iface, "-") && !strings.ContainsAny(iface, "\x00\r\n")
}
func normalizeDNS(values []string) ([]string, error) {
	normalized := make([]string, 0, len(values))
	for _, v := range values {
		ip := net.ParseIP(v)
		if ip == nil {
			return nil, errors.New("некорректный адрес в журнале DNS")
		}
		normalized = append(normalized, ip.String())
	}
	return normalized, nil
}

type dnsRead func(context.Context, string) ([]string, error)
type dnsWrite func(context.Context, string, []string) error

func changeUnixDNS(ctx context.Context, dir, platform, operation, server, iface string, read dnsRead, write dnsWrite) (string, error) {
	path := filepath.Join(dir, "dns-backup.json")
	archive := func() error { return os.Rename(path, path+".restored-"+ID()) }
	stat, statErr := os.Lstat(path)
	if operation == "restore" {
		if statErr != nil || !stat.Mode().IsRegular() || stat.Size() > 32768 {
			return "", errors.New("нет доступного журнала DNS")
		}
		raw, err := os.ReadFile(path)
		if err != nil {
			return "", err
		}
		var backup dnsBackup
		if json.Unmarshal(raw, &backup) != nil || backup.Version != 1 || backup.OS != platform || !validDNSInterface(backup.Interface) || net.ParseIP(backup.Applied) == nil || len(backup.Before) > 32 {
			return "", errors.New("журнал DNS повреждён или относится к другой системе")
		}
		before, err := normalizeDNS(backup.Before)
		if err != nil {
			return "", err
		}
		now, err := read(ctx, backup.Interface)
		if err != nil {
			return "", err
		}
		if !slices.Equal(now, before) {
			if !slices.Equal(now, []string{net.ParseIP(backup.Applied).String()}) {
				return "", errors.New("DNS изменён вне Dagami; журнал сохранён")
			}
			if err = write(ctx, backup.Interface, before); err != nil {
				return "", err
			}
			now, err = read(ctx, backup.Interface)
			if err != nil || !slices.Equal(now, before) {
				return "", errors.New("откат не подтверждён; журнал сохранён")
			}
		}
		if err = archive(); err != nil {
			return "", err
		}
		return "Прежний DNS восстановлен", nil
	}
	if operation != "apply" {
		return "", errors.New("используйте восстановление DNS выбранной службы")
	}
	if !validDNSInterface(iface) {
		return "", errors.New("укажите точное имя сетевой службы или интерфейса")
	}
	ip := net.ParseIP(server)
	if ip == nil {
		return "", errors.New("укажите IP DNS-сервера")
	}
	server = ip.String()
	if !os.IsNotExist(statErr) {
		return "", errors.New("сначала восстановите DNS из существующего журнала")
	}
	before, err := read(ctx, iface)
	if err != nil {
		return "", err
	}
	before, err = normalizeDNS(before)
	if err != nil {
		return "", err
	}
	target := []string{server}
	if slices.Equal(before, target) {
		return "DNS уже соответствует выбранному адресу", nil
	}
	backup := dnsBackup{Version: 1, OS: platform, Interface: iface, Before: before, Applied: server}
	data, err := json.MarshalIndent(backup, "", "  ")
	if err != nil {
		return "", err
	}
	if err = atomicWrite(path, data, 0600); err != nil {
		return "", err
	}
	writeErr := write(ctx, iface, target)
	// Verify a cancelled/failed external command before deciding whether its
	// journal can be retired. A caller may have lost the window after the write.
	verifyCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 10*time.Second)
	defer cancel()
	after, err := read(verifyCtx, iface)
	if err != nil {
		return "", errors.New("не удалось проверить применение DNS; журнал сохранён")
	}
	if slices.Equal(after, target) && writeErr == nil {
		return "DNS записан и подтверждён; прежние значения сохранены", nil
	}
	if slices.Equal(after, before) {
		if err = archive(); err != nil {
			return "", err
		}
		return "", errors.New("DNS остался прежним")
	}
	return "", errors.New("изменение DNS не подтверждено; доступен откат из сохранённого журнала")
}
