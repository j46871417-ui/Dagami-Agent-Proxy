//go:build windows

package portable

import (
	"encoding/base64"
	"errors"
	"os"
	"os/exec"
	"sync"
	"syscall"
	"unsafe"
)

var crypt32 = syscall.NewLazyDLL("crypt32.dll")
var kernel32 = syscall.NewLazyDLL("kernel32.dll")

type blob struct {
	Size uint32
	Data *byte
}

func protectSecret(id, secret string) (string, error) {
	raw := []byte(secret)
	in := blob{uint32(len(raw)), &raw[0]}
	out := blob{}
	r, _, _ := crypt32.NewProc("CryptProtectData").Call(uintptr(unsafe.Pointer(&in)), 0, 0, 0, 0, 1, uintptr(unsafe.Pointer(&out)))
	if r == 0 {
		return "", errors.New("Windows не смог защитить реквизиты")
	}
	defer kernel32.NewProc("LocalFree").Call(uintptr(unsafe.Pointer(out.Data)))
	return base64.StdEncoding.EncodeToString(unsafe.Slice(out.Data, int(out.Size))), nil
}
func unprotectSecret(id, value string) (string, error) {
	raw, e := base64.StdEncoding.DecodeString(value)
	if e != nil || len(raw) == 0 {
		return "", errors.New("повреждены защищённые реквизиты")
	}
	in := blob{uint32(len(raw)), &raw[0]}
	out := blob{}
	r, _, _ := crypt32.NewProc("CryptUnprotectData").Call(uintptr(unsafe.Pointer(&in)), 0, 0, 0, 0, 1, uintptr(unsafe.Pointer(&out)))
	if r == 0 {
		return "", errors.New("реквизиты недоступны этому пользователю Windows")
	}
	defer kernel32.NewProc("LocalFree").Call(uintptr(unsafe.Pointer(out.Data)))
	return string(unsafe.Slice(out.Data, int(out.Size))), nil
}
func replaceFile(from, to string) error {
	f, e := syscall.UTF16PtrFromString(from)
	if e != nil {
		return e
	}
	t, e := syscall.UTF16PtrFromString(to)
	if e != nil {
		return e
	}
	r, _, e := kernel32.NewProc("MoveFileExW").Call(uintptr(unsafe.Pointer(f)), uintptr(unsafe.Pointer(t)), 9)
	if r == 0 {
		return e
	}
	return nil
}
func prepareProcess(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000} }
func manageProcess(cmd *exec.Cmd) (func() error, error) {
	job, _, e := kernel32.NewProc("CreateJobObjectW").Call(0, 0)
	if job == 0 {
		return nil, e
	}
	// JOBOBJECT_EXTENDED_LIMIT_INFORMATION on 64-bit Windows.
	var limits [144]byte
	*(*uint32)(unsafe.Pointer(&limits[16])) = 0x2000
	r, _, e := kernel32.NewProc("SetInformationJobObject").Call(job, 9, uintptr(unsafe.Pointer(&limits[0])), uintptr(len(limits)))
	if r == 0 {
		syscall.CloseHandle(syscall.Handle(job))
		return nil, e
	}
	process, e := syscall.OpenProcess(0x0100|0x0001, false, uint32(cmd.Process.Pid))
	if e != nil {
		syscall.CloseHandle(syscall.Handle(job))
		return nil, e
	}
	defer syscall.CloseHandle(process)
	r, _, e = kernel32.NewProc("AssignProcessToJobObject").Call(job, uintptr(process))
	if r == 0 {
		syscall.CloseHandle(syscall.Handle(job))
		return nil, errors.New("не удалось изолировать дерево процессов; запуск отменён")
	}
	var once sync.Once
	return func() error {
		var err error
		once.Do(func() { err = syscall.CloseHandle(syscall.Handle(job)) })
		return err
	}, nil
}
func openBrowser(address string) error {
	target, err := syscall.UTF16PtrFromString(address)
	if err != nil {
		return err
	}
	verb, _ := syscall.UTF16PtrFromString("open")
	shell := syscall.NewLazyDLL("shell32.dll").NewProc("ShellExecuteW")
	result, _, _ := shell.Call(0, uintptr(unsafe.Pointer(verb)), uintptr(unsafe.Pointer(target)), 0, 0, 1)
	if result <= 32 {
		return errors.New("Windows не смог открыть браузер по умолчанию")
	}
	return nil
}
func resolveExecutable(path string) (string, error) {
	fi, e := os.Stat(path)
	if e != nil {
		return "", e
	}
	if !fi.Mode().IsRegular() {
		return "", errors.New("выберите EXE")
	}
	return path, nil
}
