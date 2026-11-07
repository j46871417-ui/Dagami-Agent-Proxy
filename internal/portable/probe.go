package portable

import (
	"bufio"
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net/http"
	"sync"
	"time"
)

func probeHosts(service string) []string {
	switch service {
	case "", "antigravity":
		return []string{"accounts.google.com", "oauth2.googleapis.com", "cloudcode-pa.googleapis.com"}
	case "gemini":
		return []string{"gemini.google.com", "accounts.google.com"}
	case "cursor":
		return []string{"cursor.com", "api2.cursor.sh"}
	case "codex":
		return []string{"chatgpt.com", "api.openai.com"}
	case "claude":
		return []string{"claude.ai", "api.anthropic.com"}
	default:
		return nil
	}
}

type ProbeResult struct {
	Host   string
	Status int
	MS     int64
	Error  string
}

func (c *Controller) Probe(parent context.Context, service string) ([]ProbeResult, error) {
	if c.preview {
		return nil, errors.New("проверка маршрута отключена в превью")
	}
	hosts := probeHosts(service)
	if len(hosts) == 0 {
		return nil, errors.New("выберите сервис для проверки")
	}
	bridge, e := c.activeRoute()
	if e != nil {
		return nil, e
	}
	results := make([]ProbeResult, len(hosts))
	var wg sync.WaitGroup
	for i, host := range hosts {
		wg.Add(1)
		go func(i int, host string) {
			defer wg.Done()
			ctx, cancel := context.WithTimeout(parent, 8*time.Second)
			defer cancel()
			start := time.Now()
			status, e := probeHost(ctx, bridge, host)
			r := ProbeResult{Host: host, MS: time.Since(start).Milliseconds()}
			if e != nil {
				r.Error = "не удалось проверить TLS/HTTP"
			} else {
				r.Status = status
			}
			results[i] = r
		}(i, host)
	}
	wg.Wait()
	c.mu.Lock()
	c.eventLocked("Проверен TLS/HTTP маршрута. Ответ модели и права аккаунта не проверялись.")
	c.mu.Unlock()
	return results, nil
}

func probeHost(ctx context.Context, bridge *Bridge, host string) (int, error) {
	conn, err := bridge.dial(ctx, host, 443)
	if err != nil {
		return 0, err
	}
	defer conn.Close()
	if deadline, ok := ctx.Deadline(); ok {
		conn.SetDeadline(deadline)
	}
	stop := context.AfterFunc(ctx, func() { conn.Close() })
	defer stop()
	tlsConn := tls.Client(conn, &tls.Config{ServerName: host, MinVersion: tls.VersionTLS12})
	if err = tlsConn.HandshakeContext(ctx); err != nil {
		return 0, err
	}
	if _, err = fmt.Fprintf(tlsConn, "HEAD / HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n", host); err != nil {
		return 0, err
	}
	response, err := http.ReadResponse(bufio.NewReader(io.LimitReader(tlsConn, 32<<10)), &http.Request{Method: "HEAD"})
	if err != nil {
		return 0, err
	}
	defer response.Body.Close()
	return response.StatusCode, nil
}
