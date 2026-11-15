package portable

import (
	"bufio"
	"bytes"
	"context"
	"crypto/tls"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"dagami.local/agentproxy/internal/managed"
)

// Bridge is one bounded, loopback-only session. TLS payloads are opaque.
type Bridge struct {
	listener net.Listener
	upstream *url.URL
	proof    func(string) (string, error)
	h2       *http.Transport
	slots    chan struct{}
	mu       sync.Mutex
	peers    map[net.Conn]bool
	closed   bool
	wg       sync.WaitGroup
}

func ParseProxy(raw string) (*url.URL, error) {
	if raw == "direct" || raw == "" {
		return nil, nil
	}
	u, e := url.Parse(raw)
	if e != nil || u.Hostname() == "" {
		return nil, errors.New("нужен адрес HTTP, HTTPS, SOCKS4/4a или SOCKS5 прокси")
	}
	switch u.Scheme {
	case "http", "https", "socks4", "socks4a", "socks5", "socks5h", "socks5s":
	default:
		return nil, errors.New("неподдерживаемый протокол прокси")
	}
	if u.RawQuery != "" || u.ForceQuery || strings.Contains(raw, "#") || (u.Path != "" && u.Path != "/") {
		return nil, errors.New("в адресе прокси не должно быть пути или параметров")
	}
	if strings.HasSuffix(u.Host, ":") {
		return nil, errors.New("неверный порт прокси")
	}
	if u.User != nil && (u.Scheme == "socks4" || u.Scheme == "socks4a") {
		if _, passwordSet := u.User.Password(); passwordSet || strings.ContainsRune(u.User.Username(), 0) || len(u.User.Username()) > 255 {
			return nil, errors.New("SOCKS4/4a принимает только идентификатор пользователя")
		}
	}
	if _, e = strconv.Atoi(proxyPort(u)); e != nil {
		return nil, errors.New("неверный порт прокси")
	}
	p, _ := strconv.Atoi(proxyPort(u))
	if p < 1 || p > 65535 {
		return nil, errors.New("порт должен быть от 1 до 65535")
	}
	return u, nil
}
func proxyPort(u *url.URL) string {
	if u.Port() != "" {
		return u.Port()
	}
	if u.Scheme == "https" {
		return "443"
	}
	if strings.HasPrefix(u.Scheme, "socks") {
		return "1080"
	}
	return "80"
}
func StartBridge(raw string) (*Bridge, error) {
	u, e := ParseProxy(raw)
	if e != nil {
		return nil, e
	}
	return (&Bridge{upstream: u}).start()
}

