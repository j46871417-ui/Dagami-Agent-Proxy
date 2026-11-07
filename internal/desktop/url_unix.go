//go:build linux || darwin

package desktop

import (
	"os/exec"
	"runtime"
)

func openTelegram(link string) error {
	program := "xdg-open"
	if runtime.GOOS == "darwin" {
		program = "/usr/bin/open"
	}
	return exec.Command(program, link).Run()
}
