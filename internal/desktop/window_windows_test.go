package desktop

import "testing"

func TestWin32WindowUsesNativeControls(t *testing.T) {
	clicked := false
	err := runWindow(func(window window) {
		w := window.(*winWindow)
		user32.NewProc("SetWindowPos").Call(w.hwnd, 0, ^uintptr(29999), ^uintptr(29999), 0, 0, 0x0015)
		w.show(view{title: "Fixture", items: []string{"First", "Second"}, selected: 1, fields: []field{{"Name", "Тест"}},
			actions: []button{{"Check", func(index int, fields []string) {
				clicked = index == 1 && len(fields) == 1 && fields[0] == "Тест"
			}}}})
		w.post(func() {
			sendMessage.Call(w.hwnd, wmCommand, buttonBase, 0)
			destroyWindow.Call(w.hwnd)
		})
	})
	if err != nil || !clicked {
		t.Fatalf("Win32 control dispatch: clicked=%t error=%v", clicked, err)
	}
}
