package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"
	"sync"
	"time"

	"dagami.local/agentproxy/internal/managed"
	"dagami.local/agentproxy/internal/portable"
)

type routeSpec struct {
	Name           string    `json:"name"`
	Proxy          string    `json:"proxy"`
	Public         bool      `json:"public,omitempty"`
	RequireForeign bool      `json:"require_foreign,omitempty"`
	Disabled       bool      `json:"disabled,omitempty"`
	ExpiresAt      time.Time `json:"expires_at,omitempty"`
}
type routeConfig struct {
	SourceIP string      `json:"source_ip"`
	Routes   []routeSpec `json:"routes"`
}
type routeHealth struct {
	Up        bool      `json:"up"`
	Checked   time.Time `json:"checked"`
	Failures  int       `json:"failures"`
	Successes int       `json:"successes"`
	LatencyMS int64     `json:"latency_ms"`
}
type exitRoute struct {
	spec    routeSpec
	bridge  *portable.Bridge
	health  map[string]routeHealth
	foreign bool
}
type routePool struct {
	mu         sync.Mutex
	routes     []*exitRoute
	source     net.IP
	healthFile string
	configFile string
	configRaw  string
}

var serviceProbes = map[string][]struct {
	URL    string
	Status int
}{
	"google":  {{"https://gemini.google.com/app", 200}, {"https://www.googleapis.com/oauth2/v2/userinfo", 401}},
	"openai":  {{"https://api.openai.com/v1/models", 401}},
	"general": {{"https://example.com/", 200}},
}
var routeName = regexp.MustCompile(`^[a-z0-9_-]{1,48}$`)

