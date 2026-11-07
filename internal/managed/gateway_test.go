package managed

import (
	"bufio"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestManagedPairTunnelAndRevoke(t *testing.T) {
	const login = "agw_0123456789abcdef01234567"
	const target = "example.test:443"
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	legacy, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer legacy.Close()
	bridgeKey := encoding.EncodeToString(make([]byte, 32))
	legacyDone := make(chan error, 1)
	go func() {
		conn, e := legacy.Accept()
		if e != nil {
			legacyDone <- e
			return
		}
		defer conn.Close()
		conn.SetDeadline(time.Now().Add(8 * time.Second))
		reader := bufio.NewReader(conn)
		req, e := http.ReadRequest(reader)
		if e != nil || req.Host != target || req.Header.Get("Proxy-Authorization") != "Dagami-Local "+bridgeKey+"."+login {
			legacyDone <- fmt.Errorf("legacy policy not used: %v", e)
			return
		}
		io.WriteString(conn, "HTTP/1.1 200 Connection Established\r\n\r\ninitial")
		data := make([]byte, 4)
		if _, e = io.ReadFull(reader, data); e == nil && string(data) == "ping" {
			_, e = io.WriteString(conn, "pong")
		} else if e == nil {
			e = fmt.Errorf("unexpected tunnel payload %q", data)
		}
		legacyDone <- e
		io.Copy(io.Discard, reader)
	}()
	var gateway *Gateway
	public := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { gateway.Public(w, r) }))
	state := filepath.Join(t.TempDir(), "devices.json")
	endpoint, _ := Endpoint("https://" + public.Listener.Addr().String())
	gateway, err = OpenGateway(state, endpoint.String(), legacy.Addr().String(), bridgeKey)
	if err != nil {
		t.Fatal(err)
	}
	public.StartTLS()
	defer public.Close()
	connect := func(proof string, want int) (net.Conn, *bufio.Reader) {
		t.Helper()
		tlsConfig := public.Client().Transport.(*http.Transport).TLSClientConfig.Clone()
		conn, e := tls.Dial("tcp", endpoint.Host, tlsConfig)
		if e != nil {
			t.Fatal(e)
		}
		conn.SetDeadline(time.Now().Add(3 * time.Second))
		fmt.Fprintf(conn, "CONNECT %s HTTP/1.1\r\nHost: %s\r\nProxy-Authorization: %s\r\n\r\n", target, target, proof)
		reader := bufio.NewReader(conn)
		res, e := http.ReadResponse(reader, &http.Request{Method: http.MethodConnect})
		if e != nil || res.StatusCode != want {
			conn.Close()
			t.Fatalf("CONNECT got %v, %v; want %d", res, e, want)
		}
		if want != 200 {
			conn.Close()
		}
		return conn, reader
	}
	sign := func(authority string) string {
		t.Helper()
		proof, e := Sign(key, endpoint.Host, http.MethodConnect, authority)
		if e != nil {
			t.Fatal(e)
		}
		return proof
	}
	connect("Basic fixture", 407)
	connect(sign(target), 407)
	pair, err := activation(context.Background(), public.Client(), endpoint, key, true)
	if err != nil || pair.Active || len(pair.Code) != 43 {
		t.Fatalf("pair=%+v error=%v", pair, err)
	}
	admin := func(path, payload string, want int) {
		t.Helper()
		request := httptest.NewRequest(http.MethodPost, path, strings.NewReader(payload))
		response := httptest.NewRecorder()
		gateway.Private(response, request)
		if response.Code != want {
			t.Fatalf("%s: HTTP %d; want %d", path, response.Code, want)
		}
	}
	approve := fmt.Sprintf(`{"code":%q,"subject":"42","login":%q}`, pair.Code, login)
	admin("/private/pending", fmt.Sprintf(`{"code":%q}`, pair.Code), 204)
	admin("/private/approve", approve, 204)
	admin("/private/approve", approve, 410)
	active, err := activation(context.Background(), public.Client(), endpoint, key, false)
	if err != nil || !active.Active || active.Code != "" {
		t.Fatalf("active=%+v error=%v", active, err)
	}
	loaded, err := openRegistry(state)
	if err != nil || len(loaded.devices) != 1 {
		t.Fatalf("device registry did not persist: %v", err)
	}
	connect(sign("different.test:443"), 407)
	proof := sign(target)
	conn, reader := connect(proof, 200)
	defer conn.Close()
	connect(proof, 407)
	data := make([]byte, 7)
	if _, err = io.ReadFull(reader, data); err != nil || string(data) != "initial" {
		t.Fatalf("buffered bytes lost: %q %v", data, err)
	}
	io.WriteString(conn, "ping")
	if _, err = io.ReadFull(reader, data[:4]); err != nil || string(data[:4]) != "pong" {
		t.Fatalf("tunnel relay failed: %q %v", data[:4], err)
	}
	if err = <-legacyDone; err != nil {
		t.Fatal(err)
	}
	admin("/private/revoke", `{"subject":"42"}`, 204)
	if _, err = reader.ReadByte(); err == nil || (err != io.EOF && !strings.Contains(err.Error(), "closed")) {
		t.Fatalf("revocation did not close established tunnel: %v", err)
	}
	connect(sign(target), 407)
}

