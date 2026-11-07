package desktop

import (
	"fmt"
	"runtime"
	"strings"
	"sync"
	"unicode/utf8"

	"github.com/ebitengine/purego"
	"github.com/ebitengine/purego/objc"
)

const macWidth, macHeight = 780, 500

type macPoint struct{ X, Y float64 }
type macSize struct{ Width, Height float64 }
type macRect struct {
	Origin macPoint
	Size   macSize
}

func rect(x, y, width, height float64) macRect {
	return macRect{macPoint{x, macHeight - y - height}, macSize{width, height}}
}

func init() { runtime.LockOSThread() }

func send(target objc.ID, selector string, args ...any) objc.ID {
	return target.Send(objc.RegisterName(selector), args...)
}

func cocoaClass(name string) objc.ID { return objc.ID(objc.GetClass(name)) }

func cocoaString(text string) objc.ID {
	return send(cocoaClass("NSString"), "stringWithUTF8String:", strings.ReplaceAll(text, "\x00", ""))
}

func cocoaNew(class string) objc.ID { return send(send(cocoaClass(class), "alloc"), "init") }

type cocoaWindow struct {
	app, window, root, delegate, picker objc.ID
	fields                              []objc.ID
	actions                             []button
	jobs                                chan func()
	mu                                  sync.Mutex
	closed                              bool
}

func runWindow(ready func(window)) error {
	if _, err := purego.Dlopen("/System/Library/Frameworks/Cocoa.framework/Cocoa", purego.RTLD_GLOBAL|purego.RTLD_LAZY); err != nil {
		return err
	}
	pool := cocoaNew("NSAutoreleasePool")
	defer send(pool, "drain")
	w := &cocoaWindow{app: send(cocoaClass("NSApplication"), "sharedApplication"), jobs: make(chan func(), 1)}
	class, err := objc.RegisterClass("DagamiDesktopDelegate", objc.GetClass("NSObject"), nil, nil, []objc.MethodDef{
		{Cmd: objc.RegisterName("dagamiClick:"), Fn: func(_ objc.ID, _ objc.SEL, sender objc.ID) { w.clicked(sender) }},
		{Cmd: objc.RegisterName("dagamiDispatch:"), Fn: func(_ objc.ID, _ objc.SEL, _ objc.ID) { (<-w.jobs)() }},
		{Cmd: objc.RegisterName("windowWillClose:"), Fn: func(_ objc.ID, _ objc.SEL, _ objc.ID) {
			w.mu.Lock()
			w.closed = true
			w.mu.Unlock()
			send(w.app, "stop:", objc.ID(0))
		}},
	})
	if err != nil {
		return err
	}
	w.delegate = send(send(objc.ID(class), "alloc"), "init")
	defer send(w.delegate, "release")
	send(w.app, "setActivationPolicy:", 0)
	w.window = send(send(cocoaClass("NSWindow"), "alloc"), "initWithContentRect:styleMask:backing:defer:", rect(0, 0, macWidth, macHeight), 7, 2, false)
	defer send(w.window, "release")
	send(w.window, "setReleasedWhenClosed:", false)
	send(w.window, "setTitle:", cocoaString("Dagami Agent Proxy"))
	send(w.window, "setDelegate:", w.delegate)
	ready(w)
	send(w.window, "center")
	send(w.window, "makeKeyAndOrderFront:", objc.ID(0))
	send(w.app, "activateIgnoringOtherApps:", true)
	send(w.app, "run")
	return nil
}

func (w *cocoaWindow) post(job func()) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return
	}
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	w.jobs <- job
	pool := cocoaNew("NSAutoreleasePool")
	defer send(pool, "drain")
	send(w.delegate, "performSelectorOnMainThread:withObject:waitUntilDone:", objc.RegisterName("dagamiDispatch:"), objc.ID(0), false)
}

