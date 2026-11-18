package main

import (
	"bufio"
	"dagami.local/agentproxy/internal/portable"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestBrokerAuthenticationAndProviderIsolation(t *testing.T) {
	headers := make(chan string, 1)
	provider := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		headers <- r.Header.Get("Proxy-Authorization")
		c, b, e := w.(http.Hijacker).Hijack()
		if e != nil {
			return
		}
		defer c.Close()
		b.WriteString("HTTP/1.1 200 OK\r\n\r\ninitial")
		b.Flush()
		io.Copy(c, b)
	}))
	defer provider.Close()
	bridge, err := portable.StartBridge("http://provider:secret@" + provider.Listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer bridge.Close()
	route := &exitRoute{spec: routeSpec{Name: "paid-fixture"}, bridge: bridge, health: map[string]routeHealth{"general": {Up: true, Checked: time.Now()}}}
	pool := &routePool{routes: []*exitRoute{route}}
	handler, closeTunnels := brokerHandler(pool, "Basic PRIVATE_GATEWAY_CREDENTIAL")
	defer closeTunnels()
	server := httptest.NewServer(handler)
	defer server.Close()
	dial := func(auth string, want int) (net.Conn, *bufio.Reader) {
		t.Helper()
		c, e := net.DialTimeout("tcp", server.Listener.Addr().String(), time.Second)
		if e != nil {
			t.Fatal(e)
		}
		c.SetDeadline(time.Now().Add(3 * time.Second))
		fmt.Fprintf(c, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\nProxy-Authorization: %s\r\n\r\n", auth)
		b := bufio.NewReader(c)
		r, e := http.ReadResponse(b, &http.Request{Method: "CONNECT"})
		if e != nil || r.StatusCode != want {
			c.Close()
			t.Fatal(r, e)
		}
		return c, b
	}
	c, _ := dial("Basic WRONG", 407)
	c.Close()
	c, b := dial("Basic PRIVATE_GATEWAY_CREDENTIAL", 200)
	defer c.Close()
	if got := <-headers; got != "Basic cHJvdmlkZXI6c2VjcmV0" {
		t.Fatal("gateway credential leaked to provider")
	}
	initial := make([]byte, 7)
	if _, err = io.ReadFull(b, initial); err != nil || string(initial) != "initial" {
		t.Fatal("buffered bytes lost", err)
	}
	c.Write([]byte("ping"))
	out := make([]byte, 4)
	if _, err = io.ReadFull(b, out); err != nil || string(out) != "ping" {
		t.Fatal("tunnel failed", err)
	}
	closeTunnels()
	if _, err = c.Read(out); err == nil {
		t.Fatal("shutdown left tunnel active")
	}
}
