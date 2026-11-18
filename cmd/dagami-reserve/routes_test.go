package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestPoolReloadPreservesHealthyRouteAndClosesExpiredProvider(t *testing.T) {
	provider := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		c, b, e := w.(http.Hijacker).Hijack()
		if e != nil {
			return
		}
		defer c.Close()
		b.WriteString("HTTP/1.1 200 OK\r\n\r\n")
		b.Flush()
		io.Copy(c, b)
	}))
	defer provider.Close()
	path := filepath.Join(t.TempDir(), "routes.json")
	config := routeConfig{SourceIP: "1.1.1.1", Routes: []routeSpec{{Name: "own"}, {Name: "paid-fixture", Proxy: provider.URL, ExpiresAt: time.Now().Add(time.Hour)}}}
	write := func() {
		t.Helper()
		raw, _ := json.Marshal(config)
		if err := os.WriteFile(path, raw, 0600); err != nil {
			t.Fatal(err)
		}
	}
	write()
	pool, err := newRoutePool(path, "")
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	own := pool.routes[0]
	own.health["google"] = routeHealth{Up: true, Checked: time.Now()}
	client, err := pool.connect(context.Background(), pool.routes[1], "example.test:443")
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	client.SetDeadline(time.Now().Add(3 * time.Second))
	client.Write([]byte("ping"))
	out := make([]byte, 4)
	if _, err = io.ReadFull(client, out); err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(path, []byte("broken"), 0600); err != nil {
		t.Fatal(err)
	}
	if pool.Reload() == nil || len(pool.routes) != 2 {
		t.Fatal("invalid reload replaced working routes")
	}
	config.Routes[1].ExpiresAt = time.Now().Add(-time.Minute)
	write()
	if err = pool.Reload(); err != nil {
		t.Fatal(err)
	}
	if len(pool.routes) != 1 || pool.routes[0] != own || !own.health["google"].Up {
		t.Fatal("healthy route state was lost")
	}
	if _, err = client.Read(out); err == nil {
		t.Fatal("expired provider tunnel left open")
	}
}

func TestServiceHealthAndStableFailover(t *testing.T) {
	now := time.Now()
	h := routeHealth{}
	h = updateHealth(h, nil, time.Second, now)
	if h.Up {
		t.Fatal("premature recovery")
	}
	h = updateHealth(h, nil, time.Second, now)
	if !h.Up {
		t.Fatal("not recovered")
	}
	first := &exitRoute{spec: routeSpec{Name: "own"}, health: map[string]routeHealth{"google": h, "openai": h}}
	second := &exitRoute{spec: routeSpec{Name: "peer"}, health: map[string]routeHealth{"google": h}}
	third := &exitRoute{spec: routeSpec{Name: "free", Public: true}, health: map[string]routeHealth{"google": h}}
	pool := &routePool{routes: []*exitRoute{first, second, third}}
	if got := pool.candidates("google", now); len(got) != 2 || got[0] != first {
		t.Fatal("unstable priority")
	}
	first.health["google"] = updateHealth(h, errors.New("timeout"), 0, now)
	if pool.candidates("google", now)[0] != first {
		t.Fatal("single failure flaps route")
	}
	first.health["google"] = updateHealth(first.health["google"], errors.New("timeout"), 0, now)
	if pool.candidates("google", now)[0] != second {
		t.Fatal("no fallback")
	}
	if pool.candidates("openai", now)[0] != first {
		t.Fatal("Google failure removed OpenAI")
	}
	if len(pool.candidates("google", now.Add(4*time.Minute))) != 0 {
		t.Fatal("stale health accepted")
	}
}
func TestPublicEgressLocation(t *testing.T) {
	for _, body := range []string{"loc=RU\n", "loc=XX\n", "loc=\n", "no country"} {
		if foreignLocation(body) {
			t.Fatal(body)
		}
	}
	if !foreignLocation("ip=1.1.1.1\nloc=US\n") {
		t.Fatal("US rejected")
	}
}
func TestServiceClassification(t *testing.T) {
	for host, want := range map[string]string{"daily-cloudcode-pa.googleapis.com:443": "google", "accounts.google.com:443": "google", "api.openai.com:443": "openai", "chatgpt.com.evil.test:443": "general"} {
		if got := serviceFor(host); got != want {
			t.Fatal(host, got)
		}
	}
}
