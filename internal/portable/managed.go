package portable

import (
	"context"
	"errors"
	"net/url"
	"os"
	"strings"

	"dagami.local/agentproxy/internal/managed"
)

type pairing struct {
	ID        string `json:"id"`
	Endpoint  string `json:"endpoint"`
	Protected string `json:"protected"`
}

const managedDefaultID = "dagami-managed"

func pendingManagedProfile() Profile {
	return Profile{ID: managedDefaultID, Name: "Dagami · Telegram", Managed: true}
}

func (c *Controller) ManagedEndpoint() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state.Pairing.Endpoint
}

func (c *Controller) pairingFor(endpoint *url.URL, start bool) (pairing, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.preview {
		return pairing{}, errors.New("привязка отключена в превью")
	}
	if p := c.state.Pairing; p.ID != "" && p.Endpoint == endpoint.String() {
		return p, nil
	}
	if !start {
		return pairing{}, errors.New("сначала начните привязку")
	}
	key, err := managed.NewKey()
	if err != nil {
		return pairing{}, err
	}
	p := pairing{ID: ID(), Endpoint: endpoint.String()}
	p.Protected, err = protectSecret(p.ID, key)
	if err != nil {
		return pairing{}, err
	}
	old := c.state.Pairing
	c.state.Pairing = p
	if err = c.saveLocked(); err != nil {
		c.state.Pairing = old
		return pairing{}, err
	}
	return p, nil
}

func (c *Controller) ActivateManaged(ctx context.Context, raw string, start bool) (managed.PairStatus, error) {
	return c.activateManaged(ctx, raw, start, "")
}

func (c *Controller) UseAccessCode(ctx context.Context, code string) (managed.PairStatus, error) {
	if !managed.ValidAccessCode(code) {
		return managed.PairStatus{}, errors.New("вставьте полный ключ из команды /key")
	}
	return c.activateManaged(ctx, strings.TrimSpace(os.Getenv("DAGAMI_MANAGED_URL")), true, code)
}

func (c *Controller) activateManaged(ctx context.Context, raw string, start bool, code string) (managed.PairStatus, error) {
	endpoint, err := managed.Endpoint(raw)
	if raw == "" {
		endpoint, err = managed.Discover(ctx)
	}
	if err != nil {
		return managed.PairStatus{}, err
	}
	p, err := c.pairingFor(endpoint, start)
	if err != nil {
		return managed.PairStatus{}, err
	}
	encoded, err := unprotectSecret(p.ID, p.Protected)
	if err != nil {
		return managed.PairStatus{}, err
	}
	key, err := managed.ParseKey(encoded)
	if err != nil {
		return managed.PairStatus{}, err
	}
	var result managed.PairStatus
	if code != "" {
		result, err = managed.Redeem(ctx, endpoint, key, code)
	} else {
		result, err = managed.Activation(ctx, endpoint, key, start)
	}
	if err != nil || !result.Active {
		return result, err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.state.Pairing.ID != p.ID {
		return result, errors.New("запрос привязки изменился")
	}
	if c.state.Active == p.ID {
		return result, nil
	}
	if len(c.sessions) != 0 {
		return result, errors.New("остановите активные сессии и проверьте привязку снова")
	}
	old := c.state
	profiles := make([]Profile, 0, len(old.Profiles)+1)
	for _, entry := range old.Profiles {
		if entry.ID != managedDefaultID && (!entry.Managed || entry.Proxy != p.Endpoint) {
			profiles = append(profiles, entry)
		}
	}
	c.state.Profiles = append(profiles, Profile{ID: p.ID, Name: "Dagami · Telegram", Proxy: p.Endpoint, Protected: p.Protected, Managed: true})
	c.state.Active = p.ID
	if err = c.saveLocked(); err != nil {
		c.state = old
		return result, err
	}
	c.eventLocked("Устройство привязано к Dagami")
	return result, nil
}

func routeForProfile(p Profile) (*Bridge, error) {
	raw, err := profileValue(p)
	if err != nil {
		return nil, err
	}
	if !p.Managed {
		u, err := ParseProxy(raw)
		return &Bridge{upstream: u}, err
	}
	endpoint, err := managed.Endpoint(p.Proxy)
	if err != nil {
		return nil, err
	}
	key, err := managed.ParseKey(raw)
	if err != nil {
		return nil, err
	}
	route := &Bridge{upstream: endpoint, proof: func(target string) (string, error) {
		if p.Paid {
			return managed.SignPaid(key, endpoint.Host, "CONNECT", target)
		}
		return managed.Sign(key, endpoint.Host, "CONNECT", target)
	}}
	if useManagedHTTP2() {
		route.h2 = newGatewayHTTP2Transport()
	}
	return route, nil
}

// SelectPaidManaged reuses the registered device, protecting its private key
// under the new profile ID. Provider URLs never become local profiles.
func (c *Controller) SelectPaidManaged() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.preview {
		return errors.New("приватный пул доступен в обычном режиме")
	}
	var source *Profile
	for i := range c.state.Profiles {
		p := &c.state.Profiles[i]
		if p.Managed && !p.Paid && p.Proxy != "" && p.Protected != "" {
			source = p
			break
		}
	}
	if source == nil {
		return errors.New("сначала добавьте ключ прокси Dagami из /key")
	}
	raw, err := profileValue(*source)
	if err != nil {
		return err
	}
	const identifier = "dagami-managed-paid"
	// Verify the protected key without copying/replacing keychain entries.
	if _, err = managed.ParseKey(raw); err != nil {
		return err
	}
	old := c.state
	profiles := []Profile{}
	for _, p := range c.state.Profiles {
		if p.ID != identifier {
			profiles = append(profiles, p)
		}
	}
	c.state.Profiles = append(profiles, Profile{ID: identifier, Name: "Приватные прокси по ключу", Proxy: source.Proxy, Managed: true, Paid: true, Protected: source.Protected, KeyProfile: source.ID})
	c.state.Active = identifier
	if err = c.saveLocked(); err != nil {
		c.state = old
		return err
	}
	c.eventLocked("Выбран приватный пул по ключу; доступность проверяется при подключении")
	return nil
}

func profileValue(p Profile) (string, error) {
	if p.Protected != "" {
		if p.Managed && p.Paid && p.KeyProfile != "" {
			return unprotectSecret(p.KeyProfile, p.Protected)
		}
		return unprotectSecret(p.ID, p.Protected)
	}
	if p.Managed {
		return "", errors.New("сначала привяжите устройство в разделе «Подключение»")
	}
	return p.Proxy, nil
}