func serviceFor(target string) string {
	host, _, _ := net.SplitHostPort(target)
	host = strings.ToLower(strings.TrimSuffix(host, "."))
	if host == "api.openai.com" || host == "chatgpt.com" || strings.HasSuffix(host, ".openai.com") || strings.HasSuffix(host, ".chatgpt.com") {
		return "openai"
	}
	for _, suffix := range []string{"google.com", "googleapis.com", "googleusercontent.com", "gstatic.com", "goog", "google.dev"} {
		if host == suffix || strings.HasSuffix(host, "."+suffix) {
			return "google"
		}
	}
	return "general"
}
func newRoutePool(configPath, healthFile string) (*routePool, error) {
	file, err := os.Open(configPath)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	var config routeConfig
	data, err := io.ReadAll(io.LimitReader(file, 65537))
	if err != nil || len(data) > 65536 {
		return nil, errors.New("route configuration too large")
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	var extra any
	if decoder.Decode(&config) != nil || decoder.Decode(&extra) != io.EOF || len(config.Routes) == 0 || len(config.Routes) > 32 {
		return nil, errors.New("invalid route configuration")
	}
	source := net.ParseIP(config.SourceIP)
	if source == nil || source.To4() == nil || !managed.PublicDestination(source) {
		return nil, errors.New("invalid source address")
	}
	pool := &routePool{source: source, healthFile: healthFile, configFile: configPath, configRaw: string(data)}
	names := map[string]bool{}
	for _, spec := range config.Routes {
		if !routeName.MatchString(spec.Name) || names[spec.Name] || (spec.Public && spec.Proxy == "") {
			pool.Close()
			return nil, errors.New("invalid route name")
		}
		names[spec.Name] = true
		if spec.Disabled || (!spec.ExpiresAt.IsZero() && !time.Now().Before(spec.ExpiresAt)) {
			continue
		}
		route := &exitRoute{spec: spec, health: map[string]routeHealth{}}
		if spec.Proxy != "" {
			parsed, err := portable.ParseProxy(spec.Proxy)
			if err != nil || parsed == nil {
				pool.Close()
				return nil, errors.New("invalid upstream proxy")
			}
			// Public-list entries must be literal, globally routed IPv4 addresses.
			if spec.Public {
				ip := net.ParseIP(parsed.Hostname())
				if ip == nil || ip.To4() == nil || !managed.PublicDestination(ip) || parsed.User != nil {
					pool.Close()
					return nil, errors.New("invalid public proxy")
				}
			}
			route.bridge, err = portable.StartBridge(spec.Proxy)
			if err != nil {
				pool.Close()
				return nil, errors.New("upstream bridge failed")
			}
		}
		pool.routes = append(pool.routes, route)
	}
	return pool, nil
}
func (p *routePool) Close() {
	p.mu.Lock()
	routes := append([]*exitRoute(nil), p.routes...)
	p.mu.Unlock()
	for _, route := range routes {
		if route.bridge != nil {
			route.bridge.Close()
		}
	}
}
func (p *routePool) direct(ctx context.Context, target string) (net.Conn, error) {
	host, port, err := net.SplitHostPort(target)
	if err != nil {
		return nil, err
	}
	addresses, err := net.DefaultResolver.LookupIP(ctx, "ip4", host)
	if err != nil {
		return nil, errors.New("destination lookup failed")
	}
	dialer := net.Dialer{Timeout: 3 * time.Second, KeepAlive: 30 * time.Second, LocalAddr: &net.TCPAddr{IP: p.source}}
	for _, ip := range addresses {
		if !managed.PublicDestination(ip) {
			continue
		}
		conn, err := dialer.DialContext(ctx, "tcp4", net.JoinHostPort(ip.String(), port))
		if err == nil {
			return conn, nil
		}
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
	}
	return nil, errors.New("public destination unavailable")
}
func (p *routePool) connect(ctx context.Context, route *exitRoute, target string) (net.Conn, error) {
	if route.bridge == nil {
		return p.direct(ctx, target)
	}
	parsed, _ := url.Parse(route.bridge.URL())
	conn, status := managed.ConnectLocal(ctx, parsed.Host, target)
	if status != http.StatusOK {
		return nil, errors.New("upstream CONNECT failed")
	}
	return conn, nil
}
func updateHealth(previous routeHealth, err error, elapsed time.Duration, now time.Time) routeHealth {
	previous.Checked = now
	previous.LatencyMS = elapsed.Milliseconds()
	if err != nil {
		previous.Failures++
		previous.Successes = 0
		if previous.Failures >= 2 {
			previous.Up = false
		}
	} else {
		previous.Successes++
		previous.Failures = 0
		if previous.Successes >= 2 {
			previous.Up = true
		}
	}
	return previous
}
func (p *routePool) candidates(service string, now time.Time) []*exitRoute {
	p.mu.Lock()
	defer p.mu.Unlock()
	var selected []*exitRoute
	for _, route := range p.routes {
		health := route.health[service]
		if health.Up && now.Sub(health.Checked) < 3*time.Minute && (route.spec.ExpiresAt.IsZero() || now.Before(route.spec.ExpiresAt)) && (!(route.spec.Public || route.spec.RequireForeign) || route.foreign) {
			selected = append(selected, route)
		}
	}
	return selected
}
func (p *routePool) Dial(ctx context.Context, login, target string) (net.Conn, int) {
	_ = login // Route priority stays stable for each service until its health changes.
	for _, route := range p.candidates(serviceFor(target), time.Now()) {
		privateRoute := strings.HasPrefix(route.spec.Name, "paid_") && !route.spec.Public && route.spec.Proxy != "" && route.spec.RequireForeign
		if managed.PaidPool(ctx) != privateRoute {
			continue
		}
		attempt, cancel := context.WithTimeout(ctx, 4*time.Second)
		conn, err := p.connect(attempt, route, target)
		cancel()
		if err == nil {
			conn.SetDeadline(time.Time{})
			return conn, http.StatusOK
		}
		p.mu.Lock()
		route.health[serviceFor(target)] = updateHealth(route.health[serviceFor(target)], err, 0, time.Now())
		p.mu.Unlock()
	}
	return nil, http.StatusServiceUnavailable
}
func (p *routePool) request(ctx context.Context, route *exitRoute, address string) (int, string, string, error) {
	transport := &http.Transport{
		DisableKeepAlives: true,
		TLSClientConfig:   &tls.Config{MinVersion: tls.VersionTLS12},
		DialContext: func(ctx context.Context, network, target string) (net.Conn, error) {
			return p.connect(ctx, route, target)
		},
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 6 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	request, _ := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	request.Header.Set("User-Agent", "Dagami-Network-Health/1")
	response, err := client.Do(request)
	if err != nil {
		return 0, "", "", errors.New("HTTPS probe failed")
	}
	defer response.Body.Close()
	body, err := io.ReadAll(io.LimitReader(response.Body, 8192))
	return response.StatusCode, response.Header.Get("Location"), string(body), err
}
func (p *routePool) probe(ctx context.Context, route *exitRoute, service string) error {
	for _, check := range serviceProbes[service] {
		status, location, body, err := p.request(ctx, route, check.URL)
		if err != nil {
			return err
		}
		if strings.Contains(body, "User location is not supported") || strings.Contains(body, "unsupported_country") || strings.Contains(body, "Your location is not eligible") {
			return errors.New("region rejected")
		}
		if status != check.Status {
			redirect, err := url.Parse(location)
			if service != "google" || status != 302 || err != nil || redirect.Scheme != "https" || redirect.Hostname() != "accounts.google.com" {
				return errors.New("unexpected service response")
			}
		}
	}
	return nil
}
func (p *routePool) Probe(ctx context.Context) {
	slots := make(chan struct{}, 6)
	var workers sync.WaitGroup
	p.mu.Lock()
	routes := append([]*exitRoute(nil), p.routes...)
	p.mu.Unlock()
	for _, route := range routes {
		if route.spec.Public || route.spec.RequireForeign {
			status, _, body, err := p.request(ctx, route, "https://www.cloudflare.com/cdn-cgi/trace")
			foreign := err == nil && status == 200 && foreignLocation(body)
			p.mu.Lock()
			route.foreign = foreign
			p.mu.Unlock()
		}
		for service := range serviceProbes {
			workers.Add(1)
			go func(route *exitRoute, service string) {
				defer workers.Done()
				select {
				case slots <- struct{}{}:
					defer func() { <-slots }()
				case <-ctx.Done():
					return
				}
				start := time.Now()
				err := p.probe(ctx, route, service)
				p.mu.Lock()
				route.health[service] = updateHealth(route.health[service], err, time.Since(start), time.Now())
				p.mu.Unlock()
			}(route, service)
		}
	}
	workers.Wait()
	p.mu.Lock()
	result := map[string]map[string]routeHealth{}
	for _, route := range p.routes {
		copy := map[string]routeHealth{}
		for name, value := range route.health {
			if (route.spec.Public || route.spec.RequireForeign) && !route.foreign {
				value.Up = false
			}
			copy[name] = value
		}
		result[route.spec.Name] = copy
	}
	p.mu.Unlock()
	if data, err := json.Marshal(result); err == nil {
		atomicFile(p.healthFile, data, 0640)
	}
}

// Reload keeps healthy, unchanged routes and closes removed/expired providers.
// Credentials are never returned to desktop clients or included in health JSON.
func (p *routePool) Reload() error {
	file, err := os.Open(p.configFile)
	if err != nil {
		return err
	}
	raw, err := io.ReadAll(io.LimitReader(file, 65537))
	file.Close()
	if err != nil || len(raw) > 65536 {
		return errors.New("invalid route configuration")
	}
	p.mu.Lock()
	unchanged := string(raw) == p.configRaw
	for _, route := range p.routes {
		if !route.spec.ExpiresAt.IsZero() && !time.Now().Before(route.spec.ExpiresAt) {
			unchanged = false
		}
	}
	p.mu.Unlock()
	if unchanged {
		return nil
	}
	next, err := newRoutePool(p.configFile, p.healthFile)
	if err != nil {
		return err
	}
	var closeRoutes []*exitRoute
	p.mu.Lock()
	if !p.source.Equal(next.source) {
		p.mu.Unlock()
		next.Close()
		return errors.New("changing source address requires restart")
	}
	existing := map[string]*exitRoute{}
	for _, route := range p.routes {
		existing[route.spec.Name] = route
	}
	for index, route := range next.routes {
		if old := existing[route.spec.Name]; old != nil && reflect.DeepEqual(old.spec, route.spec) {
			next.routes[index] = old
			closeRoutes = append(closeRoutes, route)
			delete(existing, route.spec.Name)
		}
	}
	for _, route := range existing {
		closeRoutes = append(closeRoutes, route)
	}
	p.routes = next.routes
	p.configRaw = next.configRaw
	p.mu.Unlock()
	for _, route := range closeRoutes {
		if route.bridge != nil {
			route.bridge.Close()
		}
	}
	return nil
}
func atomicFile(path string, data []byte, mode os.FileMode) error {
	file, err := os.CreateTemp(filepath.Dir(path), ".reserve-")
	if err != nil {
		return err
	}
	temp := file.Name()
	defer os.Remove(temp)
	if err = file.Chmod(mode); err == nil {
		_, err = file.Write(data)
	}
	if err == nil {
		err = file.Sync()
	}
	closed := file.Close()
	if err == nil {
		err = closed
	}
	if err != nil {
		return err
	}
	return os.Rename(temp, path)
}
func foreignLocation(body string) bool {
	for _, line := range strings.Split(body, "\n") {
		if strings.HasPrefix(line, "loc=") {
			code := strings.TrimPrefix(line, "loc=")
			// Only explicitly recognised foreign egress locations are eligible.
			return strings.Contains(" US CA GB DE FR NL SE FI NO DK IS IE CH AT BE ES IT PT PL CZ EE LV LT JP KR SG AU NZ BR IN ZA ", " "+code+" ") && len(code) == 2
		}
	}
	return false
}
