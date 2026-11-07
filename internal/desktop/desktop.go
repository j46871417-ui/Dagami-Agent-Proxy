package desktop

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"

	"dagami.local/agentproxy/internal/managed"
	"dagami.local/agentproxy/internal/portable"
)

type button struct {
	label string
	click func(int, []string)
}

type field struct{ label, value string }

type view struct {
	title, detail, report string
	items                 []string
	selected              int
	fields                []field
	nav, actions          []button
}

type window interface {
	show(view)
	post(func())
}

type desktop struct {
	window
	controller *portable.Controller
	ctx        context.Context
	page       string
	status     string
	preview    bool
}

func Run(c *portable.Controller, page string) error {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	return runWindow(func(w window) {
		d := &desktop{window: w, controller: c, ctx: ctx, page: page, preview: c.Snapshot().Preview}
		d.render()
	})
}

func action(label string, click func()) button {
	return button{label, func(int, []string) { click() }}
}

func (d *desktop) showPage(v view) {
	for _, entry := range []struct{ id, label string }{{"apps", "Приложения"}, {"connection", "Подключение"}, {"dns", "DNS и сброс"}, {"journal", "Журнал"}} {
		if entry.id != "dns" || !portable.RoutingOnly {
			v.nav = append(v.nav, action(entry.label, func() { d.page, d.status = entry.id, ""; d.render() }))
		}
	}
	if d.preview {
		v.detail = "Предпросмотр · " + v.detail
	}
	if d.status != "" {
		v.detail = d.status
	}
	d.show(v)
}

func (d *desktop) render() {
	snapshot := d.controller.Snapshot()
	switch d.page {
	case "connection":
		d.profiles(snapshot)
	case "dns":
		if !snapshot.RoutingOnly {
			d.dns(snapshot.OS)
			return
		}
		d.page = "apps"
		d.apps(snapshot)
	case "journal":
		var lines []string
		for _, entry := range snapshot.Events {
			lines = append(lines, entry.Time+"  "+entry.Message)
		}
		d.showPage(view{title: "Журнал", detail: "События текущего запуска", report: strings.Join(lines, "\n"), actions: []button{action("Обновить", d.render)}})
	default:
		d.page = "apps"
		d.apps(snapshot)
	}
}

func (d *desktop) run(label string, operation func() (string, error)) {
	if d.preview {
		d.status = "Операция доступна в обычном режиме"
		d.render()
		return
	}
	d.show(view{title: label, detail: "Выполняется…"})
	go func() {
		result, err := operation()
		if d.ctx.Err() != nil {
			return
		}
		d.post(func() {
			d.status = ""
			if err != nil {
				d.report(label, portable.Redact(err.Error()))
			} else if result != "" {
				d.report(label, result)
			} else {
				d.render()
			}
		})
	}()
}

func (d *desktop) report(title, text string) {
	d.showPage(view{title: title, report: text, actions: []button{action("Назад", d.render)}})
}

func (d *desktop) confirm(title, detail string, fn func()) {
	d.showPage(view{title: title, detail: detail, actions: []button{action("Подтвердить", fn), action("Отмена", d.render)}})
}

