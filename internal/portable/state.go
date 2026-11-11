package portable

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"time"
)

type App struct {
	ID      string   `json:"id"`
	Name    string   `json:"name"`
	Path    string   `json:"path"`
	Args    []string `json:"args"`
	Console bool     `json:"console"`
}
type Profile struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Proxy     string `json:"proxy"`
	Managed   bool   `json:"managed,omitempty"`
	Protected string `json:"protected,omitempty"`
}
type State struct {
	Pairing  pairing   `json:"pairing,omitempty"`
	Version  int       `json:"version"`
	Profiles []Profile `json:"profiles"`
	Apps     []App     `json:"apps"`
	Active   string    `json:"active"`
}
type Event struct {
	Time    string `json:"time"`
	Message string `json:"message"`
}
type SessionInfo struct {
	ID      string `json:"id"`
	App     string `json:"app"`
	Profile string `json:"profile"`
	PID     int    `json:"pid"`
	Bridge  string `json:"bridge"`
}
type session struct {
	SessionInfo
	cmd    *exec.Cmd
	bridge *Bridge
	stop   func() error
	done   chan struct{}
}
type Controller struct {
	mu       sync.Mutex
	dnsMu    sync.Mutex
	state    State
	dir      string
	sessions map[string]*session
	events   []Event
	preview  bool
}

func ID() string {
	b := make([]byte, 16)
	if _, e := rand.Read(b); e != nil {
		panic(e)
	}
	return hex.EncodeToString(b)
}
func NewController(dir string, preview bool) (*Controller, error) {
	c := &Controller{dir: dir, sessions: map[string]*session{}, preview: preview}
	if preview {
		c.state = State{Version: 1, Active: "demo", Profiles: []Profile{{ID: "demo", Name: "Рабочий профиль", Proxy: "direct"}}, Apps: []App{{ID: "antigravity", Name: "Antigravity"}, {ID: "antigravity-ide", Name: "Antigravity IDE"}, {ID: "codex", Name: "Codex"}, {ID: "vscode", Name: "Visual Studio Code"}}}
		return c, nil
	}
	if dir == "" {
		base, e := os.UserConfigDir()
		if e != nil {
			return nil, e
		}
		dir = filepath.Join(base, "DagamiAgentProxy", "native-v1")
		c.dir = dir
	}
	if e := os.MkdirAll(dir, 0700); e != nil {
		return nil, e
	}
	fi, e := os.Lstat(dir)
	if e != nil || !fi.IsDir() || fi.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("каталог состояния должен быть обычной папкой")
	}
	raw, e := os.ReadFile(filepath.Join(dir, "state.json"))
	if e == nil {
		if len(raw) > 1048576 {
			return nil, errors.New("слишком большой файл настроек")
		}
		if e = json.Unmarshal(raw, &c.state); e != nil {
			return nil, errors.New("повреждён файл настроек; исходник сохранён")
		}
		if c.state.Version != 1 {
			return nil, errors.New("неподдерживаемая версия настроек")
		}
		// The original install provided just the system route as a preset.
		if c.state.Active == "direct" && len(c.state.Profiles) == 1 && c.state.Profiles[0].ID == "direct" && c.state.Profiles[0].Proxy == "direct" {
			c.state.Profiles = append(c.state.Profiles, pendingManagedProfile())
			c.state.Active = managedDefaultID
			if e = c.saveLocked(); e != nil {
				return nil, e
			}
		}
		c.state.Apps = addMissingApps(c.state.Apps)
	} else if !os.IsNotExist(e) {
		return nil, e
	} else {
		c.state = State{Version: 1, Active: managedDefaultID, Profiles: []Profile{pendingManagedProfile(), {ID: "direct", Name: "Системный маршрут / VPN", Proxy: "direct"}}, Apps: DiscoverApps()}
		if e = c.saveLocked(); e != nil {
			return nil, e
		}
	}
	return c, nil
}
func atomicWrite(path string, data []byte, mode os.FileMode) error {
	f, e := os.CreateTemp(filepath.Dir(path), ".dagami-*")
	if e != nil {
		return e
	}
	temp := f.Name()
	defer os.Remove(temp)
	if e = f.Chmod(mode); e == nil {
		_, e = f.Write(data)
	}
	if e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e == nil {
		e = ce
	}
	if e != nil {
		return e
	}
	return replaceFile(temp, path)
}
func (c *Controller) saveLocked() error {
	if c.preview {
		return errors.New("режим превью не меняет настройки")
	}
	b, e := json.MarshalIndent(c.state, "", "  ")
	if e != nil {
		return e
	}
	return atomicWrite(filepath.Join(c.dir, "state.json"), b, 0600)
}

var redactURL = regexp.MustCompile(`(?i)(https?|socks5h?)://[^\s/@]+@`)
var oauthURL = regexp.MustCompile(`https://accounts\.google\.com/[^\s]+`)
var managedSecret = regexp.MustCompile(`(?i)(?:dagami v1\.[a-z0-9_.-]+|dagami_[a-z0-9_-]{43})`)

