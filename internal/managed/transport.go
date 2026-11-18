package managed

import (
	"context"
	"crypto/tls"
	"errors"
	"net"
	"time"
)

// GatewayCandidates applies to proxy data only. Control-plane addresses never
// become implicit proxy fallbacks. Custom gateways keep their own destination.
func GatewayCandidates(address string) []string {
	if address == bootstrapAuthority {
		return []string{address, "179.254.186.80:9443", "45.86.72.85:9443", "31.76.52.235:9443", "150.241.83.174:9443"}
	}
	return []string{address}
}

// tryGateway retries TCP and TLS together, before any HTTP/proxy payload exists.
// Every attempt has a bounded budget under the caller's overall context.
func tryGateway(ctx context.Context, candidates []string, dial func(context.Context, string) (net.Conn, error), secure func(context.Context, net.Conn) (net.Conn, error)) (net.Conn, error) {
	var last error
	for _, address := range candidates {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		attempt, cancel := context.WithTimeout(ctx, 4*time.Second)
		conn, err := dial(attempt, address)
		if err == nil {
			var secured net.Conn
			secured, err = secure(attempt, conn)
			if err == nil {
				cancel()
				return secured, nil
			}
			conn.Close()
		}
		cancel()
		last = err
	}
	return nil, errors.Join(errors.New("gateway unavailable on permitted routes"), last)
}

func DialGatewayTLS(ctx context.Context, network, address string) (net.Conn, error) {
	return dialGatewayTLS(ctx, network, address, "")
}

func DialGatewayH2(ctx context.Context, network, address string) (net.Conn, error) {
	return dialGatewayTLS(ctx, network, address, "h2")
}

func dialGatewayTLS(ctx context.Context, network, address, protocol string) (net.Conn, error) {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	dialer := &net.Dialer{Timeout: 2 * time.Second, KeepAlive: 30 * time.Second}
	return tryGateway(ctx, GatewayCandidates(address), func(ctx context.Context, target string) (net.Conn, error) {
		return dialer.DialContext(ctx, network, target)
	}, func(ctx context.Context, conn net.Conn) (net.Conn, error) {
		// Always validate the requested hostname, including when dialing an IP.
		config := &tls.Config{ServerName: host, MinVersion: tls.VersionTLS12}
		if protocol != "" {
			config.NextProtos = []string{protocol}
		}
		secured := tls.Client(conn, config)
		if err := secured.HandshakeContext(ctx); err != nil {
			return nil, err
		}
		if protocol != "" && secured.ConnectionState().NegotiatedProtocol != protocol {
			return nil, errors.New("gateway did not negotiate " + protocol)
		}
		return secured, nil
	})
}
