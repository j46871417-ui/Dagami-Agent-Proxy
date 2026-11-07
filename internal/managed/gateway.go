package managed

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

type Gateway struct {
	registry *registry
	audience string
	dial     func(context.Context, string, string) (net.Conn, int)
	replay   replayGuard
	slots    chan struct{}
}

func OpenGateway(stateFile, endpoint, upstream, bridgeKey string) (*Gateway, error) {
	u, err := Endpoint(endpoint)
	if err != nil {
		return nil, err
	}
	host, port, err := net.SplitHostPort(upstream)
	key, keyErr := encoding.DecodeString(bridgeKey)
	portNumber, portErr := strconv.Atoi(port)
	if err != nil || !net.ParseIP(host).IsLoopback() || portErr != nil || portNumber < 1 || portNumber > 65535 || keyErr != nil || len(key) != 32 {
		return nil, errors.New("upstream должен слушать loopback и требовать отдельный 32-байтовый ключ")
	}
	r, err := openRegistry(stateFile)
	if err != nil {
		return nil, err
	}
	g := &Gateway{registry: r, audience: u.Host, slots: make(chan struct{}, 1024)}
	g.dial = func(ctx context.Context, login, target string) (net.Conn, int) {
		return dialLegacy(ctx, upstream, "Dagami-Local "+bridgeKey+"."+login, target)
	}
	return g, nil
}

func (g *Gateway) Public(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	if r.TLS == nil {
		w.WriteHeader(http.StatusUpgradeRequired)
		return
	}
	select {
	case g.slots <- struct{}{}:
		defer func() { <-g.slots }()
	default:
		w.WriteHeader(http.StatusServiceUnavailable)
		return
	}
	target, header := r.URL.Path, "Authorization"
	if r.Method == http.MethodConnect {
		target, header = r.RequestURI, "Proxy-Authorization"
		host, port, err := net.SplitHostPort(target)
		if err != nil || host == "" || target != r.Host || len(target) > 255 || strings.ContainsAny(host, " /\\@?#\r\n") || (port != "80" && port != "443" && port != "5228") {
			w.WriteHeader(http.StatusBadRequest)
			return
		}
	} else if r.URL.RawQuery != "" || r.Host != g.audience || (r.Method != http.MethodPost || target != "/v1/pair") && (r.Method != http.MethodGet || target != "/v1/status") {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	now := time.Now()
	device := ""
	if values := r.Header.Values(header); len(values) == 1 {
		device = g.replay.verify(values[0], g.audience, r.Method, target, now.Unix())
	}
	if device == "" {
		if r.Method == http.MethodConnect {
			w.WriteHeader(http.StatusProxyAuthRequired)
		} else {
			w.WriteHeader(http.StatusUnauthorized)
		}
		return
	}
	b, active := g.registry.get(device)
	switch r.Method {
	case http.MethodConnect:
		if !active {
			w.WriteHeader(http.StatusProxyAuthRequired)
			return
		}
		g.tunnel(w, r, device, b, target)
	case http.MethodGet:
		writeJSON(w, PairStatus{Active: active})
	case http.MethodPost:
		code, err := g.registry.pair(device, now)
		if err != nil {
			w.WriteHeader(http.StatusServiceUnavailable)
			return
		}
		writeJSON(w, PairStatus{Active: code == "", Code: code})
	}
}

type PairStatus struct {
	Active bool   `json:"active"`
	Code   string `json:"code,omitempty"`
}

func writeJSON(w http.ResponseWriter, value any) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(value)
}