func (w *cocoaWindow) clicked(sender objc.ID) {
	index := objc.Send[int](sender, objc.RegisterName("tag"))
	selected := -1
	if w.picker != 0 {
		selected = objc.Send[int](w.picker, objc.RegisterName("indexOfSelectedItem"))
	}
	values := make([]string, len(w.fields))
	if len(w.fields) > 0 {
		send(w.window, "makeFirstResponder:", objc.ID(0))
	}
	for i, field := range w.fields {
		values[i] = objc.Send[string](send(field, "stringValue"), objc.RegisterName("UTF8String"))
	}
	if index >= 0 && index < len(w.actions) {
		w.actions[index].click(selected, values)
	}
}

func (w *cocoaWindow) add(widget objc.ID, frame macRect) {
	send(widget, "setFrame:", frame)
	send(w.root, "addSubview:", widget)
}

func (w *cocoaWindow) buttons(buttons []button, y float64) {
	x := 20.0
	for _, item := range buttons {
		width := float64(max(94, utf8.RuneCountInString(item.label)*8+20))
		if x+width > macWidth-20 {
			x, y = 20, y+34
		}
		widget := send(cocoaClass("NSButton"), "buttonWithTitle:target:action:", cocoaString(item.label), w.delegate, objc.RegisterName("dagamiClick:"))
		send(widget, "setTag:", len(w.actions))
		w.actions = append(w.actions, item)
		w.add(widget, rect(x, y, width, 30))
		x += width + 6
	}
}

func (w *cocoaWindow) show(v view) {
	pool := cocoaNew("NSAutoreleasePool")
	defer send(pool, "drain")
	w.picker, w.actions, w.fields = 0, nil, nil
	w.root = cocoaNew("NSView")
	send(w.window, "setContentView:", w.root)
	defer send(w.root, "release")
	w.buttons(v.nav, 15)
	title := send(cocoaClass("NSTextField"), "labelWithString:", cocoaString(v.title))
	send(title, "setFont:", send(cocoaClass("NSFont"), "boldSystemFontOfSize:", 20.0))
	w.add(title, rect(20, 62, 740, 28))
	detail := send(cocoaClass("NSTextField"), "wrappingLabelWithString:", cocoaString(v.detail))
	w.add(detail, rect(20, 100, 740, 44))
	if len(v.items) > 0 {
		w.picker = send(send(cocoaClass("NSPopUpButton"), "alloc"), "initWithFrame:pullsDown:", rect(20, 152, 740, 32), false)
		for i, item := range v.items {
			send(w.picker, "addItemWithTitle:", cocoaString(fmt.Sprintf("%d. %s", i+1, item)))
		}
		send(w.picker, "selectItemAtIndex:", v.selected)
		w.add(w.picker, rect(20, 152, 740, 32))
		send(w.picker, "release")
	}
	for i, field := range v.fields {
		y := float64(153 + i*68)
		label := send(cocoaClass("NSTextField"), "labelWithString:", cocoaString(field.label))
		entry := send(cocoaClass("NSTextField"), "textFieldWithString:", cocoaString(field.value))
		w.add(label, rect(20, y, 740, 22))
		w.add(entry, rect(20, y+25, 740, 28))
		w.fields = append(w.fields, entry)
	}
	if v.report != "" {
		scroll := cocoaNew("NSScrollView")
		send(scroll, "setHasVerticalScroller:", true)
		send(scroll, "setBorderType:", 1)
		text := send(send(cocoaClass("NSTextView"), "alloc"), "initWithFrame:", macRect{Size: macSize{720, 252}})
		send(text, "setEditable:", false)
		send(text, "setVerticallyResizable:", true)
		send(text, "setString:", cocoaString(v.report))
		send(scroll, "setDocumentView:", text)
		w.add(scroll, rect(20, 148, 740, 256))
		send(text, "release")
		send(scroll, "release")
	}
	w.buttons(v.actions, 419)
	if len(w.fields) > 0 {
		send(w.window, "makeFirstResponder:", w.fields[0])
	}
}
