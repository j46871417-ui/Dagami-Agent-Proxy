package managed

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync/atomic"
	"time"
)

const BotChat = "https://t.me/bright26bot"
const BotURL = BotChat + "?start=dagami_"
const BotKeyURL = BotChat + "?start=dagamikey"
const Bootstrap = "https://test-dns.dagami.ru:9443"
const bootstrapAuthority = "test-dns.dagami.ru:9443"
const bootstrapFallback = "48.102.213.179:9443"

var preferFallbackUntil atomic.Int64

// DialGateway keeps the URL hostname for TLS and signatures. Only the public
// bootstrap address has an IP fallback, independent of system DNS.
func DialGateway(ctx context.Context, dialer *net.Dialer, network, address string) (net.Conn, error) {
	if address != bootstrapAuthority {
		return dialer.DialContext(ctx, network, address)
	}
	if time.Now().Unix() < preferFallbackUntil.Load() {
		if conn, err := dialer.DialContext(ctx, network, bootstrapFallback); err == nil {
			return conn, nil
		}
	}
	dnsCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
	conn, err := dialer.DialContext(dnsCtx, network, address)
	cancel()
	if err == nil || ctx.Err() != nil {
		return conn, err
	}
	conn, err = dialer.DialContext(ctx, network, bootstrapFallback)
	if err == nil {
		preferFallbackUntil.Store(time.Now().Add(time.Minute).Unix())
	}
	return conn, err
}

func ValidAccessCode(code string) bool {
	raw, ok := strings.CutPrefix(code, AccessPrefix)
	if !ok || len(raw) != 43 {
		return false
	}
	value, err := encoding.DecodeString(raw)
	return err == nil && len(value) == 32 && encoding.EncodeToString(value) == raw
}

var activationClient = &http.Client{
	Timeout: 10 * time.Second,
	Transport: &http.Transport{TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12}, DisableKeepAlives: true, DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
		return DialGateway(ctx, &net.Dialer{Timeout: 8 * time.Second}, network, address)
	}},
	CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
}

func Discover(ctx context.Context) (*url.URL, error) {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, Bootstrap+"/v1/config", nil)
	if err != nil {
		return nil, err
	}
	response, err := activationClient.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode == http.StatusNotFound {
		return Endpoint(Bootstrap)
	}
	if response.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("шлюз Dagami вернул HTTP %d", response.StatusCode)
	}
	var config gatewayConfig
	if err = json.NewDecoder(io.LimitReader(response.Body, 1024)).Decode(&config); err != nil {
		return nil, err
	}
	return Endpoint(config.Endpoint)
}

func Redeem(ctx context.Context, endpoint *url.URL, key *ecdsa.PrivateKey, code string) (PairStatus, error) {
	if !ValidAccessCode(code) {
		return PairStatus{}, fmt.Errorf("вставьте полный ключ из команды /key")
	}
	body, err := json.Marshal(map[string]string{"code": code})
	if err != nil {
		return PairStatus{}, err
	}
	return sendActivation(ctx, activationClient, endpoint, key, http.MethodPost, "/v1/redeem", body)
}

func Activation(ctx context.Context, endpoint *url.URL, key *ecdsa.PrivateKey, start bool) (PairStatus, error) {
	return activation(ctx, activationClient, endpoint, key, start)
}

func activation(ctx context.Context, client *http.Client, endpoint *url.URL, key *ecdsa.PrivateKey, start bool) (PairStatus, error) {
	method, path := http.MethodGet, "/v1/status"
	if start {
		method, path = http.MethodPost, "/v1/pair"
	}
	result, err := sendActivation(ctx, client, endpoint, key, method, path, nil)
	if err == nil && start && !result.Active {
		code, decodeErr := encoding.DecodeString(result.Code)
		if decodeErr != nil || len(code) != 32 || encoding.EncodeToString(code) != result.Code {
			return PairStatus{}, fmt.Errorf("шлюз вернул неверный код привязки")
		}
	}
	return result, err
}

func sendActivation(ctx context.Context, client *http.Client, endpoint *url.URL, key *ecdsa.PrivateKey, method, path string, body []byte) (PairStatus, error) {
	proof, err := Sign(key, endpoint.Host, method, path)
	if err != nil {
		return PairStatus{}, err
	}
	request, err := http.NewRequestWithContext(ctx, method, endpoint.String()+path, bytes.NewReader(body))
	if err != nil {
		return PairStatus{}, err
	}
	request.Header.Set("Authorization", proof)
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	response, err := client.Do(request)
	if err != nil {
		return PairStatus{}, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		if path == "/v1/redeem" {
			switch response.StatusCode {
			case http.StatusGone:
				return PairStatus{}, fmt.Errorf("ключ не найден или отозван. Проверьте действующий ключ через /key")
			case http.StatusUnauthorized:
				return PairStatus{}, fmt.Errorf("шлюз не принял подпись устройства. Проверьте автоматическую синхронизацию времени и попробуйте снова")
			case http.StatusConflict:
				return PairStatus{}, fmt.Errorf("достигнут лимит устройств. Отзовите старые устройства в Telegram")
			}
		}
		return PairStatus{}, fmt.Errorf("шлюз Dagami вернул HTTP %d", response.StatusCode)
	}
	var result PairStatus
	if err = json.NewDecoder(io.LimitReader(response.Body, 1024)).Decode(&result); err != nil {
		return PairStatus{}, err
	}
	return result, nil
}
