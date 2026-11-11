package portable

import (
	"bufio"
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

const (
	publicProxySource = "https://raw.githubusercontent.com/ProxyScrape/free-proxy-list/main/proxies/protocols/https/data.txt"
	publicProxyMirror = "https://cdn.jsdelivr.net/gh/ProxyScrape/free-proxy-list@main/proxies/protocols/https/data.txt"
)

func isPublicIPv4(ip net.IP) bool {
	v4 := ip.To4()
	if v4 == nil {
		return false
	}
	b0, b1, b2 := v4[0], v4[1], v4[2]
	if b0 == 0 || b0 == 10 || b0 == 127 || b0 >= 224 {
		return false
	}
	if b0 == 100 && b1 >= 64 && b1 <= 127 {
		return false
	}
	if b0 == 169 && b1 == 254 {
		return false
	}
	if b0 == 172 && b1 >= 16 && b1 <= 31 {
		return false
	}
	if b0 == 192 && (b1 == 168 || (b1 == 0 && (b2 == 0 || b2 == 2)) || (b1 == 88 && b2 == 99)) {
		return false
	}
	if b0 == 198 && (b1 == 18 || b1 == 19 || (b1 == 51 && b2 == 100)) {
		return false
	}
	if b0 == 203 && b1 == 0 && b2 == 113 {
		return false
	}
	return true
}

func downloadPublicCandidates(ctx context.Context, source string) ([]*url.URL, error) {
	reqCtx, cancel := context.WithTimeout(ctx, 7*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(reqCtx, http.MethodGet, source, nil)
	if err != nil {
		return nil, err
	}
	client := &http.Client{
		Timeout: 7 * time.Second,
		Transport: &http.Transport{
			Proxy:                 nil,
			TLSHandshakeTimeout:   3 * time.Second,
			ResponseHeaderTimeout: 5 * time.Second,
		},
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, errors.New("ProxyScrape не вернул список")
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, 131073))
	if err != nil {
		return nil, err
	}
	if len(data) > 131072 {
		return nil, errors.New("превышен лимит загрузки списка")
	}
	seen := make(map[string]bool)
	candidates := make([]*url.URL, 0, 12)
	for _, line := range strings.Split(string(data), "\n") {
		raw := strings.TrimSpace(line)
		if raw == "" {
			continue
		}
		u, err := ParseProxy(raw)
		if err != nil || u == nil || u.Scheme != "http" || u.User != nil {
			continue
		}
		ip := net.ParseIP(u.Hostname())
		if ip == nil || !isPublicIPv4(ip) {
			continue
		}
		authority := net.JoinHostPort(u.Hostname(), proxyPort(u))
		if seen[authority] {
			continue
		}
		seen[authority] = true
		candidates = append(candidates, u)
		if len(candidates) == 12 {
			break
		}
	}
	if len(candidates) == 0 {
		return nil, errors.New("в списке нет допустимых адресов")
	}
	return candidates, nil
}

func probePublicProxy(ctx context.Context, candidate *url.URL, host string, skipTLSVerify bool) bool {
	dialCtx, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	endpoint := net.JoinHostPort(candidate.Hostname(), proxyPort(candidate))
	d := net.Dialer{Timeout: 2500 * time.Millisecond}
	conn, err := d.DialContext(dialCtx, "tcp", endpoint)
	if err != nil {
		return false
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err := fmt.Fprintf(conn, "CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n", host, host); err != nil {
		return false
	}
	resp, err := http.ReadResponse(bufio.NewReader(io.LimitReader(conn, 4096)), &http.Request{Method: http.MethodConnect})
	if err != nil {
		return false
	}
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return false
	}
	tlsConn := tls.Client(conn, &tls.Config{
		ServerName:         host,
		MinVersion:         tls.VersionTLS12,
		InsecureSkipVerify: skipTLSVerify,
	})
	tlsCtx, tlsCancel := context.WithTimeout(dialCtx, 2500*time.Millisecond)
	defer tlsCancel()
	return tlsConn.HandshakeContext(tlsCtx) == nil
}

func findPublicProxyFrom(ctx context.Context, sources []string, hosts []string, skipTLSVerify bool) (string, string, error) {
	var candidates []*url.URL
	for _, src := range sources {
		list, err := downloadPublicCandidates(ctx, src)
		if err == nil {
			candidates = list
			break
		}
	}
	if len(candidates) == 0 {
		return "", "", errors.New("список ProxyScrape недоступен через GitHub и jsDelivr")
	}

	probeCtx, cancel := context.WithCancel(ctx)
	defer cancel()

	var (
		mu        sync.Mutex
		foundURL  string
		foundHost string
		wg        sync.WaitGroup
		sem       = make(chan struct{}, 4)
	)

	for _, cand := range candidates {
		wg.Add(1)
		go func(c *url.URL) {
			defer wg.Done()
			select {
			case sem <- struct{}{}:
				defer func() { <-sem }()
			case <-probeCtx.Done():
				return
			}
			for _, host := range hosts {
				if probeCtx.Err() != nil {
					return
				}
				if probePublicProxy(probeCtx, c, host, skipTLSVerify) {
					mu.Lock()
					if foundURL == "" {
						foundURL = "http://" + net.JoinHostPort(c.Hostname(), proxyPort(c))
						foundHost = host
						cancel()
					}
					mu.Unlock()
					return
				}
			}
		}(cand)
	}
	wg.Wait()

	if foundURL == "" {
		return "", "", fmt.Errorf("рабочий публичный туннель до %s не найден", strings.Join(hosts, " / "))
	}
	return foundURL, foundHost, nil
}

// FindPublicProxy searches for an active public HTTPS proxy from ProxyScrape,
// verifies CONNECT + TLS to either Gemini or GitHub/ChatGPT, and activates it.
func (c *Controller) FindPublicProxy(ctx context.Context, requireGemini bool) (string, error) {
	if c.preview {
		return "", errors.New("поиск прокси отключён в превью")
	}
	c.mu.Lock()
	hasSessions := len(c.sessions) > 0
	c.mu.Unlock()
	if hasSessions {
		return "", errors.New("сначала остановите активные сессии")
	}

	hosts := []string{"github.com", "chatgpt.com"}
	if requireGemini {
		hosts = []string{"gemini.google.com"}
	}

	proxyURL, testedHost, err := findPublicProxyFrom(ctx, []string{publicProxySource, publicProxyMirror}, hosts, false)
	if err != nil {
		return "", err
	}

	if err := c.SavePublicProxy(proxyURL, testedHost); err != nil {
		return "", err
	}
	return fmt.Sprintf("Публичный прокси включён: %s\nПроверен TLS до %s.", proxyURL, testedHost), nil
}

func (c *Controller) SavePublicProxy(raw, testedHost string) error {
	if _, err := ParseProxy(raw); err != nil {
		return err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.sessions) > 0 {
		return errors.New("сначала остановите активные сессии")
	}
	old := c.state
	const publicID = "public-proxy"
	profiles := make([]Profile, 0, len(old.Profiles)+1)
	for _, entry := range old.Profiles {
		if entry.ID != publicID {
			profiles = append(profiles, entry)
		}
	}
	profiles = append(profiles, Profile{ID: publicID, Name: "Публичный прокси", Proxy: raw})
	c.state.Profiles = profiles
	c.state.Active = publicID
	if err := c.saveLocked(); err != nil {
		c.state = old
		return err
	}
	c.eventLocked("Публичный прокси включён: " + raw + " (TLS " + testedHost + ")")
	return nil
}
