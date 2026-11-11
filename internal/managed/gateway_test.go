package managed

import (
	"bufio"
	"bytes"
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
	"sync/atomic"
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
	gateway.CloseTunnels()
	if err := gateway.FlushTraffic(); err != nil {
		t.Fatal(err)
	}
	meter, err := openTraffic(state + ".traffic.json")
	if err != nil {
		t.Fatal(err)
	}
	if counters := meter.snapshot(); counters.UploadBytes != 4 || counters.DownloadBytes != 11 || counters.Since.IsZero() {
		t.Fatalf("unexpected tunnel traffic: %+v", counters)
	}
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
	var gateway atomic.Pointer[Gateway]
	public := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { gateway.Load().Public(w, r) }))
	endpoint, err := Endpoint("https://" + public.Listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	state := filepath.Join(t.TempDir(), "devices.json")
	reopen := func() {
		t.Helper()
		loaded, err := OpenGateway(state, endpoint.String(), "127.0.0.1:1", encoding.EncodeToString(make([]byte, 32)))
		if err != nil {
			t.Fatal(err)
		}
		gateway.Store(loaded)
	}
	reopen()
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
		gateway.Load().Private(response, request)
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
	device, other, third, fourth := generate(), generate(), generate(), generate()
	redeem := func(key *ecdsa.PrivateKey, code string, want int) {
		t.Helper()
		body, err := json.Marshal(map[string]string{"code": code})
		if err != nil {
			t.Fatal(err)
		}
		proof, err := Sign(key, endpoint.Host, http.MethodPost, "/v1/redeem")
		if err != nil {
			t.Fatal(err)
		}
		request, err := http.NewRequest(http.MethodPost, endpoint.String()+"/v1/redeem", bytes.NewReader(body))
		if err != nil {
			t.Fatal(err)
		}
		request.Header.Set("Authorization", proof)
		response, err := public.Client().Do(request)
		if err != nil {
			t.Fatal(err)
		}
		defer response.Body.Close()
		if response.StatusCode != want {
			t.Fatalf("activation HTTP %d; want %d", response.StatusCode, want)
		}
		if want == http.StatusOK {
			var result PairStatus
			if json.NewDecoder(response.Body).Decode(&result) != nil || !result.Active {
				t.Fatal("activation was not confirmed")
			}
		}
	}
	current := issue()
	if issue() != current {
		t.Fatal("repeated issuance changed the key")
	}
	redeem(device, current, http.StatusOK)
	// Promote a copy under the same public authority. The original registry
	// stays untouched and the next requests use a new gateway and state path.
	primaryState := state
	state = filepath.Join(t.TempDir(), "devices.json")
	snapshot, err := os.ReadFile(primaryState)
	if err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(state, snapshot, 0600); err != nil {
		t.Fatal(err)
	}
	reopen()
	if issue() != current {
		t.Fatal("restoring the snapshot changed the key")
	}
	restored, err := activation(context.Background(), public.Client(), endpoint, device, false)
	if err != nil || !restored.Active {
		t.Fatalf("restored snapshot lost existing device: %v", err)
	}
	redeem(device, current, http.StatusOK)
	redeem(other, current, http.StatusOK)
	checkStats := func(want keyStats) {
		t.Helper()
		r := httptest.NewRecorder()
		gateway.Load().Private(r, httptest.NewRequest(http.MethodGet, "/private/stats", nil))
		var fields map[string]json.RawMessage
		if r.Code != http.StatusOK || json.Unmarshal(r.Body.Bytes(), &fields) != nil || len(fields) != 7 {
			t.Fatal("statistics response is not the aggregate-only schema")
		}
		for _, field := range []string{"issued", "activated", "current_keys", "devices", "upload_bytes", "download_bytes", "traffic_since"} {
			if _, ok := fields[field]; !ok {
				t.Fatalf("missing aggregate field %s", field)
			}
		}
		var got keyStats
		if json.Unmarshal(r.Body.Bytes(), &got) != nil || got != want {
			t.Fatalf("stats = %+v, want %+v", got, want)
		}
	}
	checkStats(keyStats{keyTotals{1, 1}, 1, 2})
	rotated := admin("/private/rotate-access", `{"subject":"42","login":"agw_0123456789abcdef01234567"}`, 200).Code
	if !ValidAccessCode(rotated) || rotated == current || issue() != rotated {
		t.Fatal("rotation did not install a different current key")
	}
	checkStats(keyStats{keyTotals{2, 1}, 1, 2})
	redeem(third, current, http.StatusGone)
	reopen()
	redeem(third, current, http.StatusGone)
	status, err := activation(context.Background(), public.Client(), endpoint, device, false)
	if err != nil || !status.Active {
		t.Fatal("rotation removed an existing device")
	}
	redeem(third, rotated, http.StatusOK)
	redeem(fourth, rotated, http.StatusConflict)
	checkStats(keyStats{keyTotals{2, 2}, 1, 3})
	response, err := public.Client().Get(endpoint.String() + "/private/stats")
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusNotFound {
		t.Fatalf("private stats exposed publicly: HTTP %d", response.StatusCode)
	}
	admin("/private/revoke", `{"subject":"42"}`, 204)
	reopen()
	unchanged, err := os.ReadFile(primaryState)
	if err != nil || !bytes.Equal(snapshot, unchanged) {
		t.Fatal("promoted copy wrote back to the former primary")
	}
	checkStats(keyStats{keyTotals{2, 2}, 0, 0})
	redeem(device, current, http.StatusGone)
	redeem(device, rotated, http.StatusGone)
	replacement := issue()
	if replacement == current {
		t.Fatal("revocation reused the old key")
	}
	redeem(fourth, replacement, http.StatusOK)
	checkStats(keyStats{keyTotals{3, 3}, 1, 1})
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

func TestActivationClockToleranceRejectsReplay(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().Unix()
	proof, err := Sign(key, "gateway.test", "POST", "/v1/redeem")
	if err != nil {
		t.Fatal(err)
	}
	guard := &replayGuard{}
	if guard.verifyWithin(proof, "gateway.test", "POST", "/v1/redeem", now+redeemProofWindow+2, redeemProofWindow) != "" ||
		guard.verifyWithin(proof, "gateway.test", "POST", "/v1/redeem", now+120, redeemProofWindow) == "" ||
		guard.verifyWithin(proof, "gateway.test", "POST", "/v1/redeem", now+121, redeemProofWindow) != "" {
		t.Fatal("activation window or replay protection failed")
	}
}
