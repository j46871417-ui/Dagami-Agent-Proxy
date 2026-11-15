package portable

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"dagami.local/agentproxy/internal/managed"
)

func TestSOCKS4WireAndSOCKS5NoDowngrade(t *testing.T) {
	for _, test := range []struct {
		address, host string
		request       []byte
	}{
		{"socks4://local@127.0.0.1", "192.0.2.1", []byte{4, 1, 1, 187, 192, 0, 2, 1, 'l', 'o', 'c', 'a', 'l', 0}},
		{"socks4a://127.0.0.1", "example.test", append([]byte{4, 1, 1, 187, 0, 0, 0, 1, 0}, "example.test\x00"...)},
	} {
		proxy, err := ParseProxy(test.address)
		if err != nil {
			t.Fatal(err)
		}
		client, server := net.Pipe()
		client.SetDeadline(time.Now().Add(time.Second))
		server.SetDeadline(time.Now().Add(time.Second))
		received := make(chan []byte, 1)
		go func() {
			defer server.Close()
			request := make([]byte, len(test.request))
			io.ReadFull(server, request)
			received <- request
			server.Write([]byte{0, 90, 0, 0, 0, 0, 0, 0})
		}()
		err = socks4Connect(context.Background(), client, proxy, test.host, 443)
		client.Close()
		if err != nil || !bytes.Equal(<-received, test.request) {
			t.Fatalf("%s handshake failed: %v", proxy.Scheme, err)
		}
	}
	for _, invalid := range []string{"socks4://user:pass@localhost", "socks4a://user%00@localhost", "socks5s://localhost?", "socks5://localhost:"} {
		if _, err := ParseProxy(invalid); err == nil {
			t.Fatalf("accepted invalid %s URL", strings.Split(invalid, ":")[0])
		}
	}
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	client.SetDeadline(time.Now().Add(time.Second))
	server.SetDeadline(time.Now().Add(time.Second))
	go func() { io.CopyN(io.Discard, server, 3); server.Write([]byte{5, 0}) }()
	proxy, _ := ParseProxy("socks5s://user:fixture@localhost")
	if err := socksConnect(client, proxy, "example.test", 443); err == nil {
		t.Fatal("accepted unauthenticated mode for a credentialed SOCKS proxy")
	}
}

func TestHTTP2ManagedStreamAuthorizationAndRevocation(t *testing.T) {
	const target = "example.test:443"
	const login = "agw_0123456789abcdef01234567"
	upstream, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer upstream.Close()
	bridgeKey := base64.RawURLEncoding.EncodeToString(make([]byte, 32))
	observed := make(chan error, 1)
	go func() {
		conn, err := upstream.Accept()
		if err != nil {
			observed <- err
			return
		}
		defer conn.Close()
		conn.SetDeadline(time.Now().Add(5 * time.Second))
		reader := bufio.NewReader(conn)
		request, err := http.ReadRequest(reader)
		if err != nil || request.Host != target || request.Header.Get("Proxy-Authorization") != "Dagami-Local "+bridgeKey+"."+login {
			observed <- fmt.Errorf("unexpected internal CONNECT: %v", err)
			return
		}
		io.WriteString(conn, "HTTP/1.1 200 OK\r\n\r\ninitial")
		ping := make([]byte, 4)
		if _, err = io.ReadFull(reader, ping); err != nil || string(ping) != "ping" {
			observed <- fmt.Errorf("unexpected upload: %q, %v", ping, err)
			return
		}
		_, err = io.WriteString(conn, "pong")
		observed <- err
		io.Copy(io.Discard, reader)
	}()
	var gateway *managed.Gateway
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { gateway.Public(w, r) }))
	defer server.Close()
	endpoint, _ := url.Parse("https://" + server.Listener.Addr().String())
	gateway, err = managed.OpenGateway(filepath.Join(t.TempDir(), "devices.json"), endpoint.String(), upstream.Addr().String(), bridgeKey)
	if err != nil {
		t.Fatal(err)
	}
	server.EnableHTTP2 = true
	server.StartTLS()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	admin := func(path, data string) *httptest.ResponseRecorder {
		t.Helper()
		recorder := httptest.NewRecorder()
		gateway.Private(recorder, httptest.NewRequest(http.MethodPost, path, strings.NewReader(data)))
		return recorder
	}
	issued := admin("/private/issue-access", fmt.Sprintf(`{"subject":"42","login":%q}`, login))
	var keyCode managed.PairStatus
	if err := json.Unmarshal(issued.Body.Bytes(), &keyCode); err != nil || keyCode.Code == "" {
		t.Fatalf("issue key: HTTP %d, %v", issued.Code, err)
	}
	encoded, _ := managed.NewKey()
	key, err := managed.ParseKey(encoded)
	if err != nil {
		t.Fatal(err)
	}
	sign := func(method, destination string) string {
		t.Helper()
		proof, err := managed.Sign(key, endpoint.Host, method, destination)
		if err != nil {
			t.Fatal(err)
		}
		return proof
	}
	redeem, _ := http.NewRequestWithContext(ctx, http.MethodPost, endpoint.String()+"/v1/redeem", strings.NewReader(fmt.Sprintf(`{"code":%q}`, keyCode.Code)))
	redeem.Header.Set("Authorization", sign(http.MethodPost, "/v1/redeem"))
	response, err := server.Client().Do(redeem)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("redeem: HTTP %d", response.StatusCode)
	}
	transport := server.Client().Transport
	assertDenied := func(proof string) {
		t.Helper()
		conn, err := dialHTTP2(ctx, transport, endpoint, target, proof)
		if conn != nil {
			conn.Close()
		}
		if err != connectStatus(http.StatusProxyAuthRequired) {
			t.Fatalf("wanted HTTP 407, got %v", err)
		}
	}
	assertDenied("Basic ignored")
	proof := sign(http.MethodConnect, target)
	dialCtx, stopDial := context.WithCancel(ctx)
	tunnel, err := dialHTTP2(dialCtx, transport, endpoint, target, proof)
	stopDial() // The tunnel must survive completion of the dial operation.
	if err != nil {
		t.Fatal(err)
	}
	defer tunnel.Close()
	tunnel.SetDeadline(time.Now().Add(5 * time.Second))
	assertDenied(proof) // Replay cannot open a second stream.
	reader := bufio.NewReader(tunnel)
	initial := make([]byte, 7)
	if _, err = io.ReadFull(reader, initial); err != nil || string(initial) != "initial" {
		t.Fatalf("initial bytes %q: %v", initial, err)
	}
	if _, err = io.WriteString(tunnel, "ping"); err != nil {
		t.Fatal(err)
	}
	pong := make([]byte, 4)
	if _, err = io.ReadFull(reader, pong); err != nil || string(pong) != "pong" {
		t.Fatalf("download %q: %v", pong, err)
	}
	if err := <-observed; err != nil {
		t.Fatal(err)
	}
	if revoked := admin("/private/revoke", `{"subject":"42"}`); revoked.Code != http.StatusNoContent {
		t.Fatalf("revoke: HTTP %d", revoked.Code)
	}
	if _, err := reader.ReadByte(); err != io.EOF {
		t.Fatalf("revoked stream did not end: %v", err)
	}
	assertDenied(sign(http.MethodConnect, target))
}
