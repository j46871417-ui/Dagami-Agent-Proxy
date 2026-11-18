package managed

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestPaidProofCannotBecomeDefaultOrRegisterDevice(t *testing.T) {
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	paid, err := SignPaid(key, "gateway.test", "CONNECT", "example.test:443")
	if err != nil {
		t.Fatal(err)
	}
	proof := "Dagami " + strings.TrimPrefix(paid, "Dagami-Paid ")
	guard := &replayGuard{}
	if guard.verify(proof, "gateway.test", "CONNECT", "example.test:443", time.Now().Unix()) != "" {
		t.Fatal("paid proof accepted as ordinary")
	}
	if guard.verify(proof, "gateway.test#paid", "CONNECT", "example.test:443", time.Now().Unix()) == "" {
		t.Fatal("paid proof rejected")
	}
	g := &Gateway{audience: "gateway.test", slots: make(chan struct{}, 1)}
	req := httptest.NewRequest("POST", "https://gateway.test/v1/pair", nil)
	req.Header.Set("Authorization", paid)
	recorder := httptest.NewRecorder()
	g.Public(recorder, req)
	if recorder.Code != http.StatusUnauthorized {
		t.Fatal("paid CONNECT proof allowed registration", recorder.Code)
	}
	if PaidPool(context.Background()) || !PaidPool(WithPaidPool(context.Background())) {
		t.Fatal("pool context lost")
	}
}
