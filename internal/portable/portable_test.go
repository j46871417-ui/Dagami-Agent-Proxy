package portable

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestBridgePreservesBufferedBytesAndHalfClose(t *testing.T) {
	server, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer server.Close()
	done := make(chan error, 1)
	go func() {
		c, e := server.Accept()
		if e != nil {
			done <- e
			return
		}
		defer c.Close()
		c.SetDeadline(time.Now().Add(5 * time.Second))
		data, e := io.ReadAll(c)
		if e == nil {
			_, e = c.Write(append([]byte("echo:"), data...))
		}
		done <- e
	}()
	b, e := StartBridge("direct")
	if e != nil {
		t.Fatal(e)
	}
	defer b.Close()
	c, e := net.DialTimeout("tcp", strings.TrimPrefix(b.URL(), "http://"), time.Second)
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	authority := server.Addr().String()
	request := "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\nfirst-bytes"
	if _, e = io.WriteString(c, request); e != nil {
		t.Fatal(e)
	}
	c.(*net.TCPConn).CloseWrite()
	r := bufio.NewReader(c)
	res, e := http.ReadResponse(r, &http.Request{Method: "CONNECT"})
	if e != nil || res.StatusCode != 200 {
		t.Fatalf("CONNECT: %v %v", res, e)
	}
	reply, e := io.ReadAll(r)
	if e != nil || string(reply) != "echo:first-bytes" {
		t.Fatalf("relay %q: %v", reply, e)
	}
	if e = <-done; e != nil {
		t.Fatal(e)
	}
}
func TestSOCKSAuthAndRemoteHostname(t *testing.T) {
	l, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer l.Close()
	result := make(chan string, 1)
	go func() {
		c, e := l.Accept()
		if e != nil {
			result <- "accept"
			return
		}
		defer c.Close()
		c.SetDeadline(time.Now().Add(5 * time.Second))
		method := make([]byte, 4)
		io.ReadFull(c, method)
		if !bytes.Equal(method, []byte{5, 2, 0, 2}) {
			result <- "methods"
			return
		}
		c.Write([]byte{5, 2})
		auth := make([]byte, 7)
		io.ReadFull(c, auth)
		if !bytes.Equal(auth, []byte{1, 1, 'u', 3, 'p', 'w', 'd'}) {
			result <- "auth"
			return
		}
		c.Write([]byte{1, 0})
		h := make([]byte, 5)
		io.ReadFull(c, h)
		if !bytes.Equal(h, []byte{5, 1, 0, 3, 12}) {
			result <- "header"
			return
		}
		body := make([]byte, 14)
		io.ReadFull(c, body)
		if string(body[:12]) != "example.test" || !bytes.Equal(body[12:], []byte{1, 187}) {
			result <- "target"
			return
		}
		c.Write([]byte{5, 0, 0, 1, 127, 0, 0, 1, 1, 187})
		result <- "ok"
	}()
	b, e := StartBridge("socks5://u:pwd@" + l.Addr().String())
	if e != nil {
		t.Fatal(e)
	}
	defer b.Close()
	c, e := net.DialTimeout("tcp", strings.TrimPrefix(b.URL(), "http://"), time.Second)
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	io.WriteString(c, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n")
	r, e := http.ReadResponse(bufio.NewReader(c), &http.Request{Method: "CONNECT"})
	if e != nil || r.StatusCode != 200 {
		t.Fatalf("CONNECT %v %v", r, e)
	}
	if got := <-result; got != "ok" {
		t.Fatal(got)
	}
}
func TestRouteNeverFallsBackOnProxyFailure(t *testing.T) {
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { t.Error("proxy error leaked a direct request") }))
	defer backend.Close()
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "CONNECT" {
			t.Error("no tunnel")
		}
		w.WriteHeader(407)
	}))
	defer proxy.Close()
	b, e := StartBridge(proxy.URL)
	if e != nil {
		t.Fatal(e)
	}
	defer b.Close()
	c, e := net.DialTimeout("tcp", strings.TrimPrefix(b.URL(), "http://"), time.Second)
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	io.WriteString(c, "GET "+backend.URL+" HTTP/1.1\r\nHost: "+strings.TrimPrefix(backend.URL, "http://")+"\r\n\r\n")
	r, e := http.ReadResponse(bufio.NewReader(c), nil)
	if e != nil || r.StatusCode != 502 {
		t.Fatalf("expected 502: %v %v", r, e)
	}
}

func TestConnectRetriesOnlyTransient(t *testing.T) {
	for _, tc := range []struct{ first, want int }{{503, 2}, {407, 1}} {
		var calls atomic.Int32
		upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.Method != "CONNECT" {
				t.Error("unexpected upstream method")
			}
			if calls.Add(1) == 1 {
				w.WriteHeader(tc.first)
				return
			}
			w.WriteHeader(200)
		}))
		bridge, err := StartBridge(upstream.URL)
		if err != nil {
			t.Fatal(err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		conn, err := bridge.dial(ctx, "example.test", 443)
		cancel()
		if conn != nil {
			conn.Close()
		}
		bridge.Close()
		upstream.Close()
		if int(calls.Load()) != tc.want || (tc.first == 407) != (err != nil) {
			t.Fatalf("status %d: calls=%d error=%v", tc.first, calls.Load(), err)
		}
	}
}
func TestPreviewDoesNotWriteOrConnect(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "never-create")
	c, err := NewController(dir, true)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	if _, err = c.Probe(context.Background(), "antigravity"); err == nil {
		t.Fatal("preview enabled network probe")
	}
	if _, err = c.DNSChange(context.Background(), "reset", "", ""); err == nil {
		t.Fatal("preview enabled DNS changes")
	}
	if _, err = os.Stat(dir); !os.IsNotExist(err) {
		t.Fatal("preview created state")
	}
}
func TestSettingsPersistAndRedact(t *testing.T) {
	dir := t.TempDir()
	c, e := NewController(dir, false)
	if e != nil {
		t.Fatal(e)
	}
	if e = c.SaveProfile("fixture", "http://127.0.0.1:18080"); e != nil {
		t.Fatal(e)
	}
	next, e := NewController(dir, false)
	if e != nil {
		t.Fatal(e)
	}
	raw, e := next.profileProxy()
	if e != nil || raw != "http://127.0.0.1:18080" {
		t.Fatalf("%q %v", raw, e)
	}
	next.state.Profiles[0].Protected = "encrypted-fixture"
	dump, _ := json.Marshal(next.Snapshot())
	if strings.Contains(string(dump), "encrypted-fixture") {
		t.Fatal("protected value leaked")
	}
	if strings.Contains(Redact("https://user:fixture@host"), "fixture") {
		t.Fatal("proxy credentials leaked")
	}
}
