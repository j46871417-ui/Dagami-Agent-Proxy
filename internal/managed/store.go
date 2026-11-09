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
const AccessPrefix = "DAGAMI1-"
const registryVersion = 1
const accountLimit = 10000

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

type accessCode struct {
	Login     string `json:"login"`
	Code      string `json:"code"`
	Activated bool   `json:"activated,omitempty"`
}

type keyTotals struct {
	Issued    uint64 `json:"issued"`
	Activated uint64 `json:"activated"`
}

type registryState struct {
	Version int                   `json:"version"`
	Devices map[string]binding    `json:"devices"`
	Keys    map[string]accessCode `json:"keys"`
	Totals  *keyTotals            `json:"totals,omitempty"`
}

type registry struct {
	mu      sync.Mutex
	file    string
	devices map[string]binding
	pending map[[32]byte]pendingPair
	keys    map[string]accessCode
	codes   map[[32]byte]binding
	totals  keyTotals
	tunnels map[string]map[net.Conn]net.Conn
	closing bool
	active  sync.WaitGroup
}

func openRegistry(path string) (*registry, error) {
	r := &registry{file: path, devices: map[string]binding{}, pending: map[[32]byte]pendingPair{}, keys: map[string]accessCode{}, codes: map[[32]byte]binding{}, tunnels: map[string]map[net.Conn]net.Conn{}}
	raw, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return r, nil
	}
	if err != nil {
		return nil, err
	}
	invalid := errors.New("не удалось прочитать реестр устройств")
	var fields map[string]json.RawMessage
	if len(raw) > 4<<20 || json.Unmarshal(raw, &fields) != nil || fields == nil {
		return nil, invalid
	}
	if _, versioned := fields["version"]; versioned {
		var state registryState
		if json.Unmarshal(raw, &state) != nil || state.Version != registryVersion || state.Devices == nil || state.Keys == nil || len(state.Keys) > accountLimit {
			return nil, invalid
		}
		r.devices, r.keys = state.Devices, state.Keys
		for subject, key := range state.Keys {
			hash := sha256.Sum256([]byte(key.Code))
			if !subjectPattern.MatchString(subject) || !loginPattern.MatchString(key.Login) || !ValidAccessCode(key.Code) || r.codes[hash].Subject != "" {
				return nil, invalid
			}
			r.codes[hash] = binding{subject, key.Login}
		}
		if state.Totals != nil {
			r.totals = *state.Totals
		} else {
			// The old format did not distinguish key activations from link pairings.
			// Start the activation count with the first confirmed redemption.
			r.totals.Issued = uint64(len(r.keys))
		}
	} else if json.Unmarshal(raw, &r.devices) != nil {
		return nil, invalid
	}
	return r, nil
}

func (r *registry) saveLocked(devices map[string]binding, keys map[string]accessCode, totals keyTotals) error {
	if err := writeState(r.file, registryState{registryVersion, devices, keys, &totals}); err != nil {
		return err
	}
	r.devices, r.keys, r.totals = devices, keys, totals
	return nil
}

