package desktop

import (
	"errors"
	"runtime"
	"strings"
	"syscall"
	"unicode/utf8"
	"unsafe"
)

var (
	user32        = syscall.NewLazyDLL("user32.dll")
	kernel32      = syscall.NewLazyDLL("kernel32.dll")
	gdi32         = syscall.NewLazyDLL("gdi32.dll")
	createWindow  = user32.NewProc("CreateWindowExW")
	sendMessage   = user32.NewProc("SendMessageW")
	destroyWindow = user32.NewProc("DestroyWindow")
	getText       = user32.NewProc("GetWindowTextW")
	getTextLength = user32.NewProc("GetWindowTextLengthW")
	postMessage   = user32.NewProc("PostMessageW")
)

const (
	winWidth   = 800
	winHeight  = 560
	wmCommand  = 0x0111
	wmDestroy  = 0x0002
	wmDispatch = 0x8001
	buttonBase = 100
)

type winClass struct {
	size, style                uint32
	procedure                  uintptr
	classExtra, windowExtra    int32
	instance, icon, cursor, bg uintptr
	menu, name, smallIcon      uintptr
}

type winMessage struct {
	hwnd           uintptr
	message        uint32
	wparam, lparam uintptr
	time           uint32
	x, y           int32
	private        uint32
}

type winWindow struct {
	hwnd, instance, font uintptr
	controls, fields     []uintptr
	list                 uintptr
	actions              []button
	jobs                 chan func()
}

func wide(s string) *uint16 {
	p, _ := syscall.UTF16PtrFromString(strings.ReplaceAll(s, "\x00", ""))
	return p
}

func openTelegram(link string) error {
	shell := syscall.NewLazyDLL("shell32.dll").NewProc("ShellExecuteW")
	value, _, _ := shell.Call(0, uintptr(unsafe.Pointer(wide("open"))), uintptr(unsafe.Pointer(wide(link))), 0, 0, 1)
	if value <= 32 {
		return errors.New("Windows не смогла открыть ссылку Telegram")
	}
	return nil
}

func runWindow(ready func(window)) error {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	w := &winWindow{jobs: make(chan func(), 1)}
	w.instance, _, _ = kernel32.NewProc("GetModuleHandleW").Call(0)
	w.font, _, _ = gdi32.NewProc("GetStockObject").Call(17) // DEFAULT_GUI_FONT
	cursor, _, _ := user32.NewProc("LoadCursorW").Call(0, 32512)
	className := wide("DagamiDesktopWindow")
	class := winClass{procedure: syscall.NewCallback(w.process), instance: w.instance, cursor: cursor, bg: 16, name: uintptr(unsafe.Pointer(className))}
	class.size = uint32(unsafe.Sizeof(class))
	if result, _, err := user32.NewProc("RegisterClassExW").Call(uintptr(unsafe.Pointer(&class))); result == 0 {
		return err
	}
	defer func() {
		user32.NewProc("UnregisterClassW").Call(uintptr(unsafe.Pointer(className)), w.instance)
	}()
	runtime.KeepAlive(className)
	// Caption, system menu and minimize button. Dimensions fit the same native form on each page.
	w.hwnd, _, _ = createWindow.Call(0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(wide("Dagami Agent Proxy"))), 0x00CA0000,
		0x80000000, 0x80000000, winWidth, winHeight, 0, 0, w.instance, 0)
	if w.hwnd == 0 {
		return errors.New("Windows не смогла создать окно Dagami")
	}
	ready(w)
	user32.NewProc("ShowWindow").Call(w.hwnd, 5)
	getMessage := user32.NewProc("GetMessageW")
	translate := user32.NewProc("TranslateMessage")
	dispatch := user32.NewProc("DispatchMessageW")
	dialog := user32.NewProc("IsDialogMessageW")
	var msg winMessage
	for {
		ptr := uintptr(unsafe.Pointer(&msg))
		result, _, err := getMessage.Call(ptr, 0, 0, 0)
		switch int32(result) {
		case -1:
			return err
		case 0:
			return nil
		}
		if handled, _, _ := dialog.Call(w.hwnd, ptr); handled == 0 {
			translate.Call(ptr)
			dispatch.Call(ptr)
		}
	}
}

