package mobile

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"dagami.local/agentproxy/internal/managed"
)

type fixtureKeystore struct{ key *ecdsa.PrivateKey }

func (f fixtureKeystore) PublicPoint() []byte {
	return elliptic.Marshal(elliptic.P256(), f.key.X, f.key.Y)
}
func (f fixtureKeystore) SignDER(message []byte) ([]byte, error) {
	hash := sha256.Sum256(message)
	return ecdsa.SignASN1(rand.Reader, f.key, hash[:])
}

func TestAndroidSignerUsesGatewayFormat(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer := signerAdapter{fixtureKeystore{key}}
	proof, err := managed.SignUsing(signer, "gateway.example:9443", "CONNECT", "api.openai.com:443")
	if err != nil || !strings.HasPrefix(proof, "Dagami v1.") {
		t.Fatalf("Android signer incompatible with shared proof: %v", err)
	}
	if _, err := RedeemAccess("DAGAMI-OLD-LICENSE", nil); err == nil {
		t.Fatal("legacy app licence accepted")
	}
}

func TestAndroidCustomCheckUsesSelectedProxy(t *testing.T) {
	requests := make(chan string, 2)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests <- r.Method + " " + r.Host
		w.WriteHeader(http.StatusProxyAuthRequired)
	}))
	defer server.Close()
	if _, err := CheckCustom(server.URL); err == nil {
		t.Fatal("proxy rejection counted as a working route")
	}
	select {
	case request := <-requests:
		if request != "CONNECT api.openai.com:443" {
			t.Fatalf("unexpected proxy request %q", request)
		}
	default:
		t.Fatal("probe bypassed selected proxy")
	}
}
