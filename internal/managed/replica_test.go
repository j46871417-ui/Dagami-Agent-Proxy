package managed

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"encoding/hex"
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

func TestReplicaPolicyReloadAndRevocation(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "snapshot.json")
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	sum := sha256.Sum256(elliptic.Marshal(key.Curve, key.X, key.Y))
	device := hex.EncodeToString(sum[:])
	login := "agw_0123456789abcdef01234567"
	snapshot := ReplicaSnapshot{Version: 1, GeneratedAt: time.Now().Add(-time.Minute), Devices: map[string]binding{device: {Subject: "42", Login: login}}, Accounts: map[string]ReplicaAccount{login: {Subject: "42", Scope: "all"}}, AllowedHosts: []string{"example.test"}, AllowedSuffixes: []string{".googleapis.com"}}
	write := func() {
		t.Helper()
		data, _ := json.Marshal(snapshot)
		if err := os.WriteFile(path, data, 0600); err != nil {
			t.Fatal(err)
		}
	}
	write()
	var dials atomic.Int32
	gateway, err := OpenReplica(path, filepath.Join(dir, "traffic.json"), "https://gateway.test:9443", func(context.Context, string, string) (net.Conn, int) {
		dials.Add(1)
		a, b := net.Pipe()
		b.Close()
		return a, 200
	})
	if err != nil {
		t.Fatal(err)
	}
	request := func(method, target string, want int) {
		t.Helper()
		proof, err := Sign(key, "gateway.test:9443", method, target)
		if err != nil {
			t.Fatal(err)
		}
		r := httptest.NewRequest(method, "https://gateway.test:9443"+target, nil)
		r.Host = "gateway.test:9443"
		r.TLS = &tls.ConnectionState{}
		r.Header.Set("Authorization", proof)
		w := httptest.NewRecorder()
		gateway.Public(w, r)
		if w.Code != want {
			t.Fatalf("status %d want %d", w.Code, want)
		}
	}
	request("GET", "/v1/status", 200)
	request("POST", "/v1/pair", 503)
	for _, target := range []string{"example.test:443", "a.googleapis.com:443"} {
		c, status := gateway.replica.connect(context.Background(), login, target)
		if status != 200 {
			t.Fatal(target, status)
		}
		c.Close()
	}
	for _, target := range []string{"evil.test:443", "example.test:22", "127.0.0.1:443", "a.googleapis.com.evil.test:443"} {
		if _, status := gateway.replica.connect(context.Background(), login, target); status != 403 {
			t.Fatal(target, status)
		}
	}
	a, b := net.Pipe()
	defer a.Close()
	defer b.Close()
	if !gateway.registry.track(device, snapshot.Devices[device], a, b) {
		t.Fatal("track failed")
	}
	snapshot.GeneratedAt = snapshot.GeneratedAt.Add(time.Second)
	snapshot.Accounts[login] = ReplicaAccount{Subject: "42", Scope: "google"}
	write()
	if err = gateway.ReloadReplica(); err != nil {
		t.Fatal(err)
	}
	if _, err = a.Write([]byte("revoked")); err == nil {
		t.Fatal("policy change left tunnel open")
	}
	gateway.registry.untrack(device, a)
	snapshot.GeneratedAt = snapshot.GeneratedAt.Add(time.Second)
	snapshot.Devices = map[string]binding{}
	write()
	if err = gateway.ReloadReplica(); err != nil {
		t.Fatal(err)
	}
	if _, ok := gateway.registry.get(device); ok {
		t.Fatal("revoked device retained")
	}
	if err = os.WriteFile(path, []byte("broken"), 0600); err != nil {
		t.Fatal(err)
	}
	if gateway.ReloadReplica() == nil {
		t.Fatal("invalid snapshot accepted")
	}
	if len(gateway.registry.devices) != 0 {
		t.Fatal("invalid reload changed last good policy")
	}
	snapshot.GeneratedAt = snapshot.GeneratedAt.Add(-2 * time.Minute)
	write()
	if gateway.ReloadReplica() == nil {
		t.Fatal("old snapshot accepted")
	}
	if dials.Load() != 2 {
		t.Fatal("forbidden request reached dialer")
	}
}

func TestReplicaPublicDestinations(t *testing.T) {
	for _, address := range []string{"127.0.0.1", "10.2.3.4", "169.254.169.254", "100.64.0.2", "192.0.2.10", "198.18.0.1", "203.0.113.1", "::1", "fc00::1", "2001:db8::1"} {
		if PublicDestination(net.ParseIP(address)) {
			t.Fatal("private destination", address)
		}
	}
	for _, address := range []string{"1.1.1.1", "8.8.8.8", "2606:4700:4700::1111"} {
		if !PublicDestination(net.ParseIP(address)) {
			t.Fatal(address)
		}
	}
	if _, status := ConnectLocal(context.Background(), "1.1.1.1:80", "example.com:443"); status != 502 {
		t.Fatal(status)
	}
}

func TestReplicaSignedTunnel(t *testing.T) {
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	sum := sha256.Sum256(elliptic.Marshal(key.Curve, key.X, key.Y))
	dir := t.TempDir()
	path := filepath.Join(dir, "snapshot.json")
	login := "agw_0123456789abcdef01234567"
	data, _ := json.Marshal(ReplicaSnapshot{Version: 1, GeneratedAt: time.Now(), Devices: map[string]binding{hex.EncodeToString(sum[:]): {Subject: "42", Login: login}}, Accounts: map[string]ReplicaAccount{login: {Subject: "42", Scope: "all"}}, AllowedHosts: []string{"example.test"}})
	os.WriteFile(path, data, 0600)
	var gateway *Gateway
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { gateway.Public(w, r) }))
	defer server.Close()
	gateway, _ = OpenReplica(path, filepath.Join(dir, "traffic.json"), server.URL, func(context.Context, string, string) (net.Conn, int) {
		a, b := net.Pipe()
		go func() { defer b.Close(); io.Copy(b, b) }()
		return a, 200
	})
	defer gateway.CloseTunnels()
	endpoint, _ := Endpoint(server.URL)
	proof, _ := Sign(key, endpoint.Host, "CONNECT", "example.test:443")
	conn, err := tls.Dial("tcp", endpoint.Host, server.Client().Transport.(*http.Transport).TLSClientConfig.Clone())
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(3 * time.Second))
	io.WriteString(conn, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\nProxy-Authorization: "+proof+"\r\n\r\n")
	var header strings.Builder
	one := make([]byte, 1)
	for !strings.HasSuffix(header.String(), "\r\n\r\n") {
		if _, err = io.ReadFull(conn, one); err != nil {
			t.Fatal(err)
		}
		header.Write(one)
	}
	if !strings.Contains(header.String(), "200") {
		t.Fatal(header.String())
	}
	conn.Write([]byte("ping"))
	out := make([]byte, 4)
	if _, err = io.ReadFull(conn, out); err != nil || string(out) != "ping" {
		t.Fatal(string(out), err)
	}
}
