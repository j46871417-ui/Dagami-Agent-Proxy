// Package mobile is the gomobile binding for the Android prototype.
package mobile

import (
	"context"
	"errors"
	"strings"
	"time"

	"dagami.local/agentproxy/internal/managed"
	"dagami.local/agentproxy/internal/portable"
)

// DeviceSigner keeps the private key in Android Keystore. SignDER signs the
// message using SHA256withECDSA. PublicPoint returns uncompressed SEC1 P-256.
type DeviceSigner interface {
	PublicPoint() []byte
	SignDER(message []byte) ([]byte, error)
}

type signerAdapter struct{ DeviceSigner }

func (s signerAdapter) PublicKey() []byte { return s.PublicPoint() }
func (s signerAdapter) SignMessage(message []byte) ([]byte, error) {
	der, err := s.SignDER(message)
	if err != nil {
		return nil, err
	}
	return managed.SignatureFromDER(der)
}

// RedeemAccess binds a proxy key and returns the data-plane endpoint. It does
// not store the access code. The UI stores the endpoint only after success.
func RedeemAccess(code string, signer DeviceSigner) (string, error) {
	code = strings.TrimSpace(code)
	if !managed.ValidAccessCode(code) {
		return "", errors.New("Вставьте ключ прокси из команды /key")
	}
	if signer == nil {
		return "", errors.New("Ключ устройства недоступен")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 22*time.Second)
	defer cancel()
	endpoint, err := managed.Discover(ctx)
	if err != nil {
		return "", errors.New("Не удалось получить адрес шлюза Dagami")
	}
	result, err := managed.RedeemUsing(ctx, endpoint, signerAdapter{signer}, code)
	if err != nil {
		return "", errors.New(portable.Redact(err.Error()))
	}
	if !result.Active {
		return "", errors.New("Шлюз не подтвердил доступ к прокси")
	}
	return endpoint.String(), nil
}

func CheckDagami(endpoint string, signer DeviceSigner) (int, error) {
	return checkDagami(endpoint, signer, false)
}

func CheckDagamiHTTP2(endpoint string, signer DeviceSigner) (int, error) {
	return checkDagami(endpoint, signer, true)
}

func checkDagami(endpoint string, signer DeviceSigner, http2 bool) (int, error) {
	if signer == nil || strings.TrimSpace(endpoint) == "" {
		return 0, errors.New("Сначала добавьте ключ прокси Dagami")
	}
	return check(endpoint, signerAdapter{signer}, http2)
}

func CheckCustom(address string) (int, error) {
	address = strings.TrimSpace(address)
	if address == "" || address == "direct" {
		return 0, errors.New("Введите адрес HTTP, HTTPS, SOCKS4/4a или SOCKS5-прокси")
	}
	return check(address, nil, false)
}

func CheckDirect() (int, error) { return check("direct", nil, false) }

func check(address string, signer managed.ProofSigner, http2 bool) (int, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	status, err := portable.ProbeOpenAITransport(ctx, address, signer, http2)
	if err != nil {
		return 0, errors.New(portable.Redact(err.Error()))
	}
	return status, nil
}
