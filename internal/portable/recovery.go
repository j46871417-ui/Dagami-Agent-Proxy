package portable

import (
	"context"
	"errors"
	"os"
	"runtime"
)

type RecoveryReport struct {
	Completed []string `json:"completed"`
	Errors    []string `json:"errors"`
}

func (r RecoveryReport) Success() bool { return len(r.Errors) == 0 }

// Recover does not kill editors or erase credentials. Only recorded settings
// are reverted; proxy profiles remain available for a later reconnect.
func (c *Controller) Recover(ctx context.Context) RecoveryReport {
	r := RecoveryReport{}
	c.mu.Lock()
	if c.preview || c.recovering {
		c.mu.Unlock()
		r.Errors = append(r.Errors, "восстановление уже выполняется или отключено в превью")
		return r
	}
	c.recovering = true
	c.mu.Unlock()
	defer func() { c.mu.Lock(); c.recovering = false; c.mu.Unlock() }()
	// Detaching keeps unsaved work alive. Bridges close so sessions stop using Dagami.
	c.Close()
	c.mu.Lock()
	home, e := os.UserHomeDir()
	if e == nil {
		e = restoreLaunchers(c.dir, home, runtime.GOOS, "")
	}
	if e != nil {
		r.Errors = append(r.Errors, Redact(e.Error()))
	} else {
		r.Completed = append(r.Completed, "Управляемые запуски отменены")
	}
	c.mu.Unlock()
	if restored, err := c.restoreDNS(ctx); err != nil {
		r.Errors = append(r.Errors, Redact(err.Error()))
	} else if restored {
		r.Completed = append(r.Completed, "Прежний DNS восстановлен")
	}

	c.mu.Lock()
	old := c.state
	found := false
	for _, p := range c.state.Profiles {
		if p.ID == "direct" {
			found = true
			break
		}
	}
	if !found {
		c.state.Profiles = append(c.state.Profiles, Profile{ID: "direct", Name: "Системный маршрут / VPN", Proxy: "direct"})
	}
	c.state.Active = "direct"
	if e = c.saveLocked(); e != nil {
		c.state = old
		r.Errors = append(r.Errors, Redact(e.Error()))
	} else {
		r.Completed = append(r.Completed, "Выбран прямой маршрут; ключи и профили сохранены")
	}
	c.eventLocked("Восстановление: перезапустите IDE для удаления унаследованных переменных прокси")
	c.mu.Unlock()
	return r
}

// StandaloneRecovery works even if state.json cannot be decoded; a damaged
// connection configuration is never overwritten as a side effect of recovery.
func StandaloneRecovery(ctx context.Context, dir string) (RecoveryReport, error) {
	if handled, report, err := ForwardRecovery(ctx, dir); handled || err != nil {
		return report, err
	}
	path, e := StateDirectory(dir)
	if e != nil {
		return RecoveryReport{}, e
	}
	if e = os.MkdirAll(path, 0700); e != nil {
		return RecoveryReport{}, e
	}
	instance, e := lockInstance(path)
	if e != nil {
		return RecoveryReport{}, e
	}
	defer instance.Close()
	c, e := NewController(path, false)
	if e == nil {
		defer c.Close()
		r := c.Recover(ctx)
		if !r.Success() {
			return r, errors.New("восстановление выполнено частично; журналы сохранены")
		}
		return r, nil
	}
	r := RecoveryReport{Errors: []string{"Настройки подключения недоступны. " + Redact(e.Error())}}
	home, hErr := os.UserHomeDir()
	if hErr == nil {
		hErr = restoreLaunchers(path, home, runtime.GOOS, "")
	}
	if hErr != nil {
		r.Errors = append(r.Errors, Redact(hErr.Error()))
	} else {
		r.Completed = append(r.Completed, "Управляемые запуски отменены")
	}
	if restored, err := restoreRecordedDNS(ctx, path); err != nil {
		r.Errors = append(r.Errors, Redact(err.Error()))
	} else if restored {
		r.Completed = append(r.Completed, "Прежний DNS восстановлен")
	}

	return r, errors.New("восстановление выполнено частично; повреждённые данные не изменялись")
}
