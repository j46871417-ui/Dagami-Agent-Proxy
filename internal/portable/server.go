package portable

import (
	"context"
	"crypto/subtle"
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

//go:embed ui.html
var pageHTML []byte

//go:embed ui.js
var pageJS []byte

//go:embed ui.css
var pageCSS []byte

type Server struct {
	http       *http.Server
	listener   net.Listener
	Token      string
	URL        string
	Controller *Controller
	stopped    chan struct{}
	once       sync.Once
}

func StartServer(c *Controller) (*Server, error) {
	l, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		return nil, e
	}
	s := &Server{listener: l, Token: ID() + ID(), URL: "http://" + l.Addr().String(), Controller: c, stopped: make(chan struct{})}
	s.http = &http.Server{Handler: http.HandlerFunc(s.handle), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 15 * time.Second, WriteTimeout: 90 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 16384}
	go s.http.Serve(l)
	return s, nil
}
func (s *Server) Close() {
	s.once.Do(func() {
		s.Controller.Close()
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		s.http.Shutdown(ctx)
		close(s.stopped)
	})
}
func (s *Server) Wait()       { <-s.stopped }
func (s *Server) Open() error { return openBrowser(s.URL + "/#" + s.Token) }
func (s *Server) handle(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
	if r.Host != s.listener.Addr().String() {
		http.Error(w, "invalid host", 403)
		return
	}
	if origin := r.Header.Get("Origin"); origin != "" && origin != s.URL {
		http.Error(w, "invalid origin", 403)
		return
	}
	if !strings.HasPrefix(r.URL.Path, "/api/") {
		if r.Method != "GET" {
			w.WriteHeader(405)
			return
		}
		switch r.URL.Path {
		case "/":
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			w.Write(pageHTML)
		case "/ui.js":
			w.Header().Set("Content-Type", "application/javascript; charset=utf-8")
			w.Write(pageJS)
		case "/ui.css":
			w.Header().Set("Content-Type", "text/css; charset=utf-8")
			w.Write(pageCSS)
		default:
			http.NotFound(w, r)
		}
		return
	}
	if subtle.ConstantTimeCompare([]byte(r.Header.Get("X-Dagami-Token")), []byte(s.Token)) != 1 {
		http.Error(w, "forbidden", 403)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	if r.Method == "GET" && r.URL.Path == "/api/state" {
		json.NewEncoder(w).Encode(s.Controller.Snapshot())
		return
	}
	if r.Method != "POST" {
		w.WriteHeader(405)
		return
	}
	if s.Controller.preview {
		reply(w, nil, errors.New("режим превью: изменения и сетевые проверки отключены"))
		return
	}
	var input struct {
		ID        string `json:"id"`
		Name      string `json:"name"`
		Proxy     string `json:"proxy"`
		Path      string `json:"path"`
		Console   bool   `json:"console"`
		Server    string `json:"server"`
		Interface string `json:"interface"`
		Confirm   string `json:"confirm"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 16384)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if e := decoder.Decode(&input); e != nil {
		reply(w, nil, errors.New("неверный запрос"))
		return
	}
	var extra any
	if decoder.Decode(&extra) != io.EOF {
		reply(w, nil, errors.New("лишние данные в запросе"))
		return
	}
	var value any
	var e error
	switch r.URL.Path {
	case "/api/profile/save":
		e = s.Controller.SaveProfile(input.Name, input.Proxy)
	case "/api/profile/select":
		e = s.Controller.SelectProfile(input.ID)
	case "/api/profile/delete":
		e = s.Controller.DeleteProfile(input.ID)
	case "/api/app/register":
		e = s.Controller.Register(input.ID, input.Name, input.Path, input.Console)
	case "/api/app/launch":
		e = s.Controller.Launch(input.ID)
	case "/api/app/inspect":
		value, e = s.Controller.InspectApp(input.ID)
	case "/api/session/stop":
		e = s.Controller.Stop(input.ID)
	case "/api/probe":
		value, e = s.Controller.Probe(r.Context())
	case "/api/dns/status":
		value, e = s.Controller.DNSStatus(r.Context())
	case "/api/dns/apply", "/api/dns/restore", "/api/dns/reset":
		operation := strings.TrimPrefix(r.URL.Path, "/api/dns/")
		if operation == "reset" && input.Confirm != "RESET ALL DNS AND NRPT" {
			e = errors.New("для общего сброса нужны оба подтверждения")
		} else {
			value, e = s.Controller.DNSChange(r.Context(), operation, input.Server, input.Interface)
		}
	case "/api/quit":
		value = "Приложение завершает работу"
		go func() { time.Sleep(100 * time.Millisecond); s.Close() }()
	default:
		w.WriteHeader(404)
		return
	}
	reply(w, value, e)
}
func reply(w http.ResponseWriter, value any, e error) {
	if e != nil {
		w.WriteHeader(400)
		json.NewEncoder(w).Encode(map[string]any{"ok": false, "error": Redact(e.Error())})
		return
	}
	json.NewEncoder(w).Encode(map[string]any{"ok": true, "result": value})
}
func (c *Controller) Probe(parent context.Context) (any, error) {
	raw, e := c.profileProxy()
	if e != nil {
		return nil, e
	}
	b, e := StartBridge(raw)
	if e != nil {
		return nil, e
	}
	defer b.Close()
	u, _ := url.Parse(b.URL())
	transport := &http.Transport{Proxy: http.ProxyURL(u), TLSHandshakeTimeout: 5 * time.Second, ResponseHeaderTimeout: 5 * time.Second, DisableKeepAlives: true}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }}
	type result struct {
		Host   string `json:"host"`
		Status int    `json:"status"`
		MS     int64  `json:"ms"`
		Error  string `json:"error,omitempty"`
	}
	results := make([]result, 3)
	hosts := []string{"accounts.google.com", "oauth2.googleapis.com", "cloudcode-pa.googleapis.com"}
	var wg sync.WaitGroup
	for i, host := range hosts {
		wg.Add(1)
		go func(i int, host string) {
			defer wg.Done()
			ctx, cancel := context.WithTimeout(parent, 8*time.Second)
			defer cancel()
			start := time.Now()
			req, _ := http.NewRequestWithContext(ctx, "HEAD", "https://"+host+"/", nil)
			res, e := client.Do(req)
			r := result{Host: host, MS: time.Since(start).Milliseconds()}
			if e != nil {
				r.Error = "не удалось проверить TLS/HTTP"
			} else {
				r.Status = res.StatusCode
				res.Body.Close()
			}
			results[i] = r
		}(i, host)
	}
	wg.Wait()
	c.mu.Lock()
	c.eventLocked("Проверен TLS/HTTP маршрута. Ответ модели и права аккаунта не проверялись.")
	c.mu.Unlock()
	return map[string]any{"targets": results, "model_verified": false, "note": "HTTP 401/403 подтверждает ответ сервера, но не работу модели."}, nil
}
func (c *Controller) LogMessage(s string) { c.mu.Lock(); c.eventLocked(fmt.Sprint(s)); c.mu.Unlock() }