func (w *winWindow) process(hwnd, msg, wp, lp uintptr) uintptr {
	switch msg {
	case wmCommand:
		index := int(wp&0xffff) - buttonBase
		if wp>>16 == 0 && index >= 0 && index < len(w.actions) {
			selected := -1
			if w.list != 0 {
				value, _, _ := sendMessage.Call(w.list, 0x0188, 0, 0) // LB_GETCURSEL
				selected = int(int32(value))
			}
			values := make([]string, len(w.fields))
			for i, handle := range w.fields {
				length, _, _ := getTextLength.Call(handle)
				buf := make([]uint16, length+1)
				getText.Call(handle, uintptr(unsafe.Pointer(&buf[0])), length+1)
				values[i] = syscall.UTF16ToString(buf)
			}
			w.actions[index].click(selected, values)
			return 0
		}
	case wmDispatch:
		select {
		case job := <-w.jobs:
			job()
		default:
		}
		return 0
	case wmDestroy:
		user32.NewProc("PostQuitMessage").Call(0)
		return 0
	}
	result, _, _ := user32.NewProc("DefWindowProcW").Call(hwnd, msg, wp, lp)
	return result
}

func (w *winWindow) post(job func()) {
	w.jobs <- job
	postMessage.Call(w.hwnd, wmDispatch, 0, 0)
}

func (w *winWindow) control(class, text string, style uintptr, x, y, width, height int, id int) uintptr {
	handle, _, _ := createWindow.Call(0, uintptr(unsafe.Pointer(wide(class))), uintptr(unsafe.Pointer(wide(text))), style|0x50000000,
		uintptr(x), uintptr(y), uintptr(width), uintptr(height), w.hwnd, uintptr(id), w.instance, 0)
	w.controls = append(w.controls, handle)
	sendMessage.Call(handle, 0x0030, w.font, 1) // WM_SETFONT
	return handle
}

func (w *winWindow) buttons(buttons []button, y int) {
	x := 20
	for _, item := range buttons {
		width := max(94, utf8.RuneCountInString(item.label)*7+22)
		if x+width > 760 {
			x, y = 20, y+36
		}
		w.control("BUTTON", item.label, 0x00010000, x, y, width, 30, buttonBase+len(w.actions))
		w.actions = append(w.actions, item)
		x += width + 8
	}
}

func (w *winWindow) show(v view) {
	for _, h := range w.controls {
		destroyWindow.Call(h)
	}
	w.controls, w.actions, w.fields, w.list = nil, nil, nil, 0
	w.buttons(v.nav, 18)
	w.control("STATIC", v.title, 0, 20, 70, 750, 24, 0)
	w.control("STATIC", v.detail, 0, 20, 100, 745, 44, 0)
	if len(v.items) > 0 {
		w.list = w.control("LISTBOX", "", 0x00A10101, 20, 151, 744, 270, 0)
		for _, row := range v.items {
			sendMessage.Call(w.list, 0x0180, 0, uintptr(unsafe.Pointer(wide(row)))) // LB_ADDSTRING
		}
		sendMessage.Call(w.list, 0x0186, uintptr(v.selected), 0) // LB_SETCURSEL
	}
	for i, field := range v.fields {
		y := 155 + i*68
		w.control("STATIC", field.label, 0, 20, y, 744, 22, 0)
		w.fields = append(w.fields, w.control("EDIT", field.value, 0x00810080, 20, y+24, 744, 29, 0))
	}
	if v.report != "" {
		w.control("EDIT", strings.ReplaceAll(v.report, "\n", "\r\n"), 0x00A10844, 20, 150, 744, 270, 0)
	}
	w.buttons(v.actions, 445)
	if len(w.fields) > 0 {
		user32.NewProc("SetFocus").Call(w.fields[0])
	} else if w.list != 0 {
		user32.NewProc("SetFocus").Call(w.list)
	}
}
