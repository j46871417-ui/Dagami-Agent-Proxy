package desktop

import (
	"errors"
	"runtime"

	"github.com/ebitengine/purego"
)

type gtkAPI struct {
	initCheck     func(uintptr, uintptr) int32
	main          func()
	quit          func()
	windowNew     func(int32) uintptr
	windowTitle   func(uintptr, string)
	windowSize    func(uintptr, int32, int32)
	boxNew        func(int32, int32) uintptr
	boxPack       func(uintptr, uintptr, int32, int32, uint32)
	containerAdd  func(uintptr, uintptr)
	border        func(uintptr, uint32)
	labelNew      func(string) uintptr
	labelAlign    func(uintptr, float32)
	labelWrap     func(uintptr, int32)
	buttonNew     func(string) uintptr
	entryNew      func() uintptr
	entrySet      func(uintptr, string)
	entryGet      func(uintptr) string
	entryVisible  func(uintptr, int32)
	present       func(uintptr)
	flowNew       func() uintptr
	flowSelection func(uintptr, int32)
	flowMax       func(uintptr, uint32)
	sizeRequest   func(uintptr, int32, int32)
	listNew       func() uintptr
	listRow       func(uintptr, int32) uintptr
	listSelect    func(uintptr, uintptr)
	listSelected  func(uintptr) uintptr
	rowIndex      func(uintptr) int32
	scrollNew     func(uintptr, uintptr) uintptr
	textNew       func() uintptr
	textBuffer    func(uintptr) uintptr
	textSet       func(uintptr, string, int32)
	textEditable  func(uintptr, int32)
	textWrap      func(uintptr, int32)
	showAll       func(uintptr)
	destroy       func(uintptr)
	connect       func(uintptr, string, uintptr, uintptr, uintptr, int32) uint64
	idleAdd       func(uintptr, uintptr) uint32
}

func loadGTK(handle uintptr) gtkAPI {
	var g gtkAPI
	for name, target := range map[string]any{
		"gtk_init_check": &g.initCheck, "gtk_main": &g.main, "gtk_main_quit": &g.quit,
		"gtk_window_new": &g.windowNew, "gtk_window_set_title": &g.windowTitle, "gtk_window_set_default_size": &g.windowSize,
		"gtk_box_new": &g.boxNew, "gtk_box_pack_start": &g.boxPack,
		"gtk_container_add": &g.containerAdd, "gtk_container_set_border_width": &g.border,
		"gtk_label_new": &g.labelNew, "gtk_label_set_xalign": &g.labelAlign, "gtk_label_set_line_wrap": &g.labelWrap,
		"gtk_button_new_with_label": &g.buttonNew, "gtk_entry_new": &g.entryNew,
		"gtk_entry_set_visibility": &g.entryVisible, "gtk_window_present": &g.present, "gtk_flow_box_new": &g.flowNew, "gtk_flow_box_set_selection_mode": &g.flowSelection, "gtk_flow_box_set_max_children_per_line": &g.flowMax, "gtk_widget_set_size_request": &g.sizeRequest,
		"gtk_entry_set_text": &g.entrySet, "gtk_entry_get_text": &g.entryGet,
		"gtk_list_box_new": &g.listNew, "gtk_list_box_get_row_at_index": &g.listRow,
		"gtk_list_box_select_row": &g.listSelect, "gtk_list_box_get_selected_row": &g.listSelected, "gtk_list_box_row_get_index": &g.rowIndex,
		"gtk_scrolled_window_new": &g.scrollNew, "gtk_text_view_new": &g.textNew, "gtk_text_view_get_buffer": &g.textBuffer,
		"gtk_text_buffer_set_text": &g.textSet, "gtk_text_view_set_editable": &g.textEditable, "gtk_text_view_set_wrap_mode": &g.textWrap,
		"gtk_widget_show_all": &g.showAll, "gtk_widget_destroy": &g.destroy,
		"g_signal_connect_data": &g.connect, "g_idle_add": &g.idleAdd,
	} {
		purego.RegisterLibFunc(target, handle, name)
	}
	return g
}

type gtkWindow struct {
	gtkAPI
	widget, root, list, page uintptr
	fields                   []uintptr
	actions                  []button
	click, dispatch          uintptr
	queue                    dispatchQueue
}