func TestWindowsProofInteroperability(t *testing.T) {
	fixture := os.Getenv("DAGAMI_NATIVE_MANAGED_CHECKS")
	if fixture == "" {
		t.Skip("WinForms fixture is built by -native-checks")
	}
	output, err := exec.Command(fixture, "proof", "gateway.test:50443", "example.test:443").Output()
	if err != nil {
		t.Fatal(err)
	}
	header := strings.TrimSpace(string(output))
	guard := &replayGuard{}
	now := time.Now().Unix()
	if guard.verify(header, "gateway.test:50443", "CONNECT", "other.test:443", now) != "" ||
		guard.verify(header, "gateway.test:50443", "CONNECT", "example.test:443", now) == "" ||
		guard.verify(header, "gateway.test:50443", "CONNECT", "example.test:443", now) != "" {
		t.Fatal("WinForms proof did not interoperate with the Go gateway")
	}
}

func TestAccessCodeDiscoveryAndDeviceBinding(t *testing.T) {
	var gateway *Gateway
	public := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { gateway.Public(w, r) }))
	endpoint, err := Endpoint("https://" + public.Listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	state := filepath.Join(t.TempDir(), "devices.json")
	gateway, err = OpenGateway(state, endpoint.String(), "127.0.0.1:1", encoding.EncodeToString(make([]byte, 32)))
	if err != nil {
		t.Fatal(err)
	}
	public.StartTLS()
	defer public.Close()

	discovery, err := public.Client().Get(endpoint.String() + "/v1/config")
	if err != nil {
		t.Fatal(err)
	}
	var config gatewayConfig
	err = json.NewDecoder(discovery.Body).Decode(&config)
	discovery.Body.Close()
	if discovery.StatusCode != 200 || err != nil || config.Endpoint != endpoint.String() {
		t.Fatalf("unexpected discovery response: %d %+v %v", discovery.StatusCode, config, err)
	}
	admin := func(path, payload string, want int) PairStatus {
		t.Helper()
		request := httptest.NewRequest(http.MethodPost, path, strings.NewReader(payload))
		response := httptest.NewRecorder()
		gateway.Private(response, request)
		if response.Code != want {
			t.Fatalf("%s: HTTP %d; want %d", path, response.Code, want)
		}
		var result PairStatus
		if want == 200 {
			if err := json.Unmarshal(response.Body.Bytes(), &result); err != nil {
				t.Fatal(err)
			}
		}
		return result
	}
	issue := func() string {
		t.Helper()
		code := admin("/private/issue-access", `{"subject":"42","login":"agw_0123456789abcdef01234567"}`, 200).Code
		if !ValidAccessCode(code) {
			t.Fatal("issued code is malformed")
		}
		return code
	}
	generate := func() *ecdsa.PrivateKey {
		t.Helper()
		key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
		if err != nil {
			t.Fatal(err)
		}
		return key
	}
	device, other := generate(), generate()
	redeem := func(key *ecdsa.PrivateKey, code string, want bool) {
		t.Helper()
		body, err := json.Marshal(map[string]string{"code": code})
		if err != nil {
			t.Fatal(err)
		}
		result, err := sendActivation(context.Background(), public.Client(), endpoint, key, http.MethodPost, "/v1/redeem", body)
		if want && (err != nil || !result.Active) || !want && err == nil {
			t.Fatalf("unexpected activation: active=%t err=%v want=%t", result.Active, err, want)
		}
	}
	first, current := issue(), issue()
	redeem(device, first, false)
	redeem(device, current, true)
	redeem(device, current, true)
	redeem(other, current, false)
	pending := issue()
	admin("/private/revoke", `{"subject":"42"}`, 204)
	redeem(device, current, false)
	redeem(other, pending, false)
}

func TestPairExpiryAndDeviceLimit(t *testing.T) {
	r, err := openRegistry(filepath.Join(t.TempDir(), "devices.json"))
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	b := binding{"42", "agw_0123456789abcdef01234567"}
	expired, err := r.pair("expired", now.Add(-pairLifetime))
	if err != nil || r.approve(expired, b, now) != errPair {
		t.Fatal("expired pairing was accepted")
	}
	for i := 0; i < 4; i++ {
		code, err := r.pair(fmt.Sprint(i), now)
		if err != nil {
			t.Fatal(err)
		}
		want := error(nil)
		if i == 3 {
			want = errLimit
		}
		if err = r.approve(code, b, now); err != want {
			t.Fatalf("device %d: %v; want %v", i, err, want)
		}
	}
}

func TestProofBoundToAudienceAndTime(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	proof, err := Sign(key, "gateway.test", "GET", "/v1/status")
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().Unix()
	guard := &replayGuard{}
	if guard.verify(proof, "elsewhere.test", "GET", "/v1/status", now) != "" ||
		guard.verify(proof, "gateway.test", "GET", "/v1/status", now+proofWindow+1) != "" ||
		guard.verify(proof, "gateway.test", "GET", "/v1/status", now) == "" {
		t.Fatal("proof did not respect endpoint or time binding")
	}
}