func (b *Bridge) start() (*Bridge, error) {
	l, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		return nil, e
	}
	b.listener, b.slots, b.peers = l, make(chan struct{}, 128), make(map[net.Conn]bool)
	b.wg.Add(1)
	go b.accept()
	return b, nil
}
func (b *Bridge) URL() string { return "http://" + b.listener.Addr().String() }
func (b *Bridge) track(c net.Conn) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.closed {
		c.Close()
		return false
	}
	b.peers[c] = true
	return true
}
func (b *Bridge) drop(c net.Conn) { c.Close(); b.mu.Lock(); delete(b.peers, c); b.mu.Unlock() }
func (b *Bridge) Close() {
	b.mu.Lock()
	if b.closed {
		b.mu.Unlock()
		return
	}
	b.closed = true
	b.listener.Close()
	for c := range b.peers {
		c.Close()
	}
	b.mu.Unlock()
	b.wg.Wait()
	if b.h2 != nil {
		b.h2.CloseIdleConnections()
	}
}
func (b *Bridge) accept() {
	defer b.wg.Done()
	for {
		c, e := b.listener.Accept()
		if e != nil {
			return
		}
		if !b.track(c) {
			return
		}
		select {
		case b.slots <- struct{}{}:
			b.wg.Add(1)
			go func() { defer b.wg.Done(); defer func() { <-b.slots; b.drop(c) }(); b.serve(c) }()
		default:
			c.SetWriteDeadline(time.Now().Add(time.Second))
			io.WriteString(c, "HTTP/1.1 503 Busy\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
			b.drop(c)
		}
	}
}
func writeError(c net.Conn, code int) {
	c.SetWriteDeadline(time.Now().Add(time.Second))
	fmt.Fprintf(c, "HTTP/1.1 %d Proxy error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", code)
}
func (b *Bridge) serve(c net.Conn) {
	c.SetDeadline(time.Now().Add(15 * time.Second))
	reader := bufio.NewReaderSize(c, 32768)
	// ReadRequest's body reader is streaming; the headers are bounded separately.
	var header []byte
	for len(header) < 32768 {
		line, e := reader.ReadSlice('\n')
		if e != nil {
			writeError(c, 431)
			return
		}
		header = append(header, line...)
		if bytes.HasSuffix(header, []byte("\r\n\r\n")) {
			break
		}
	}
	if len(header) >= 32768 {
		writeError(c, 431)
		return
	}
	requestReader := bufio.NewReader(io.MultiReader(strings.NewReader(string(header)), reader))
	req, e := http.ReadRequest(requestReader)
	if e != nil {
		writeError(c, 400)
		return
	}
	target := req.Host
	if req.Method != "CONNECT" {
		if req.URL.Scheme != "http" || req.URL.Host == "" {
			writeError(c, 400)
			return
		}
		target = req.URL.Host
		if req.URL.Port() == "" {
			target = net.JoinHostPort(req.URL.Hostname(), "80")
		}
	}
	host, port, e := net.SplitHostPort(target)
	if e != nil || host == "" {
		writeError(c, 400)
		return
	}
	pn, e := strconv.Atoi(port)
	if e != nil || pn < 1 || pn > 65535 {
		writeError(c, 400)
		return
	}
	// Never forward auth headers supplied to the local bridge.
	req.Header.Del("Proxy-Authorization")
	req.Header.Del("Proxy-Connection")
	budget := 8 * time.Second
	if b.proof != nil {
		budget = 12 * time.Second
	}
	ctx, cancel := context.WithTimeout(context.Background(), budget)
	defer cancel()
	upstream, e := b.dial(ctx, host, pn)
	if e != nil {
		writeError(c, 502)
		return
	}
	if !b.track(upstream) {
		return
	}
	defer b.drop(upstream)
	if req.Method != "CONNECT" {
		req.RequestURI = ""
		req.Close = true
		if e = req.Write(upstream); e != nil {
			return
		}
		c.SetDeadline(time.Time{})
		upstream.SetDeadline(time.Time{})
		io.Copy(c, upstream)
		return
	}
	if _, e = io.WriteString(c, "HTTP/1.1 200 Connection Established\r\n\r\n"); e != nil {
		return
	}
	c.SetDeadline(time.Time{})
	upstream.SetDeadline(time.Time{})
	done := make(chan struct{})
	go func() { io.Copy(upstream, requestReader); closeWrite(upstream); close(done) }()
	io.Copy(c, upstream)
	closeWrite(c)
	// Bound a peer that never finishes its upload after the response closed.
	drain := time.Now().Add(5 * time.Second)
	c.SetReadDeadline(drain)
	upstream.SetWriteDeadline(drain)
	<-done
}
func closeWrite(c net.Conn) {
	if v, ok := c.(interface{ CloseWrite() error }); ok {
		v.CloseWrite()
	}
}
func (b *Bridge) dial(ctx context.Context, host string, port int) (net.Conn, error) {
	c, err := b.dialOnce(ctx, host, port)
	// Only retry before any application data is sent, under the same deadline.
	var status connectStatus
	transient := errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) ||
		(errors.As(err, &status) && (status == 502 || status == 503 || status == 504))
	if b.upstream != nil && transient && ctx.Err() == nil {
		return b.dialOnce(ctx, host, port)
	}
	return c, err
}

