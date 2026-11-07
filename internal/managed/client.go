package managed

import (
	"context"
	"crypto/ecdsa"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"time"
)

const BotURL = "https://t.me/bright26bot?start=dagami_"

var activationClient = &http.Client{
	Timeout:       10 * time.Second,
	Transport:     &http.Transport{TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12}, DisableKeepAlives: true},
	CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
}

func Activation(ctx context.Context, endpoint *url.URL, key *ecdsa.PrivateKey, start bool) (PairStatus, error) {
	return activation(ctx, activationClient, endpoint, key, start)
}

func activation(ctx context.Context, client *http.Client, endpoint *url.URL, key *ecdsa.PrivateKey, start bool) (PairStatus, error) {
	method, path := http.MethodGet, "/v1/status"
	if start {
		method, path = http.MethodPost, "/v1/pair"
	}
	proof, err := Sign(key, endpoint.Host, method, path)
	if err != nil {
		return PairStatus{}, err
	}
	request, err := http.NewRequestWithContext(ctx, method, endpoint.String()+path, nil)
	if err != nil {
		return PairStatus{}, err
	}
	request.Header.Set("Authorization", proof)
	response, err := client.Do(request)
	if err != nil {
		return PairStatus{}, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return PairStatus{}, fmt.Errorf("шлюз Dagami вернул HTTP %d", response.StatusCode)
	}
	var result PairStatus
	if err = json.NewDecoder(io.LimitReader(response.Body, 1024)).Decode(&result); err != nil {
		return PairStatus{}, err
	}
	if start && !result.Active {
		code, err := encoding.DecodeString(result.Code)
		if err != nil || len(code) != 32 || encoding.EncodeToString(code) != result.Code {
			return PairStatus{}, fmt.Errorf("шлюз вернул неверный код привязки")
		}
	}
	return result, nil
}
