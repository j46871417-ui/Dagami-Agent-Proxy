package desktop

import (
	"strings"
	"testing"
)

type pairingFixtureWindow struct{ current view }

func (w *pairingFixtureWindow) show(v view)    { w.current = v }
func (w *pairingFixtureWindow) post(fn func()) { fn() }

func TestPairingMissingGatewayKeepsActionsVisible(t *testing.T) {
	w := &pairingFixtureWindow{}
	d := &desktop{window: w}
	d.showManagedPair("", "", "")
	w.current.actions[1].click(0, []string{""})
	if !strings.Contains(w.current.detail, "Укажите HTTPS-адрес") || len(w.current.fields) != 1 ||
		len(w.current.actions) != 3 || w.current.actions[0].label != "Открыть Telegram" {
		t.Fatalf("missing gateway did not leave a usable pairing form: %+v", w.current)
	}
}

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