func (b *Bridge) dialOnce(ctx context.Context, host string, port int) (net.Conn, error) {
	d := net.Dialer{Timeout: 8 * time.Second, KeepAlive: 30 * time.Second, FallbackDelay: 200 * time.Millisecond}
	if b.upstream == nil {
		return d.DialContext(ctx, "tcp", net.JoinHostPort(host, strconv.Itoa(port)))
	}
	u := b.upstream
	if b.h2 != nil {
		target := net.JoinHostPort(host, strconv.Itoa(port))
		proof, err := b.proof(target)
		if err != nil {
			return nil, err
		}
		return dialHTTP2(ctx, b.h2, u, target, proof)
	}
	endpoint := net.JoinHostPort(u.Hostname(), proxyPort(u))
	var c net.Conn
	var e error
	if b.proof != nil {
		c, e = managed.DialGatewayTLS(ctx, "tcp", endpoint)
	} else {
		c, e = d.DialContext(ctx, "tcp", endpoint)
	}
	if e != nil {
		return nil, e
	}
	success := false
	defer func() {
		if !success {
			c.Close()
		}
	}()
	if deadline, ok := ctx.Deadline(); ok {
		c.SetDeadline(deadline)
	}
	if (u.Scheme == "https" || u.Scheme == "socks5s") && b.proof == nil {
		t := tls.Client(c, &tls.Config{ServerName: u.Hostname(), MinVersion: tls.VersionTLS12})
		if e = t.HandshakeContext(ctx); e != nil {
			return nil, e
		}
		c = t
	}
	switch u.Scheme {
	case "socks4", "socks4a":
		e = socks4Connect(ctx, c, u, host, port)
	case "socks5", "socks5h", "socks5s":
		e = socksConnect(c, u, host, port)
	default:
		e = httpConnect(c, u, host, port, b.proof)
	}
	if e != nil {
		return nil, e
	}
	c.SetDeadline(time.Time{})
	success = true
	return c, nil
}

type connectStatus int

func (s connectStatus) Error() string { return fmt.Sprintf("CONNECT rejected (%d)", s) }

