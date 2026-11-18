package main

import (
	"context"
	"crypto/subtle"
	"dagami.local/agentproxy/internal/managed"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"
)

func runBroker(listen, authFile string, pool *routePool) error {
	host, _, err := net.SplitHostPort(listen)
	if err != nil || !net.ParseIP(host).IsLoopback() {
		return errors.New("broker listener must be loopback")
	}
	secret, err := os.ReadFile(authFile)
	if err != nil {
		return err
	}
	auth := strings.TrimSpace(string(secret))
	if !strings.HasPrefix(auth, "Basic ") || len(auth) < 20 || strings.ContainsAny(auth, "\r\n") {
		return errors.New("invalid broker credential")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	pool.Probe(ctx)
	pool.Probe(ctx)
	handler, closeTunnels := brokerHandler(pool, auth)
	defer closeTunnels()
	server := &http.Server{Handler: handler, ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 10 * time.Second, MaxHeaderBytes: 8192}
	listener, err := net.Listen("tcp", listen)
	if err != nil {
		return err
	}
	defer listener.Close()
	defer server.Close()
	failed := make(chan error, 1)
	go func() { failed <- server.Serve(listener) }()
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
	for {
		select {
		case <-ctx.Done():
			return nil
		case err := <-failed:
			return err
		case <-ticker.C:
			if err = pool.Reload(); err != nil {
				fmt.Fprintln(os.Stderr, "broker route reload rejected")
			}
		}
	}
}

func brokerHandler(pool *routePool, auth string) (http.HandlerFunc, func()) {
	slots := make(chan struct{}, 4096)
	var mu sync.Mutex
	connections := map[net.Conn]bool{}
	closing := false
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "CONNECT" || len(r.Header.Values("Proxy-Authorization")) != 1 || subtle.ConstantTimeCompare([]byte(r.Header.Get("Proxy-Authorization")), []byte(auth)) != 1 {
			w.WriteHeader(407)
			return
		}
		host, port, err := net.SplitHostPort(r.Host)
		if err != nil || host == "" || len(r.Host) > 255 || r.RequestURI != r.Host || strings.ContainsAny(host, " /\\@?#\r\n") || (port != "443" && port != "80" && port != "5228") {
			w.WriteHeader(400)
			return
		}
		select {
		case slots <- struct{}{}:
			defer func() { <-slots }()
		default:
			w.WriteHeader(503)
			return
		}
		requestCtx, cancel := context.WithTimeout(r.Context(), 8*time.Second)
		defer cancel()
		if r.Header.Get("X-Dagami-Pool") == "paid" {
			requestCtx = managed.WithPaidPool(requestCtx)
		}
		upstream, status := pool.Dial(requestCtx, "", r.Host)
		if upstream == nil {
			w.WriteHeader(status)
			return
		}
		defer upstream.Close()
		client, buffer, err := w.(http.Hijacker).Hijack()
		if err != nil {
			return
		}
		defer client.Close()
		mu.Lock()
		if closing {
			mu.Unlock()
			return
		}
		connections[client] = true
		mu.Unlock()
		defer func() { mu.Lock(); delete(connections, client); mu.Unlock() }()
		client.SetDeadline(time.Now().Add(2 * time.Hour))
		upstream.SetDeadline(time.Now().Add(2 * time.Hour))
		buffer.WriteString("HTTP/1.1 200 Connection Established\r\n\r\n")
		if buffer.Flush() != nil {
			return
		}
		done := make(chan struct{}, 2)
		go func() {
			io.Copy(upstream, buffer)
			if conn, ok := upstream.(interface{ CloseWrite() error }); ok {
				conn.CloseWrite()
			} else {
				upstream.Close()
			}
			done <- struct{}{}
		}()
		go func() {
			io.Copy(client, upstream)
			if conn, ok := client.(interface{ CloseWrite() error }); ok {
				conn.CloseWrite()
			} else {
				client.Close()
			}
			done <- struct{}{}
		}()
		<-done
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			client.Close()
			upstream.Close()
			<-done
		}
	})
	return handler, func() {
		mu.Lock()
		defer mu.Unlock()
		closing = true
		for conn := range connections {
			conn.Close()
		}
	}
}
