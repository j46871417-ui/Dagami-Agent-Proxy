package managed

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"net"
	"testing"
	"time"
)

type keystoreFixture struct{ key *ecdsa.PrivateKey }

func (s keystoreFixture) PublicKey() []byte {
	return elliptic.Marshal(elliptic.P256(), s.key.X, s.key.Y)
}
func (s keystoreFixture) SignMessage(message []byte) ([]byte, error) {
	digest := sha256.Sum256(message)
	der, err := ecdsa.SignASN1(rand.Reader, s.key, digest[:])
	if err != nil {
		return nil, err
	}
	return SignatureFromDER(der)
}
func TestExternalSignerCompatibleWithGateway(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	proof, err := SignUsing(keystoreFixture{key}, "gateway.test", "CONNECT", "example.com:443")
	if err != nil {
		t.Fatal(err)
	}
	guard := replayGuard{}
	if guard.verify(proof, "gateway.test", "CONNECT", "example.com:443", time.Now().Unix()) == "" {
		t.Fatal("external signer proof rejected")
	}
	for _, der := range [][]byte{nil, {0x30, 0}, make([]byte, 64)} {
		if _, err := SignatureFromDER(der); err == nil {
			t.Fatal("invalid DER accepted")
		}
	}
}
func TestTLSFailureRetriesWithoutRU(t *testing.T) {
	candidates := GatewayCandidates(bootstrapAuthority)
	if len(candidates) != 5 {
		t.Fatal(candidates)
	}
	for _, host := range candidates {
		if host == "48.102.213.179:9443" {
			t.Fatal("RU data fallback")
		}
	}
	if len(GatewayCandidates("custom.test:9443")) != 1 {
		t.Fatal("custom gateway redirected")
	}
	attempts, closed := 0, make(chan struct{})
	conn, err := tryGateway(context.Background(), []string{"one", "two"}, func(ctx context.Context, address string) (net.Conn, error) {
		attempts++
		a, b := net.Pipe()
		go func() {
			defer b.Close()
			var buf [1]byte
			b.Read(buf[:])
			if address == "one" {
				close(closed)
			}
		}()
		return a, nil
	}, func(ctx context.Context, c net.Conn) (net.Conn, error) {
		if attempts == 1 {
			return nil, errors.New("TLS fixture")
		}
		return c, nil
	})
	if err != nil || attempts != 2 {
		t.Fatalf("retry: %d %v", attempts, err)
	}
	conn.Close()
	select {
	case <-closed:
	case <-time.After(time.Second):
		t.Fatal("failed connection leaked")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err = tryGateway(ctx, []string{"never"}, func(context.Context, string) (net.Conn, error) { t.Fatal("dial after cancellation"); return nil, nil }, nil)
	if !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}