func Redact(s string) string {
	s = managedSecret.ReplaceAllString(s, "[Dagami proof hidden]")
	return oauthURL.ReplaceAllString(redactURL.ReplaceAllString(s, "$1://[hidden]@"), "[OAuth URL hidden]")
}
func (c *Controller) eventLocked(s string) {
	c.events = append(c.events, Event{Time: time.Now().Format("15:04:05"), Message: Redact(s)})
	if len(c.events) > 200 {
		c.events = append([]Event(nil), c.events[len(c.events)-200:]...)
	}
}

type Snapshot struct {
	State       State         `json:"state"`
	Sessions    []SessionInfo `json:"sessions"`
	Events      []Event       `json:"events"`
	OS          string        `json:"os"`
	Preview     bool          `json:"preview"`
	RoutingOnly bool          `json:"routing_only"`
}

func (c *Controller) Snapshot() Snapshot {
	c.mu.Lock()
	defer c.mu.Unlock()
	state := c.state
	state.Pairing = pairing{}
	state.Profiles = append([]Profile(nil), state.Profiles...)
	for i := range state.Profiles {
		state.Profiles[i].Protected = ""
	}
	sessions := []SessionInfo{}
	for _, s := range c.sessions {
		sessions = append(sessions, s.SessionInfo)
	}
	return Snapshot{state, sessions, append([]Event(nil), c.events...), runtime.GOOS, c.preview, RoutingOnly}
}
func (c *Controller) SaveProfile(name, raw string) error {
	if len(name) < 1 || len(name) > 80 || len(raw) > 4096 {
		return errors.New("проверьте имя профиля и адрес")
	}
	u, e := ParseProxy(raw)
	if e != nil {
		return e
	}
	p := Profile{ID: ID(), Name: name, Proxy: raw}
	if raw == "" {
		p.Proxy = "direct"
	}
	if u != nil && u.User != nil {
		protected, e := protectSecret(p.ID, raw)
		if e != nil {
			return e
		}
		p.Protected = protected
		safe := *u
		safe.User = nil
		p.Proxy = safe.String()
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	old := c.state
	c.state.Profiles = append(append([]Profile(nil), old.Profiles...), p)
	if len(c.sessions) == 0 {
		c.state.Active = p.ID
	}
	if e = c.saveLocked(); e != nil {
		c.state = old
		return e
	}
	c.eventLocked("Профиль сохранён: " + name)
	return nil
}
func (c *Controller) SelectProfile(id string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.sessions) > 0 {
		return errors.New("сначала остановите активные сессии")
	}
	for _, p := range c.state.Profiles {
		if p.ID == id {
			old := c.state.Active
			c.state.Active = id
			if e := c.saveLocked(); e != nil {
				c.state.Active = old
				return e
			}
			return nil
		}
	}
	return errors.New("профиль не найден")
}
func (c *Controller) DeleteProfile(id string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if id == c.state.Active {
		return errors.New("сначала выберите другой профиль")
	}
	for _, s := range c.sessions {
		if s.Profile == id {
			return errors.New("профиль используется активной сессией")
		}
	}
	old := c.state.Profiles
	next := []Profile{}
	for _, p := range old {
		if p.ID != id {
			next = append(next, p)
		}
	}
	if len(next) == len(old) {
		return errors.New("профиль не найден")
	}
	c.state.Profiles = next
	if e := c.saveLocked(); e != nil {
		c.state.Profiles = old
		return e
	}
	return nil
}
func (c *Controller) Register(id, name, path string, console bool) error {
	p, e := filepath.Abs(path)
	if e != nil {
		return e
	}
	p, e = resolveExecutable(p)
	if e != nil {
		return e
	}
	fi, e := os.Stat(p)
	if e != nil || !fi.Mode().IsRegular() {
		return errors.New("укажите существующий исполняемый файл")
	}
	if name == "" {
		name = filepath.Base(p)
	}
	if len(name) > 100 {
		return errors.New("слишком длинное название")
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	for _, a := range c.state.Apps {
		if a.Path == p && a.ID != id {
			return errors.New("приложение уже добавлено")
		}
	}
	for _, s := range c.sessions {
		if s.App == id {
			return errors.New("сначала остановите приложение")
		}
	}
	old := c.state.Apps
	next := append([]App(nil), old...)
	if id == "" {
		next = append(next, App{ID: ID(), Name: name, Path: p, Console: console})
	} else {
		found := false
		for i, a := range next {
			if a.ID == id {
				next[i] = App{ID: id, Name: name, Path: p, Console: console}
				found = true
			}
		}
		if !found {
			return errors.New("приложение не найдено")
		}
	}
	c.state.Apps = next
	if e = c.saveLocked(); e != nil {
		c.state.Apps = old
		return e
	}
	return nil
}
func childEnvironment(local string) []string {
	keys := map[string]bool{"HTTP_PROXY": true, "HTTPS_PROXY": true, "ALL_PROXY": true, "NO_PROXY": true, "AG_LS_PROXY": true, "AGY_PROXY_URL": true, "NODE_USE_ENV_PROXY": true}
	env := []string{}
	for _, v := range os.Environ() {
		k, _, _ := strings.Cut(v, "=")
		if !keys[strings.ToUpper(k)] {
			env = append(env, v)
		}
	}
	if local != "" {
		for _, k := range []string{"HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "AG_LS_PROXY", "AGY_PROXY_URL", "agy_proxy_url"} {
			env = append(env, k+"="+local)
		}
		env = append(env, "NODE_USE_ENV_PROXY=1")
	}
	return append(env, "NO_PROXY=localhost,127.0.0.1,::1", "no_proxy=localhost,127.0.0.1,::1")
}
func (c *Controller) Launch(id string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.preview {
		return errors.New("запуск отключён в превью")
	}
	var app App
	var profile Profile
	for _, a := range c.state.Apps {
		if a.ID == id {
			app = a
		}
	}
	for _, p := range c.state.Profiles {
		if p.ID == c.state.Active {
			profile = p
		}
	}
	if app.Path == "" || profile.ID == "" {
		return errors.New("выберите профиль и укажите путь приложения")
	}
	if app.Console {
		return errors.New("для интерактивного CLI используйте команду dagami run в терминале")
	}
	for _, s := range c.sessions {
		if s.App == id {
			return errors.New("приложение уже запущено через Dagami")
		}
	}
	route, e := routeForProfile(profile)
	if e != nil {
		return e
	}
	bridge, e := route.start()
	if e != nil {
		return e
	}
	args, env := launchProxySettings(app, bridge.URL(), runtime.GOOS)
	cmd := exec.Command(app.Path, args...)
	cmd.Env = env
	cmd.Dir = filepath.Dir(app.Path)
	if logf, lerr := os.OpenFile(filepath.Join(os.TempDir(), "dagami-app.log"), os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0600); lerr == nil {
		cmd.Stdout = logf
		cmd.Stderr = logf
		defer logf.Close()
	}
	prepareProcess(cmd)
	if e = cmd.Start(); e != nil {
		bridge.Close()
		return errors.New("не удалось запустить приложение")
	}
	stop, e := manageProcess(cmd)
	if e != nil {
		cmd.Process.Kill()
		cmd.Wait()
		bridge.Close()
		return e
	}
	s := &session{SessionInfo: SessionInfo{ID: ID(), App: id, Profile: profile.ID, PID: cmd.Process.Pid, Bridge: bridge.URL()}, cmd: cmd, bridge: bridge, stop: stop, done: make(chan struct{})}
	c.sessions[s.ID] = s
	c.eventLocked("Запущено: " + app.Name)
	go func() {
		cmd.Wait()
		stop()
		bridge.Close()
		c.mu.Lock()
		delete(c.sessions, s.ID)
		c.eventLocked("Сессия завершена: " + app.Name)
		c.mu.Unlock()
		close(s.done)
	}()
	return nil
}

// RunAttached keeps the current terminal's stdin/out and a single bridge alive
// for the duration of the command. No console window or settings are rewritten.
func (c *Controller) RunAttached(id string, args []string, input io.Reader, output, stderr io.Writer) error {
	c.mu.Lock()
	var app App
	for _, a := range c.state.Apps {
		if a.ID == id {
			app = a
			break
		}
	}
	c.mu.Unlock()
	if c.preview || app.Path == "" {
		return errors.New("сначала добавьте исполняемый файл в список приложений")
	}
	route, err := c.activeRoute()
	if err != nil {
		return err
	}
	bridge, err := route.start()
	if err != nil {
		return err
	}
	defer bridge.Close()
	launchArgs, launchEnv := launchProxySettings(app, bridge.URL(), runtime.GOOS)
	cmd := exec.Command(app.Path, append(launchArgs, args...)...)
	cmd.Env = launchEnv
	cmd.Dir = filepath.Dir(app.Path)
	cmd.Stdin = input
	cmd.Stdout = output
	cmd.Stderr = stderr
	return cmd.Run()
}
func (c *Controller) Stop(id string) error {
	c.mu.Lock()
	s := c.sessions[id]
	c.mu.Unlock()
	if s == nil {
		return errors.New("сессия не найдена")
	}
	return s.stop()
}
func (c *Controller) Close() {
	c.mu.Lock()
	sessions := []*session{}
	for _, s := range c.sessions {
		sessions = append(sessions, s)
	}
	c.mu.Unlock()
	for _, s := range sessions {
		s.stop()
		s.bridge.Close()
	}
	for _, s := range sessions {
		select {
		case <-s.done:
		case <-time.After(5 * time.Second):
		}
	}
}
func (c *Controller) activeRoute() (*Bridge, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	for _, p := range c.state.Profiles {
		if p.ID == c.state.Active {
			return routeForProfile(p)
		}
	}
	return nil, fmt.Errorf("профиль не выбран")
}
