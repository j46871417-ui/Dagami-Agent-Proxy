package managed

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"os"
	"reflect"
	"strings"
	"sync"
	"time"
)

// ReplicaSnapshot contains public device bindings and access policy only.
// Reusable access codes, passwords and bridge secrets never belong here.
type ReplicaSnapshot struct {
	Version         int                       `json:"version"`
	GeneratedAt     time.Time                 `json:"generated_at"`
	Devices         map[string]binding        `json:"devices"`
	Accounts        map[string]ReplicaAccount `json:"accounts"`
	AllowedHosts    []string                  `json:"allowed_hosts"`
	AllowedSuffixes []string                  `json:"allowed_suffixes"`
	GoogleHosts     []string                  `json:"google_hosts"`
	GoogleSuffixes  []string                  `json:"google_suffixes"`
	GoogleNetworks  []string                  `json:"google_networks"`
}
type ReplicaAccount struct {
	Subject string `json:"subject"`
	Scope   string `json:"scope"`
}
type replicaPolicy struct {
	mu       sync.RWMutex
	snapshot ReplicaSnapshot
	file     string
	raw      string
	dial     func(context.Context, string, string) (net.Conn, int)
}

// OpenReplica serves established devices from an atomically replaced snapshot.
// Registration remains with the issuing gateway, avoiding concurrent issuers.
func OpenReplica(snapshotFile, trafficFile, endpoint string, dial func(context.Context, string, string) (net.Conn, int)) (*Gateway, error) {
	u, err := Endpoint(endpoint)
	if err != nil || dial == nil {
		return nil, errors.New("invalid replica endpoint or dialer")
	}
	meter, err := openTraffic(trafficFile)
	if err != nil {
		return nil, err
	}
	g := &Gateway{registry: &registry{devices: map[string]binding{}, tunnels: map[string]map[io.Closer]io.Closer{}},
		traffic: meter, audience: u.Host, endpoint: u.String(), slots: make(chan struct{}, 1024)}
	g.replica = &replicaPolicy{file: snapshotFile, dial: dial}
	if err = g.ReloadReplica(); err != nil {
		return nil, err
	}
	g.dial = g.replica.connect
	return g, nil
}

func (g *Gateway) ReloadReplica() error {
	if g.replica == nil {
		return nil
	}
	file, err := os.Open(g.replica.file)
	if err != nil {
		return err
	}
	raw, err := io.ReadAll(io.LimitReader(file, (4<<20)+1))
	file.Close()
	if err != nil {
		return err
	}
	if len(raw) > 4<<20 {
		return errors.New("replica snapshot too large")
	}
	var value ReplicaSnapshot
	if json.Unmarshal(raw, &value) != nil || value.Version != 1 || value.GeneratedAt.IsZero() || value.GeneratedAt.After(time.Now().Add(time.Minute)) ||
		value.Devices == nil || value.Accounts == nil || len(value.Devices) > accountLimit*3 || len(value.Accounts) > accountLimit {
		return errors.New("invalid replica snapshot")
	}
	for device, b := range value.Devices {
		public, err := hex.DecodeString(device)
		account, ok := value.Accounts[b.Login]
		if err != nil || len(public) != 32 || hex.EncodeToString(public) != device || !ok || !subjectPattern.MatchString(b.Subject) ||
			account.Subject != b.Subject || (account.Scope != "all" && account.Scope != "google") || !loginPattern.MatchString(b.Login) {
			return errors.New("invalid replica binding")
		}
	}
	policy := g.replica
	policy.mu.Lock()
	defer policy.mu.Unlock()
	if policy.raw == string(raw) {
		return nil
	}
	if !policy.snapshot.GeneratedAt.IsZero() && value.GeneratedAt.Before(policy.snapshot.GeneratedAt) {
		return errors.New("replica snapshot moved backwards")
	}
	g.registry.mu.Lock()
	policyChanged := !reflect.DeepEqual(policy.snapshot.AllowedHosts, value.AllowedHosts) || !reflect.DeepEqual(policy.snapshot.AllowedSuffixes, value.AllowedSuffixes) || !reflect.DeepEqual(policy.snapshot.GoogleHosts, value.GoogleHosts) || !reflect.DeepEqual(policy.snapshot.GoogleSuffixes, value.GoogleSuffixes) || !reflect.DeepEqual(policy.snapshot.GoogleNetworks, value.GoogleNetworks)
	for device, tunnels := range g.registry.tunnels {
		previous := g.registry.devices[device]
		if replacement, ok := value.Devices[device]; !ok || replacement != previous || policyChanged || value.Accounts[previous.Login] != policy.snapshot.Accounts[previous.Login] {
			for client, upstream := range tunnels {
				client.Close()
				upstream.Close()
			}
		}
	}
	g.registry.devices = value.Devices
	g.registry.mu.Unlock()
	policy.snapshot = value
	policy.raw = string(raw)
	return nil
}

func (p *replicaPolicy) connect(ctx context.Context, login, target string) (net.Conn, int) {
	p.mu.RLock()
	account, ok := p.snapshot.Accounts[login]
	allowed := ok && replicaTargetAllowed(p.snapshot, account.Scope, target)
	p.mu.RUnlock()
	if !ok {
		return nil, http.StatusProxyAuthRequired
	}
	if !allowed {
		return nil, http.StatusForbidden
	}
	return p.dial(ctx, login, target)
}
func replicaTargetAllowed(snapshot ReplicaSnapshot, scope, target string) bool {
	host, port, err := net.SplitHostPort(target)
	if err != nil || (port != "80" && port != "443" && port != "5228") {
		return false
	}
	host = strings.ToLower(strings.TrimSuffix(host, "."))
	if ip := net.ParseIP(host); ip != nil {
		if !PublicDestination(ip) {
			return false
		}
		for _, value := range snapshot.GoogleNetworks {
			_, network, err := net.ParseCIDR(value)
			if err == nil && network.Contains(ip) {
				return true
			}
		}
		return false
	}
	exact, suffixes := snapshot.AllowedHosts, snapshot.AllowedSuffixes
	if scope == "google" {
		exact, suffixes = snapshot.GoogleHosts, snapshot.GoogleSuffixes
	}
	for _, value := range exact {
		if host == value {
			return true
		}
	}
	for _, value := range suffixes {
		if strings.HasPrefix(value, ".") && strings.HasSuffix(host, value) {
			return true
		}
	}
	return false
}

// PublicDestination also excludes special-use ranges accepted by IsGlobalUnicast.
func PublicDestination(ip net.IP) bool {
	if !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() {
		return false
	}
	for _, value := range []string{"100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4", "2001:db8::/32", "64:ff9b:1::/48"} {
		_, network, _ := net.ParseCIDR(value)
		if network.Contains(ip) {
			return false
		}
	}
	return true
}

// ConnectLocal reuses CONNECT framing while limiting the bridge to loopback.
func ConnectLocal(ctx context.Context, address, target string) (net.Conn, int) {
	host, _, err := net.SplitHostPort(address)
	if err != nil || !net.ParseIP(host).IsLoopback() {
		return nil, http.StatusBadGateway
	}
	return dialLegacy(ctx, address, "", target)
}