// Private must be served only on a permission-restricted Unix socket.
func (g *Gateway) Private(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	var input struct {
		Code    string `json:"code"`
		Subject string `json:"subject"`
		Login   string `json:"login"`
	}
	dec := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1024))
	dec.DisallowUnknownFields()
	var extra any
	if dec.Decode(&input) != nil || dec.Decode(&extra) != io.EOF {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	if r.URL.Path == "/private/revoke" && subjectPattern.MatchString(input.Subject) {
		if g.registry.revoke(input.Subject) != nil {
			w.WriteHeader(http.StatusServiceUnavailable)
			return
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.URL.Path != "/private/pending" && r.URL.Path != "/private/approve" {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	code, err := encoding.DecodeString(input.Code)
	if err != nil || len(code) != 32 || !g.registry.pendingExists(input.Code, time.Now()) {
		w.WriteHeader(http.StatusGone)
		return
	}
	if r.URL.Path == "/private/approve" {
		err = g.registry.approve(input.Code, binding{input.Subject, input.Login}, time.Now())
		if err != nil {
			status := http.StatusServiceUnavailable
			if errors.Is(err, errPair) {
				status = http.StatusGone
			} else if errors.Is(err, errLimit) {
				status = http.StatusConflict
			}
			w.WriteHeader(status)
			return
		}
	}
	w.WriteHeader(http.StatusNoContent)
}

func (g *Gateway) tunnel(w http.ResponseWriter, r *http.Request, device string, b binding, target string) {
	hijacker, ok := w.(http.Hijacker)
	if !ok {
		w.WriteHeader(http.StatusServiceUnavailable)
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 8*time.Second)
	defer cancel()
	upstream, status := g.dial(ctx, b.Login, target)
	if upstream == nil {
		w.WriteHeader(status)
		return
	}
	defer upstream.Close()
	client, rw, err := hijacker.Hijack()
	if err != nil {
		return
	}
	defer client.Close()
	if !g.registry.track(device, b, client) {
		rw.WriteString("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n")
		rw.Flush()
		return
	}
	defer g.registry.untrack(device, client)
	deadline := time.Now().Add(2 * time.Hour)
	client.SetDeadline(deadline)
	upstream.SetDeadline(deadline)
	if _, err = rw.WriteString("HTTP/1.1 200 Connection Established\r\n\r\n"); err != nil || rw.Flush() != nil {
		return
	}
	finished := make(chan struct{})
	go func() {
		relay(upstream, rw.Reader, client)
		close(finished)
	}()
	relay(client, upstream, upstream)
	<-finished
}

func relay(dst net.Conn, src io.Reader, source net.Conn) {
	if _, err := io.Copy(dst, src); err != nil {
		dst.Close()
		source.Close()
		return
	}
	if half, ok := dst.(interface{ CloseWrite() error }); ok {
		half.CloseWrite()
	} else {
		dst.Close()
	}
}

type bufferedConn struct {
	net.Conn
	reader io.Reader
}

func (c *bufferedConn) Read(data []byte) (int, error) { return c.reader.Read(data) }
func (c *bufferedConn) CloseWrite() error             { return c.Conn.(*net.TCPConn).CloseWrite() }

func dialLegacy(ctx context.Context, address, auth, target string) (net.Conn, int) {
	conn, err := (&net.Dialer{KeepAlive: 30 * time.Second}).DialContext(ctx, "tcp", address)
	if err != nil {
		return nil, http.StatusBadGateway
	}
	if deadline, ok := ctx.Deadline(); ok {
		conn.SetDeadline(deadline)
	}
	if _, err = fmt.Fprintf(conn, "CONNECT %s HTTP/1.1\r\nHost: %s\r\nProxy-Authorization: %s\r\n\r\n", target, target, auth); err != nil {
		conn.Close()
		return nil, http.StatusBadGateway
	}
	// Do not let a malformed local proxy response allocate an unbounded header.
	limited := &io.LimitedReader{R: conn, N: 16 << 10}
	reader := bufio.NewReader(limited)
	response, err := http.ReadResponse(reader, &http.Request{Method: http.MethodConnect})
	if err != nil || response.StatusCode != http.StatusOK {
		conn.Close()
		if err == nil && (response.StatusCode == 403 || response.StatusCode == 407 || response.StatusCode == 503) {
			return nil, response.StatusCode
		}
		return nil, http.StatusBadGateway
	}
	var buffered []byte
	if reader.Buffered() > 0 {
		data, _ := reader.Peek(reader.Buffered())
		buffered = append(buffered, data...)
	}
	return &bufferedConn{conn, io.MultiReader(bytes.NewReader(buffered), conn)}, http.StatusOK
}
