//go:build linux || darwin

package desktop

import (
	"os/exec"
	"runtime"
	"time"
)

func openTelegram(link string) error {
	program := "xdg-open"
	if runtime.GOOS == "darwin" {
		program = "/usr/bin/open"
	}
	cmd := exec.Command(program, link)
	if err := cmd.Start(); err != nil {
		return err
	}
	// xdg-open can stay alive for the lifetime of the browser.
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	timer := time.NewTimer(time.Second)
	defer timer.Stop()
	select {
	case err := <-done:
		return err
	case <-timer.C:
		return nil
	}
}
