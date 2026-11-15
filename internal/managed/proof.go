package managed

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/asn1"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"math/big"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

const proofWindow = 30
const redeemProofWindow = 5 * 60

var encoding = base64.RawURLEncoding

func Endpoint(raw string) (*url.URL, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil ||
		(u.Path != "" && u.Path != "/") || u.RawQuery != "" || u.Fragment != "" || u.ForceQuery {
		return nil, errors.New("нужен HTTPS-адрес шлюза Dagami без реквизитов")
	}
	u.Path = ""
	u.Host = strings.ToLower(u.Host)
	if u.Port() == "443" {
		u.Host = strings.TrimSuffix(u.Host, ":443")
	}
	return u, nil
}

func NewKey() (string, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return "", err
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return "", err
	}
	return encoding.EncodeToString(der), nil
}

func ParseKey(encoded string) (*ecdsa.PrivateKey, error) {
	der, err := encoding.DecodeString(encoded)
	if err != nil {
		return nil, errors.New("повреждён ключ устройства")
	}
	key, err := x509.ParseECPrivateKey(der)
	if err != nil || key.Curve != elliptic.P256() {
		return nil, errors.New("повреждён ключ устройства")
	}
	return key, nil
}

func proofMessage(audience, method, target, timestamp, nonce string) [32]byte {
	return sha256.Sum256([]byte(strings.Join([]string{"dagami-proxy-v1", audience, method, target, timestamp, nonce}, "\n")))
}

// ProofSigner keeps private key material in a platform store (Android Keystore,
// desktop keychain or a test fixture). SignMessage returns canonical P256 r||s.
type ProofSigner interface {
	PublicKey() []byte
	SignMessage([]byte) ([]byte, error)
}
type softwareSigner struct{ key *ecdsa.PrivateKey }

func (s softwareSigner) PublicKey() []byte {
	return elliptic.Marshal(elliptic.P256(), s.key.X, s.key.Y)
}
func (s softwareSigner) SignMessage(message []byte) ([]byte, error) {
	digest := sha256.Sum256(message)
	r, v, err := ecdsa.Sign(rand.Reader, s.key, digest[:])
	if err != nil {
		return nil, err
	}
	signature := make([]byte, 64)
	r.FillBytes(signature[:32])
	v.FillBytes(signature[32:])
	return signature, nil
}

// SignatureFromDER converts Android SHA256withECDSA output to gateway wire format.
func SignatureFromDER(der []byte) ([]byte, error) {
	var value struct{ R, S *big.Int }
	rest, err := asn1.Unmarshal(der, &value)
	n := elliptic.P256().Params().N
	if err != nil || len(rest) != 0 || value.R == nil || value.S == nil || value.R.Sign() <= 0 || value.S.Sign() <= 0 || value.R.Cmp(n) >= 0 || value.S.Cmp(n) >= 0 {
		return nil, errors.New("invalid P256 signature")
	}
	raw := make([]byte, 64)
	value.R.FillBytes(raw[:32])
	value.S.FillBytes(raw[32:])
	return raw, nil
}
func Sign(key *ecdsa.PrivateKey, audience, method, target string) (string, error) {
	if key == nil || key.Curve != elliptic.P256() {
		return "", errors.New("invalid device key")
	}
	return SignUsing(softwareSigner{key}, audience, method, target)
}
func SignUsing(signer ProofSigner, audience, method, target string) (string, error) {
	if signer == nil {
		return "", errors.New("device signer required")
	}
	public := signer.PublicKey()
	x, y := elliptic.Unmarshal(elliptic.P256(), public)
	if x == nil {
		return "", errors.New("invalid P256 public key")
	}
	var random [16]byte
	if _, err := rand.Read(random[:]); err != nil {
		return "", err
	}
	timestamp := strconv.FormatInt(time.Now().Unix(), 10)
	nonce := encoding.EncodeToString(random[:])
	message := []byte(strings.Join([]string{"dagami-proxy-v1", audience, method, target, timestamp, nonce}, "\n"))
	signature, err := signer.SignMessage(message)
	if err != nil {
		return "", err
	}
	digest := sha256.Sum256(message)
	if len(signature) != 64 || !ecdsa.Verify(&ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}, digest[:], new(big.Int).SetBytes(signature[:32]), new(big.Int).SetBytes(signature[32:])) {
		return "", errors.New("device signer returned an invalid signature")
	}
	return "Dagami v1." + strings.Join([]string{encoding.EncodeToString(public), timestamp, nonce, encoding.EncodeToString(signature)}, "."), nil
}

type replayGuard struct {
	mu         sync.Mutex
	buckets    map[int64]map[[32]byte]struct{}
	lastPrune  int64
	entryCount int
}

func (g *replayGuard) verify(header, audience, method, target string, now int64) string {
	return g.verifyWithin(header, audience, method, target, now, proofWindow)
}

func (g *replayGuard) verifyWithin(header, audience, method, target string, now, window int64) string {
	if !strings.HasPrefix(header, "Dagami v1.") || len(header) > 250 {
		return ""
	}
	parts := strings.Split(header[len("Dagami v1."):], ".")
	if len(parts) != 4 {
		return ""
	}
	public, e1 := encoding.DecodeString(parts[0])
	timestamp, e2 := strconv.ParseInt(parts[1], 10, 64)
	nonce, e3 := encoding.DecodeString(parts[2])
	signature, e4 := encoding.DecodeString(parts[3])
	if e1 != nil || e2 != nil || e3 != nil || e4 != nil || len(public) != 65 || len(nonce) != 16 || len(signature) != 64 || timestamp < now-window || timestamp > now+window {
		return ""
	}
	curve := elliptic.P256()
	x, y := elliptic.Unmarshal(curve, public)
	if x == nil {
		return ""
	}
	digest := proofMessage(audience, method, target, parts[1], parts[2])
	if !ecdsa.Verify(&ecdsa.PublicKey{Curve: curve, X: x, Y: y}, digest[:], new(big.Int).SetBytes(signature[:32]), new(big.Int).SetBytes(signature[32:])) {
		return ""
	}
	// Hash the canonical device and nonce, independent of signature encoding.
	replay := sha256.Sum256(append(public, nonce...))
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.buckets == nil {
		g.buckets = make(map[int64]map[[32]byte]struct{})
	}
	if g.lastPrune != now {
		for second, bucket := range g.buckets {
			if second < now-window {
				g.entryCount -= len(bucket)
				delete(g.buckets, second)
			}
		}
		g.lastPrune = now
	}
	for _, bucket := range g.buckets {
		if _, exists := bucket[replay]; exists {
			return ""
		}
	}
	if g.entryCount >= 65536 {
		return ""
	}
	if g.buckets[timestamp] == nil {
		g.buckets[timestamp] = make(map[[32]byte]struct{})
	}
	g.buckets[timestamp][replay] = struct{}{}
	g.entryCount++
	device := sha256.Sum256(public)
	return hex.EncodeToString(device[:])
}