func writeState(path string, value any) error {
	f, err := os.CreateTemp(filepath.Dir(path), ".state-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	err = json.NewEncoder(f).Encode(value)
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
	return os.Rename(f.Name(), path)
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
	if err := r.bindLocked(pending.Device, b); err != nil {
		return err
	}
	delete(r.pending, hash)
	return nil
}

func (r *registry) bindLocked(device string, b binding) error {
	devices, err := r.prepareBinding(device, b)
	if err != nil || devices == nil {
		return err
	}
	return r.saveLocked(devices, r.keys, r.totals)
}

// A nil map means the same device is already registered to the account.
func (r *registry) prepareBinding(device string, b binding) (map[string]binding, error) {
	if current, ok := r.devices[device]; ok {
		if current == b {
			return nil, nil
		}
		return nil, errPair
	}
	count := 0
	for _, entry := range r.devices {
		if entry.Subject == b.Subject {
			count++
		}
	}
	if count >= 3 || len(r.devices) >= 10000 {
		return nil, errLimit
	}
	devices := cloneBindings(r.devices)
	devices[device] = b
	return devices, nil
}

func (r *registry) issueAccess(b binding, rotate bool) (string, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !subjectPattern.MatchString(b.Subject) || !loginPattern.MatchString(b.Login) {
		return "", errPair
	}
	key, exists := r.keys[b.Subject]
	if exists && !rotate && key.Login == b.Login {
		return key.Code, nil
	}
	if !exists && len(r.keys) >= accountLimit {
		return "", errLimit
	}
	devices := r.devices
	totals := r.totals
	previousCode := key.Code
	if !exists || rotate {
		var random [32]byte
		if _, err := rand.Read(random[:]); err != nil {
			return "", err
		}
		key.Code = AccessPrefix + encoding.EncodeToString(random[:])
		if _, duplicate := r.codes[sha256.Sum256([]byte(key.Code))]; duplicate {
			return "", errors.New("не удалось сгенерировать уникальный ключ")
		}
		key.Activated = false
		totals.Issued++
	}
	// Keep already paired devices usable if the controller updates the account login.
	for _, current := range r.devices {
		if current.Subject == b.Subject && current.Login != b.Login {
			devices = cloneBindings(r.devices)
			for device, current := range devices {
				if current.Subject == b.Subject {
					devices[device] = b
				}
			}
			break
		}
	}
	key.Login = b.Login
	keys := cloneKeys(r.keys)
	keys[b.Subject] = key
	if err := r.saveLocked(devices, keys, totals); err != nil {
		return "", err
	}
	if exists && rotate {
		delete(r.codes, sha256.Sum256([]byte(previousCode)))
	}
	r.codes[sha256.Sum256([]byte(key.Code))] = b
	return key.Code, nil
}

func (r *registry) redeemAccess(code, device string) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	hash := sha256.Sum256([]byte(code))
	issued, exists := r.codes[hash]
	if !exists {
		return errPair
	}
	devices, err := r.prepareBinding(device, issued)
	if err != nil {
		return err
	}
	key := r.keys[issued.Subject]
	if key.Activated && devices == nil {
		return nil
	}
	if devices == nil {
		devices = r.devices
	}
	keys, totals := r.keys, r.totals
	if !key.Activated {
		key.Activated = true
		keys = cloneKeys(keys)
		keys[issued.Subject] = key
		totals.Activated++
	}
	return r.saveLocked(devices, keys, totals)
}

type keyStats struct {
	keyTotals
	CurrentKeys int `json:"current_keys"`
	Devices     int `json:"devices"`
}

func (r *registry) stats() keyStats {
	r.mu.Lock()
	defer r.mu.Unlock()
	return keyStats{r.totals, len(r.keys), len(r.devices)}
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

func cloneKeys(keys map[string]accessCode) map[string]accessCode {
	copy := make(map[string]accessCode, len(keys))
	for subject, key := range keys {
		copy[subject] = key
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
	key, issued := r.keys[subject]
	if len(devices) != len(r.devices) || issued {
		keys := cloneKeys(r.keys)
		delete(keys, subject)
		if err := r.saveLocked(devices, keys, r.totals); err != nil {
			return err
		}
	}
	if issued {
		delete(r.codes, sha256.Sum256([]byte(key.Code)))
	}
	for id, tunnels := range r.tunnels {
		if _, ok := devices[id]; !ok {
			for conn, upstream := range tunnels {
				conn.Close()
				upstream.Close()
			}
		}
	}
	return nil
}

func (r *registry) track(device string, b binding, conn, upstream net.Conn) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closing || r.devices[device] != b || len(r.tunnels[device]) >= 64 {
		return false
	}
	if r.tunnels[device] == nil {
		r.tunnels[device] = make(map[net.Conn]net.Conn)
	}
	r.tunnels[device][conn] = upstream
	r.active.Add(1)
	return true
}

func (r *registry) untrack(device string, conn net.Conn) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.tunnels[device], conn)
	if len(r.tunnels[device]) == 0 {
		delete(r.tunnels, device)
	}
	r.active.Done()
}

func (r *registry) closeTunnels() {
	r.mu.Lock()
	r.closing = true
	for _, tunnels := range r.tunnels {
		for client, upstream := range tunnels {
			client.Close()
			upstream.Close()
		}
	}
	r.mu.Unlock()
	r.active.Wait()
}
