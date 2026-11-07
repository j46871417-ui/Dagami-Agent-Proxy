//go:build !windows

package portable

import (
	"bytes"
	"context"
	"encoding/xml"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"time"
)

func keychainCommand(args []string, input string) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, args[0], args[1:]...)
	if input != "" {
		cmd.Stdin = strings.NewReader(input)
	}
	var out bytes.Buffer
	cmd.Stdout = &out
	if e := cmd.Run(); e != nil {
		return "", errors.New("хранилище паролей ОС недоступно; откройте Keychain / Secret Service")
	}
	return strings.TrimSuffix(out.String(), "\n"), nil
}
func protectSecret(id, secret string) (string, error) {
	if runtime.GOOS == "darwin" {
		quote := func(s string) string {
			return `"` + strings.NewReplacer(`\`, `\\`, `"`, `\"`, "\r", "", "\n", "").Replace(s) + `"`
		}
		_, e := keychainCommand([]string{"/usr/bin/security", "-i"}, "add-generic-password -U -s ru.dagami.native -a "+quote(id)+" -w "+quote(secret)+"\n")
		if e != nil {
			return "", e
		}
	} else {
		if _, e := keychainCommand([]string{"secret-tool", "store", "--label=Dagami Agent Proxy", "service", "ru.dagami.native", "profile", id}, secret); e != nil {
			return "", e
		}
	}
	got, e := unprotectSecret(id, "keyring")
	if e != nil || got != secret {
		return "", errors.New("проверка сохранения пароля не прошла")
	}
	return "keyring", nil
}
func unprotectSecret(id, value string) (string, error) {
	if value != "keyring" {
		return "", errors.New("неизвестный формат защищённых данных")
	}
	if runtime.GOOS == "darwin" {
		return keychainCommand([]string{"/usr/bin/security", "find-generic-password", "-s", "ru.dagami.native", "-a", id, "-w"}, "")
	}
	return keychainCommand([]string{"secret-tool", "lookup", "service", "ru.dagami.native", "profile", id}, "")
}
func replaceFile(from, to string) error { return os.Rename(from, to) }
func prepareProcess(cmd *exec.Cmd)      { cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true} }
func manageProcess(cmd *exec.Cmd) (func() error, error) {
	var once sync.Once
	return func() error {
		var e error
		once.Do(func() { e = syscall.Kill(-cmd.Process.Pid, syscall.SIGTERM) })
		return e
	}, nil
}
func openBrowser(address string) error {
	command := "xdg-open"
	if runtime.GOOS == "darwin" {
		command = "open"
	}
	return exec.Command(command, address).Run()
}
func resolveExecutable(path string) (string, error) {
	if runtime.GOOS == "darwin" && strings.HasSuffix(path, ".app") {
		data, e := os.ReadFile(filepath.Join(path, "Contents", "Info.plist"))
		if e != nil {
			return "", e
		}
		decoder := xml.NewDecoder(bytes.NewReader(data))
		found := false
		for {
			token, e := decoder.Token()
			if e == io.EOF {
				break
			}
			if e != nil {
				return "", errors.New("нужен XML Info.plist или прямой путь к исполняемому файлу")
			}
			if start, ok := token.(xml.StartElement); ok && (start.Name.Local == "key" || start.Name.Local == "string") {
				var value string
				if e = decoder.DecodeElement(&value, &start); e != nil {
					return "", e
				}
				if start.Name.Local == "key" {
					found = value == "CFBundleExecutable"
				} else if found {
					if filepath.Base(value) != value {
						return "", errors.New("неверный CFBundleExecutable")
					}
					path = filepath.Join(path, "Contents", "MacOS", value)
					break
				}
			}
		}
	}
	fi, e := os.Stat(path)
	if e != nil {
		return "", e
	}
	if !fi.Mode().IsRegular() || fi.Mode()&0111 == 0 {
		return "", errors.New("нет исполняемого файла или права запуска")
	}
	return path, nil
}
