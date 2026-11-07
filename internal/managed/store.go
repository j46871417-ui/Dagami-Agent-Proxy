package managed

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"sync"
	"time"
)

const pairLifetime = 10 * time.Minute

var (
	loginPattern   = regexp.MustCompile(`^agw_[a-f0-9]{24}$`)
	subjectPattern = regexp.MustCompile(`^[1-9][0-9]{0,18}$`)
	errPair        = errors.New("запрос привязки недействителен или истёк")
	errLimit       = errors.New("достигнут лимит устройств")
)

type binding struct {
	Subject string `json:"subject"`
	Login   string `json:"login"`
}

type pendingPair struct {
	Device  string
	Expires time.Time
}

type registry struct {
	mu      sync.Mutex
	file    string
	devices map[string]binding
	pending map[[32]byte]pendingPair
	tunnels map[string]map[net.Conn]struct{}
}

func openRegistry(path string) (*registry, error) {
	r := &registry{file: path, devices: map[string]binding{}, pending: map[[32]byte]pendingPair{}, tunnels: map[string]map[net.Conn]struct{}{}}
	raw, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return r, nil
	}
	if err != nil {
		return nil, err
	}
	if len(raw) > 4<<20 || json.Unmarshal(raw, &r.devices) != nil || r.devices == nil {
		return nil, errors.New("не удалось прочитать реестр устройств")
	}
	return r, nil
}

func (r *registry) saveLocked(devices map[string]binding) error {
	f, err := os.CreateTemp(filepath.Dir(r.file), ".devices-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	err = json.NewEncoder(f).Encode(devices)
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	if err = os.Rename(f.Name(), r.file); err != nil {
		return err
	}
	r.devices = devices
	return nil
}

func (r *registry) get(device string) (binding, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	value, ok := r.devices[device]
	return value, ok
}

func (r *registry) pair(device string, now time.Time) (string, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, exists := r.devices[device]; exists {
		return "", nil
	}
	for hash, pending := range r.pending {
		if !now.Before(pending.Expires) || pending.Device == device {
			delete(r.pending, hash)
		}
	}
	if len(r.pending) >= 2048 {
		return "", errLimit
	}
	var data [32]byte
	if _, err := rand.Read(data[:]); err != nil {
		return "", err
	}
	code := base64.RawURLEncoding.EncodeToString(data[:])
	r.pending[sha256.Sum256([]byte(code))] = pendingPair{device, now.Add(pairLifetime)}
	return code, nil
}

func (r *registry) approve(code string, b binding, now time.Time) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	hash := sha256.Sum256([]byte(code))
	pending, exists := r.pending[hash]
	if !exists || !now.Before(pending.Expires) || !subjectPattern.MatchString(b.Subject) || !loginPattern.MatchString(b.Login) {
		return errPair
	}
	count := 0
	for _, entry := range r.devices {
		if entry.Subject == b.Subject {
			count++
		}
	}
	if count >= 3 || len(r.devices) >= 10000 {
		return errLimit
	}
	devices := cloneBindings(r.devices)
	devices[pending.Device] = b
	if err := r.saveLocked(devices); err != nil {
		return err
	}
	delete(r.pending, hash)
	return nil
}

func (r *registry) pendingExists(code string, now time.Time) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	pending, ok := r.pending[sha256.Sum256([]byte(code))]
	return ok && now.Before(pending.Expires)
}

func cloneBindings(devices map[string]binding) map[string]binding {
	copy := make(map[string]binding, len(devices))
	for k, v := range devices {
		copy[k] = v
	}
	return copy
}

func (r *registry) revoke(subject string) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	devices := cloneBindings(r.devices)
	for id, b := range devices {
		if b.Subject == subject {
			delete(devices, id)
		}
	}
	if len(devices) == len(r.devices) {
		return nil
	}
	if err := r.saveLocked(devices); err != nil {
		return err
	}
	for id, tunnels := range r.tunnels {
		if _, ok := devices[id]; !ok {
			for conn := range tunnels {
				conn.Close()
			}
		}
	}
	return nil
}

func (r *registry) track(device string, b binding, conn net.Conn) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.devices[device] != b || len(r.tunnels[device]) >= 64 {
		return false
	}
	if r.tunnels[device] == nil {
		r.tunnels[device] = make(map[net.Conn]struct{})
	}
	r.tunnels[device][conn] = struct{}{}
	return true
}

func (r *registry) untrack(device string, conn net.Conn) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.tunnels[device], conn)
	if len(r.tunnels[device]) == 0 {
		delete(r.tunnels, device)
	}
}