func httpConnect(c net.Conn, u *url.URL, host string, port int, proof func(string) (string, error)) error {
	authority := net.JoinHostPort(host, strconv.Itoa(port))
	auth := ""
	if proof != nil {
		header, err := proof(authority)
		if err != nil {
			return err
		}
		auth = "Proxy-Authorization: " + header + "\r\n"
	} else if u.User != nil {
		p, _ := u.User.Password()
		auth = "Proxy-Authorization: Basic " + base64.StdEncoding.EncodeToString([]byte(u.User.Username()+":"+p)) + "\r\n"
	}
	if _, e := fmt.Fprintf(c, "CONNECT %s HTTP/1.1\r\nHost: %s\r\n%s\r\n", authority, authority, auth); e != nil {
		return e
	}
	// Byte-wise bounded header read avoids swallowing initial TLS bytes.
	buf := make([]byte, 0, 1024)
	one := make([]byte, 1)
	for len(buf) < 16384 {
		if _, e := io.ReadFull(c, one); e != nil {
			return e
		}
		buf = append(buf, one[0])
		if bytes.HasSuffix(buf, []byte("\r\n\r\n")) {
			break
		}
	}
	if len(buf) >= 16384 {
		return errors.New("oversized CONNECT response")
	}
	r, e := http.ReadResponse(bufio.NewReader(strings.NewReader(string(buf))), &http.Request{Method: "CONNECT"})
	if e != nil {
		return e
	}
	if r.StatusCode < 200 || r.StatusCode >= 300 {
		return connectStatus(r.StatusCode)
	}
	return nil
}
func socksConnect(c net.Conn, u *url.URL, host string, port int) error {
	methods := []byte{5, 1, 0}
	if u.User != nil {
		methods = []byte{5, 1, 2}
	}
	if _, e := c.Write(methods); e != nil {
		return e
	}
	r := make([]byte, 2)
	if _, e := io.ReadFull(c, r); e != nil {
		return e
	}
	if r[0] != 5 {
		return errors.New("invalid SOCKS version")
	}
	if r[1] == 2 && u.User != nil {
		password, _ := u.User.Password()
		user := u.User.Username()
		if len(user) == 0 || len(user) > 255 || len(password) == 0 || len(password) > 255 {
			return errors.New("invalid SOCKS credentials length")
		}
		p := append([]byte{1, byte(len(user))}, []byte(user)...)
		p = append(p, byte(len(password)))
		p = append(p, []byte(password)...)
		if _, e := c.Write(p); e != nil {
			return e
		}
		if _, e := io.ReadFull(c, r); e != nil {
			return e
		}
		if r[0] != 1 || r[1] != 0 {
			return errors.New("SOCKS authentication rejected")
		}
	} else if r[1] != 0 || u.User != nil {
		return errors.New("SOCKS method rejected")
	}
	p := []byte{5, 1, 0}
	if ip := net.ParseIP(host); ip != nil {
		if v := ip.To4(); v != nil {
			p = append(p, 1)
			p = append(p, v...)
		} else {
			p = append(p, 4)
			p = append(p, ip.To16()...)
		}
	} else {
		if len(host) > 255 {
			return errors.New("SOCKS hostname too long")
		}
		for _, v := range host {
			if v > 127 {
				return errors.New("use an ASCII/IDNA hostname")
			}
		}
		p = append(p, 3, byte(len(host)))
		p = append(p, host...)
	}
	p = append(p, byte(port>>8), byte(port))
	if _, e := c.Write(p); e != nil {
		return e
	}
	h := make([]byte, 4)
	if _, e := io.ReadFull(c, h); e != nil {
		return e
	}
	if h[0] != 5 || h[1] != 0 || h[2] != 0 {
		return errors.New("SOCKS CONNECT rejected")
	}
	n := 0
	switch h[3] {
	case 1:
		n = 4
	case 4:
		n = 16
	case 3:
		if _, e := io.ReadFull(c, r[:1]); e != nil {
			return e
		}
		n = int(r[0])
		if n == 0 {
			return errors.New("invalid SOCKS response hostname")
		}
	default:
		return errors.New("invalid SOCKS address")
	}
	_, e := io.CopyN(io.Discard, c, int64(n+2))
	return e
}

func socks4Connect(ctx context.Context, c net.Conn, proxy *url.URL, host string, port int) error {
	ip, name := net.ParseIP(host), ""
	if ip == nil {
		if proxy.Scheme == "socks4a" {
			if len(host) > 255 || strings.ContainsRune(host, 0) {
				return errors.New("invalid SOCKS4a hostname")
			}
			for _, ch := range host {
				if ch > 127 {
					return errors.New("use an ASCII/IDNA hostname")
				}
			}
			ip, name = net.IPv4(0, 0, 0, 1), host
		} else {
			addresses, err := net.DefaultResolver.LookupIP(ctx, "ip4", host)
			if err != nil || len(addresses) == 0 {
				return errors.New("SOCKS4 destination has no IPv4 address")
			}
			ip = addresses[0]
		}
	}
	v4 := ip.To4()
	if v4 == nil || (name == "" && v4[0] == 0 && v4[1] == 0 && v4[2] == 0) {
		return errors.New("SOCKS4/4a requires a valid IPv4 destination")
	}
	request := append([]byte{4, 1, byte(port >> 8), byte(port)}, v4...)
	if proxy.User != nil {
		request = append(request, proxy.User.Username()...)
	}
	request = append(request, 0)
	if name != "" {
		request = append(request, name...)
		request = append(request, 0)
	}
	if _, err := c.Write(request); err != nil {
		return err
	}
	var reply [8]byte
	if _, err := io.ReadFull(c, reply[:]); err != nil {
		return err
	}
	if reply[0] != 0 || reply[1] != 90 {
		return errors.New("SOCKS4/4a CONNECT rejected")
	}
	return nil
}
