package portable

import (
	"context"
	"errors"
	"net/url"

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
	endpoint, err := managed.Endpoint(raw)
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
	result, err := managed.Activation(ctx, endpoint, key, start)
	if err != nil || !result.Active {
		return result, err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.state.Pairing.ID != p.ID {
		return result, errors.New("запрос привязки изменился")
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
	return &Bridge{upstream: endpoint, proof: func(target string) (string, error) {
		return managed.Sign(key, endpoint.Host, "CONNECT", target)
	}}, nil
}

func profileValue(p Profile) (string, error) {
	if p.Protected != "" {
		return unprotectSecret(p.ID, p.Protected)
	}
	if p.Managed {
		return "", errors.New("сначала привяжите устройство в разделе «Подключение»")
	}
	return p.Proxy, nil
}
