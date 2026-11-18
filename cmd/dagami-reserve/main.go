// dagami-reserve is an independent server for existing, signed device bindings.
// It never issues keys. A primary outage cannot turn it into an open proxy.
package main

import (
	"context"
	"crypto/tls"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"dagami.local/agentproxy/internal/managed"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run() error {
	listen := flag.String("listen", "127.0.0.1:19443", "private TLS listener")
	brokerAuth := flag.String("broker-auth-file", "", "serve loopback-only CONNECT broker using a private Basic header file")
	endpoint := flag.String("endpoint", "https://test-dns.dagami.ru:9443", "proof audience")
	snapshot := flag.String("snapshot", "", "public device bindings and access policy")
	traffic := flag.String("traffic", "", "local traffic counter file")
	routes := flag.String("routes", "", "private egress configuration")
	health := flag.String("health", "", "private health status file")
	cert := flag.String("cert", "", "certificate file")
	key := flag.String("key", "", "private key file")
	primaryIP := flag.String("primary-ip", "", "primary IPv4 for registration forwarding only")
	refresh := flag.String("refresh-socket", "", "private snapshot refresh Unix socket")
	flag.Parse()
	if (*brokerAuth == "" && (*snapshot == "" || *traffic == "")) || *routes == "" || *health == "" {
		return errors.New("snapshot, traffic, routes and health files required")
	}
	canonical, err := managed.Endpoint(*endpoint)
	if err != nil {
		return err
	}
	pool, err := newRoutePool(*routes, *health)
	if err != nil {
		return err
	}
	defer pool.Close()
	if *brokerAuth != "" {
		return runBroker(*listen, *brokerAuth, pool)
	}
	gateway, err := managed.OpenReplica(*snapshot, *traffic, *endpoint, pool.Dial)
	if err != nil {
		return err
	}
	certificate, err := tls.LoadX509KeyPair(*cert, *key)
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	// Require recovery hysteresis at startup as well as during operation.
	pool.Probe(ctx)
	pool.Probe(ctx)
	handler := http.HandlerFunc(gateway.Public)
	if *primaryIP != "" {
		ip := net.ParseIP(*primaryIP)
		if ip == nil || ip.To4() == nil || !managed.PublicDestination(ip) {
			return errors.New("invalid primary IP")
		}
		forward := registrationForwarder(canonical, ip.String(), *refresh, gateway)
		handler = func(w http.ResponseWriter, r *http.Request) {
			if r.Method == http.MethodPost && (r.URL.Path == "/v1/pair" || r.URL.Path == "/v1/redeem") {
				forward(w, r)
				return
			}
			gateway.Public(w, r)
		}
	}
	server := &http.Server{Handler: handler, ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 10 * time.Second, WriteTimeout: 15 * time.Second, IdleTimeout: 20 * time.Second, MaxHeaderBytes: 8192}
	listener, err := net.Listen("tcp", *listen)
	if err != nil {
		return err
	}
	defer listener.Close()
	secure := tls.NewListener(listener, &tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS12, NextProtos: []string{"http/1.1"}})
	failed := make(chan error, 1)
	go func() { failed <- server.Serve(secure) }()
	go func() {
		ticker := time.NewTicker(20 * time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				pool.Probe(ctx)
			}
		}
	}()
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	var result error
	defer func() { server.Close(); gateway.CloseTunnels(); gateway.FlushTraffic() }()
	ticks := 0
	lastReloadError := false
	for {
		select {
		case <-ctx.Done():
			return nil
		case result = <-failed:
			if errors.Is(result, http.ErrServerClosed) {
				return nil
			}
			return result
		case <-ticker.C:
			if err := pool.Reload(); err != nil {
				if ticks%60 == 0 {
					fmt.Fprintln(os.Stderr, "route reload rejected; retaining last valid pool")
				}
			}
			err := gateway.ReloadReplica()
			if err != nil && !lastReloadError {
				fmt.Fprintln(os.Stderr, "snapshot reload rejected; retaining last valid policy")
			}
			lastReloadError = err != nil
			ticks++
			if ticks%15 == 0 {
				if err := gateway.FlushTraffic(); err != nil {
					fmt.Fprintln(os.Stderr, "traffic checkpoint failed")
				}
			}
		}
	}
}

func registrationForwarder(canonical *url.URL, primaryIP, refresh string, gateway *managed.Gateway) http.HandlerFunc {
	transport := &http.Transport{Proxy: nil, DisableKeepAlives: true, TLSClientConfig: &tls.Config{ServerName: canonical.Hostname(), MinVersion: tls.VersionTLS12}, DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
		return (&net.Dialer{Timeout: 4 * time.Second}).DialContext(ctx, "tcp4", net.JoinHostPort(primaryIP, canonical.Port()))
	}}
	client := &http.Client{Transport: transport, Timeout: 8 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	slots := make(chan struct{}, 16)
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		if r.TLS == nil || r.Host != canonical.Host || r.URL.RawQuery != "" || len(r.Header.Values("Authorization")) != 1 {
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		select {
		case slots <- struct{}{}:
			defer func() { <-slots }()
		default:
			w.WriteHeader(503)
			return
		}
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1024))
		if err != nil {
			w.WriteHeader(413)
			return
		}
		req, err := http.NewRequestWithContext(r.Context(), http.MethodPost, canonical.String()+r.URL.Path, strings.NewReader(string(body)))
		if err != nil {
			w.WriteHeader(503)
			return
		}
		req.Header.Set("Authorization", r.Header.Get("Authorization"))
		req.Header.Set("Content-Type", "application/json")
		response, err := client.Do(req)
		if err != nil {
			w.WriteHeader(503)
			return
		}
		defer response.Body.Close()
		output, err := io.ReadAll(io.LimitReader(response.Body, 4097))
		if err != nil || len(output) > 4096 {
			w.WriteHeader(502)
			return
		}
		if response.StatusCode == 200 {
			if err = refreshSnapshot(r.Context(), refresh); err != nil {
				w.WriteHeader(503)
				return
			}
			if err = gateway.ReloadReplica(); err != nil {
				w.WriteHeader(503)
				return
			}
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(response.StatusCode)
		w.Write(output)
	}
}
func refreshSnapshot(ctx context.Context, socket string) error {
	if socket == "" {
		return errors.New("refresh unavailable")
	}
	transport := &http.Transport{DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, "unix", socket)
	}}
	defer transport.CloseIdleConnections()
	request, _ := http.NewRequestWithContext(ctx, "POST", "http://localhost/refresh", nil)
	response, err := (&http.Client{Transport: transport, Timeout: 4 * time.Second}).Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != 204 {
		return errors.New("refresh failed")
	}
	return nil
}
