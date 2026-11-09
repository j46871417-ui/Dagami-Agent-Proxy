package main

import (
	"context"
	"crypto/tls"
	"errors"
	"flag"
	"fmt"
	"net"
	"net/http"
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
	listen := flag.String("listen", "127.0.0.1:50443", "HTTPS listen address")
	endpoint := flag.String("endpoint", "", "canonical public HTTPS URL, including a non-default port")
	cert := flag.String("cert", "", "public TLS certificate chain file")
	key := flag.String("key", "", "TLS private key file")
	state := flag.String("state", "", "device registry file in a private state directory")
	botSocket := flag.String("bot-socket", "", "private Unix socket in a restricted runtime directory")
	upstream := flag.String("upstream", "", "private legacy HAProxy IPv4/IPv6 loopback address and port")
	bridgeKey := flag.String("bridge-key-file", "", "file with an unpadded base64url-encoded 32-byte bridge secret")
	flag.Parse()
	if *state == "" || *botSocket == "" || *key == "" || *cert == "" || *bridgeKey == "" {
		return errors.New("set -endpoint, -cert, -key, -state, -bot-socket, -upstream and -bridge-key-file")
	}
	secret, err := os.ReadFile(*bridgeKey)
	if err != nil {
		return err
	}
	gateway, err := managed.OpenGateway(*state, *endpoint, *upstream, strings.TrimSpace(string(secret)))
	if err != nil {
		return err
	}
	certificate, err := tls.LoadX509KeyPair(*cert, *key)
	if err != nil {
		return err
	}
	private, err := net.Listen("unix", *botSocket)
	if err != nil {
		return err
	}
	defer private.Close()
	if err = os.Chmod(*botSocket, 0660); err != nil {
		return err
	}
	public, err := net.Listen("tcp", *listen)
	if err != nil {
		return err
	}
	defer public.Close()
	privateServer := server(gateway.Private)
	publicServer := server(gateway.Public)
	publicServer.TLSConfig = &tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS12, NextProtos: []string{"http/1.1"}}
	failed := make(chan error, 2)
	go func() { failed <- privateServer.Serve(private) }()
	go func() { failed <- publicServer.Serve(tls.NewListener(public, publicServer.TLSConfig)) }()
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	checkpoint := time.NewTicker(15 * time.Second)
	defer checkpoint.Stop()
	var serveErr error
	for running := true; running; {
		select {
		case <-ctx.Done():
			running = false
		case serveErr = <-failed:
			running = false
		case <-checkpoint.C:
			if err := gateway.FlushTraffic(); err != nil {
				fmt.Fprintln(os.Stderr, "traffic checkpoint:", err)
			}
		}
	}
	publicServer.Close()
	privateServer.Close()
	gateway.CloseTunnels()
	return errors.Join(serveErr, gateway.FlushTraffic())
}

func server(handler http.HandlerFunc) *http.Server {
	return &http.Server{Handler: handler, ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 10 * time.Second,
		WriteTimeout: 15 * time.Second, IdleTimeout: 20 * time.Second, MaxHeaderBytes: 8192}
}