func (d *desktop) apps(snapshot portable.Snapshot) {
	v := view{title: "Приложения", detail: "Выберите программу и запустите её через активный профиль."}
	sessions := make(map[string]portable.SessionInfo, len(snapshot.Sessions))
	for _, session := range snapshot.Sessions {
		sessions[session.App] = session
	}
	for _, app := range snapshot.State.Apps {
		status := "Готово"
		if app.Path == "" {
			status = "Нужен путь"
		} else if app.Console {
			status = "CLI"
		} else if session, ok := sessions[app.ID]; ok {
			status = fmt.Sprintf("Запущено · PID %d", session.PID)
		}
		v.items = append(v.items, app.Name+"  ·  "+status)
	}
	withApp := func(label string, fn func(portable.App)) button {
		return button{label, func(i int, _ []string) {
			if i >= 0 && i < len(snapshot.State.Apps) {
				fn(snapshot.State.Apps[i])
			}
		}}
	}
	v.actions = []button{
		withApp("Запустить", func(app portable.App) {
			if app.Path == "" {
				d.appEditor(app)
			} else if app.Console {
				d.report("Запуск CLI", "Откройте терминал в папке Dagami и выполните:\n\nDagamiAgentProxy run "+app.ID+" -- --help\n\nУберите --help для интерактивного запуска.")
			} else {
				d.run("Запуск приложения", func() (string, error) { return "", d.controller.Launch(app.ID) })
			}
		}),
		withApp("Остановить", func(app portable.App) {
			if session, ok := sessions[app.ID]; ok {
				d.confirm("Завершить «"+app.Name+"»?", "Сохраните работу в приложении перед остановкой.", func() {
					d.run("Завершение сессии", func() (string, error) { return "", d.controller.Stop(session.ID) })
				})
			}
		}),
		withApp("Изменить путь", d.appEditor),
		action("Добавить", func() { d.appEditor(portable.App{}) }),
		action("Обновить", d.render),
	}
	if !snapshot.RoutingOnly {
		v.actions = append(v.actions, withApp("Проверить backend", func(app portable.App) {
			d.run("Диагностика backend", func() (string, error) { return reportValue(d.controller.InspectApp(app.ID)) })
		}))
	}
	d.showPage(v)
}

func (d *desktop) appEditor(app portable.App) {
	save := func(label string, console bool) button {
		return button{label, func(_ int, values []string) {
			d.run("Сохранение приложения", func() (string, error) {
				return "", d.controller.Register(app.ID, strings.TrimSpace(values[0]), strings.TrimSpace(values[1]), console)
			})
		}}
	}
	actions := []button{save("Сохранить приложение", false), save("Сохранить как CLI", true)}
	if app.Console {
		actions[0], actions[1] = actions[1], actions[0]
	}
	d.showPage(view{title: "Путь приложения", detail: "Укажите исполняемый файл или пакет .app на macOS.",
		fields: []field{{"Название", app.Name}, {"Путь", app.Path}}, actions: append(actions, action("Отмена", d.render))})
}

func (d *desktop) profiles(snapshot portable.Snapshot) {
	v := view{title: "Подключение", detail: "Выберите маршрут или добавьте HTTP-, HTTPS- или SOCKS5-прокси."}
	for i, profile := range snapshot.State.Profiles {
		label := profile.Name
		if profile.ID == snapshot.State.Active {
			label += "  ·  Активен"
			v.selected = i
		}
		if profile.Managed && profile.Proxy == "" {
			label += "  ·  Нужна привязка"
		} else if profile.Proxy != "direct" {
			label += "  ·  " + profile.Proxy
		}
		v.items = append(v.items, label)
	}
	withProfile := func(label string, fn func(portable.Profile)) button {
		return button{label, func(i int, _ []string) {
			if i >= 0 && i < len(snapshot.State.Profiles) {
				fn(snapshot.State.Profiles[i])
			}
		}}
	}
	v.actions = []button{
		withProfile("Выбрать", func(p portable.Profile) {
			d.run("Выбор профиля", func() (string, error) { return "", d.controller.SelectProfile(p.ID) })
		}),
		action("Новый профиль", func() {
			d.showPage(view{title: "Новый профиль", detail: "Реквизиты хранятся в системном хранилище.", fields: []field{{label: "Название"}, {label: "Адрес прокси"}},
				actions: []button{{"Сохранить", func(_ int, values []string) {
					d.run("Сохранение профиля", func() (string, error) {
						return "", d.controller.SaveProfile(strings.TrimSpace(values[0]), strings.TrimSpace(values[1]))
					})
				}}, action("Отмена", d.render)}})
		}),
		withProfile("Удалить", func(p portable.Profile) {
			d.confirm("Удалить «"+p.Name+"»?", "Удаляется только выбранный профиль.", func() {
				d.run("Удаление профиля", func() (string, error) { return "", d.controller.DeleteProfile(p.ID) })
			})
		}),
		action("Проверить связь", d.probe),
		action("Dagami через бота", d.managedPair),
	}
	d.showPage(v)
}