func runWindow(ready func(window)) error {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	handle, err := purego.Dlopen("libgtk-3.so.0", purego.RTLD_NOW|purego.RTLD_LOCAL)
	if err != nil {
		return errors.New("для окна Dagami установите GTK 3 (libgtk-3.so.0)")
	}
	g := loadGTK(handle)
	if g.initCheck(0, 0) == 0 {
		return errors.New("GTK не смог подключиться к графической сессии")
	}
	w := &gtkWindow{gtkAPI: g, widget: g.windowNew(0)}
	w.click = purego.NewCallback(func(_ uintptr, id uintptr) {
		index := -1
		if w.list != 0 {
			if row := w.listSelected(w.list); row != 0 {
				index = int(w.rowIndex(row))
			}
		}
		values := make([]string, len(w.fields))
		for i, field := range w.fields {
			values[i] = w.entryGet(field)
		}
		w.actions[id].click(index, values)
	})
	w.dispatch = purego.NewCallback(func(uintptr) int32 { w.queue.drain(); return 0 })
	closed := purego.NewCallback(func(uintptr, uintptr) { w.queue.close(); g.quit() })
	g.connect(w.widget, "destroy", closed, 0, 0, 0)
	g.windowTitle(w.widget, "Dagami Agent Proxy")
	g.windowSize(w.widget, 800, 520)
	ready(w)
	g.main()
	return nil
}

func (w *gtkWindow) post(job func()) {
	if w.queue.push(job) {
		w.idleAdd(w.dispatch, 0)
	}
}

func (w *gtkWindow) label(text string) uintptr {
	label := w.labelNew(text)
	w.labelAlign(label, 0)
	w.labelWrap(label, 1)
	return label
}

func (w *gtkWindow) buttons(buttons []button) {
	if len(buttons) == 0 {
		return
	}
	row := w.flowNew()
	w.flowSelection(row, 0)
	w.flowMax(row, 4)
	for _, b := range buttons {
		widget := w.buttonNew(b.label)
		w.connect(widget, "clicked", w.click, uintptr(len(w.actions)), 0, 0)
		w.actions = append(w.actions, b)
		w.containerAdd(row, widget)
	}
	w.boxPack(w.root, row, 0, 0, 0)
}

func (w *gtkWindow) show(v view) {
	if w.page != 0 {
		w.destroy(w.page)
	}
	w.actions, w.fields, w.list = nil, nil, 0
	w.root = w.boxNew(1, 10)
	w.border(w.root, 20)
	w.page = w.scrollNew(0, 0)
	w.containerAdd(w.page, w.root)
	w.containerAdd(w.widget, w.page)
	w.buttons(v.nav)
	w.boxPack(w.root, w.label(v.title), 0, 0, 0)
	if v.detail != "" {
		w.boxPack(w.root, w.label(v.detail), 0, 0, 0)
	}
	if len(v.items) > 0 {
		w.list = w.listNew()
		for _, item := range v.items {
			label := w.label(item)
			row := w.boxNew(0, 0)
			w.border(row, 8)
			w.containerAdd(row, label)
			w.containerAdd(w.list, row)
		}
		w.listSelect(w.list, w.listRow(w.list, int32(v.selected)))
		w.scroll(w.list)
	}
	for _, f := range v.fields {
		entry := w.entryNew()
		w.entrySet(entry, f.value)
		if f.private() {
			w.entryVisible(entry, 0)
		}
		w.fields = append(w.fields, entry)
		w.boxPack(w.root, w.label(f.label), 0, 0, 0)
		w.boxPack(w.root, entry, 0, 0, 0)
	}
	if v.report != "" {
		text := w.textNew()
		w.textEditable(text, 0)
		w.textWrap(text, 3)
		w.textSet(w.textBuffer(text), v.report, -1)
		w.scroll(text)
	}
	w.buttons(v.actions)
	w.showAll(w.widget)
}

func (w *gtkWindow) scroll(widget uintptr) {
	area := w.scrollNew(0, 0)
	w.sizeRequest(area, -1, 160)
	w.containerAdd(area, widget)
	w.boxPack(w.root, area, 1, 1, 0)
}

func (w *gtkWindow) activate() { w.present(w.widget) }
