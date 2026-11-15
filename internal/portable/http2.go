package portable

import (
	"context"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	"dagami.local/agentproxy/internal/managed"
)

func useManagedHTTP2() bool {
	return strings.EqualFold(strings.TrimSpace(os.Getenv("DAGAMI_MANAGED_TRANSPORT")), "h2")
}

func newGatewayHTTP2Transport() *http.Transport {
	return &http.Transport{
		ForceAttemptHTTP2: true,
		DialTLSContext:    managed.DialGatewayH2,
		IdleConnTimeout:   30 * time.Second,
		MaxIdleConns:      4,
	}
}

func dialHTTP2(ctx context.Context, transport http.RoundTripper, endpoint *url.URL, target, proof string) (net.Conn, error) {
	// Only establishment inherits the caller's dial timeout. The lifetime of a
	// successful tunnel is owned by the returned stream.
	streamCtx, cancel := context.WithCancel(context.WithoutCancel(ctx))
	stop := context.AfterFunc(ctx, cancel)
	defer stop()
	upload, send := io.Pipe()
	req := &http.Request{
		Method: http.MethodConnect, URL: endpoint, Host: target, Body: upload,
		ContentLength: -1, Header: http.Header{"Proxy-Authorization": {proof}},
	}
	resp, err := transport.RoundTrip(req.WithContext(streamCtx))
	if err == nil && !stop() && ctx.Err() != nil {
		err = ctx.Err()
	}
	if err != nil || resp.ProtoMajor != 2 || resp.StatusCode != http.StatusOK {
		cancel()
		send.Close()
		if resp != nil {
			resp.Body.Close()
		}
		if err != nil {
			return nil, err
		}
		if resp.ProtoMajor != 2 {
			return nil, errors.New("gateway did not negotiate HTTP/2")
		}
		return nil, connectStatus(resp.StatusCode)
	}
	return &http2Stream{body: resp.Body, send: send, cancel: cancel, remote: proxyAddress(endpoint.Host)}, nil
}

type proxyAddress string

func (a proxyAddress) Network() string { return "h2" }
func (a proxyAddress) String() string  { return string(a) }

type http2Stream struct {
	body   io.ReadCloser
	send   *io.PipeWriter
	cancel context.CancelFunc
	remote proxyAddress
	once   sync.Once
	mu     sync.Mutex
	timer  *time.Timer
	closed bool
}

func (s *http2Stream) Read(b []byte) (int, error)  { return s.body.Read(b) }
func (s *http2Stream) Write(b []byte) (int, error) { return s.send.Write(b) }
func (s *http2Stream) CloseWrite() error           { return s.send.Close() }
func (s *http2Stream) LocalAddr() net.Addr         { return proxyAddress("") }
func (s *http2Stream) RemoteAddr() net.Addr        { return s.remote }
func (s *http2Stream) Close() error {
	s.once.Do(func() {
		s.mu.Lock()
		s.closed = true
		if s.timer != nil {
			s.timer.Stop()
			s.timer = nil
		}
		s.mu.Unlock()
		s.cancel()
		s.send.Close()
		s.body.Close()
	})
	return nil
}
func (s *http2Stream) SetDeadline(deadline time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return net.ErrClosed
	}
	if s.timer != nil {
		s.timer.Stop()
		s.timer = nil
	}
	if !deadline.IsZero() {
		// Closing either side also unblocks the other side of the stream.
		s.timer = time.AfterFunc(time.Until(deadline), func() { s.Close() })
	}
	return nil
}
func (s *http2Stream) SetReadDeadline(deadline time.Time) error  { return s.SetDeadline(deadline) }
func (s *http2Stream) SetWriteDeadline(deadline time.Time) error { return s.SetDeadline(deadline) }