func (d *desktop) managedPair() {
	endpoint := d.controller.ManagedEndpoint()
	if endpoint == "" {
		endpoint = os.Getenv("DAGAMI_MANAGED_URL")
	}
	activate := func(start bool) func(int, []string) {
		return func(_ int, values []string) {
			d.run("Привязка Dagami", func() (string, error) {
				result, err := d.controller.ActivateManaged(d.ctx, strings.TrimSpace(values[0]), start)
				if err != nil {
					return "", err
				}
				if result.Active {
					return "Профиль Dagami · Telegram выбран. Можно запускать приложения.", nil
				}
				if result.Code == "" {
					return "Подтверждение ещё не получено. Проверьте привязку после подтверждения в боте.", nil
				}
				link := managed.BotURL + result.Code
				if err = openTelegram(link); err != nil {
					return "Откройте эту ссылку в Telegram:\n" + link, nil
				}
				return "Подтвердите привязку в Telegram. Код устройства заканчивается на " + result.Code[len(result.Code)-6:] + ". Затем нажмите «Проверить привязку».", nil
			})
		}
	}
	d.showPage(view{title: "Dagami через бота", detail: "Подтвердите устройство в Telegram. Код действует 10 минут.",
		fields: []field{{label: "HTTPS-адрес шлюза Dagami", value: endpoint}}, actions: []button{
			{"Открыть Telegram", activate(true)}, {"Проверить привязку", activate(false)}, action("Назад", d.render),
		}})
}

func (d *desktop) probe() {
	ids := []string{"antigravity", "gemini", "cursor", "codex", "claude"}
	d.showPage(view{title: "Проверка связи", detail: "TLS и HTTP через активный профиль. Без запросов к модели.", items: []string{"Antigravity", "Gemini", "Cursor", "Codex", "Claude"},
		actions: []button{{"Проверить", func(i int, _ []string) {
			if i < 0 || i >= len(ids) {
				return
			}
			d.run("Проверка связи", func() (string, error) {
				results, err := d.controller.Probe(d.ctx, ids[i])
				var report strings.Builder
				for _, result := range results {
					if result.Error != "" {
						fmt.Fprintf(&report, "%s  ·  %s\n", result.Host, result.Error)
					} else {
						fmt.Fprintf(&report, "%s  ·  HTTP %d  ·  %d мс\n", result.Host, result.Status, result.MS)
					}
				}
				return report.String(), err
			})
		}}, action("Назад", d.render)}})
}

func (d *desktop) dns(platform string) {
	v := view{title: "DNS и сброс", detail: "Ручная настройка DNS и восстановление из копии.", fields: []field{{label: "IP DNS-сервера"}}}
	if platform != "windows" {
		v.fields = append(v.fields, field{label: "Служба сети / интерфейс"})
	}
	change := func(operation, ip, iface string) {
		d.run("Системный DNS", func() (string, error) {
			ctx, cancel := context.WithTimeout(d.ctx, 75*time.Second)
			defer cancel()
			return reportValue(d.controller.DNSChange(ctx, operation, ip, iface))
		})
	}
	v.actions = []button{
		{"Применить", func(_ int, values []string) {
			iface := ""
			if len(values) > 1 {
				iface = strings.TrimSpace(values[1])
			}
			change("apply", strings.TrimSpace(values[0]), iface)
		}},
		action("Состояние", func() {
			d.run("Состояние DNS", func() (string, error) { return reportValue(d.controller.DNSStatus(d.ctx)) })
		}),
		action("Восстановить", func() {
			d.confirm("Восстановить DNS?", "Будет восстановлена последняя резервная копия Dagami.", func() { change("restore", "", "") })
		}),
	}
	if platform == "windows" {
		v.actions = append(v.actions, action("Сбросить DNS и NRPT", func() {
			d.confirm("Сбросить DNS и все правила NRPT?", "Будут затронуты правила других программ.", func() {
				d.confirm("Подтвердить полный сброс?", "Dagami сначала сохранит копию.", func() { change("reset", "", "") })
			})
		}))
	}
	d.showPage(v)
}

func reportValue(value any, err error) (string, error) {
	if err != nil {
		return "", err
	}
	if text, ok := value.(string); ok {
		return text, nil
	}
	data, err := json.MarshalIndent(value, "", "  ")
	return string(data), err
}
