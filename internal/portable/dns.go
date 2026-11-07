package portable

import (
	"context"
	"errors"
	"net"
	"strings"
)

func (c *Controller) DNSStatus(ctx context.Context) (any, error) { return dnsStatus(ctx) }
func (c *Controller) DNSChange(ctx context.Context, operation, server, iface string) (any, error) {
	if c.preview {
		return nil, errors.New("DNS отключён в превью")
	}
	if !c.dnsMu.TryLock() {
		return nil, errors.New("дождитесь завершения текущей DNS-операции")
	}
	defer c.dnsMu.Unlock()
	if operation != "apply" && operation != "restore" && operation != "reset" {
		return nil, errors.New("неизвестная операция")
	}
	if operation == "apply" && net.ParseIP(strings.TrimSpace(server)) == nil {
		return nil, errors.New("для ручной записи укажите IP DNS-сервера")
	}
	value, e := dnsChange(ctx, c.dir, operation, strings.TrimSpace(server), iface)
	c.mu.Lock()
	if e != nil {
		c.eventLocked("DNS: операция не подтверждена — " + e.Error())
	} else {
		c.eventLocked("DNS: " + operation + " выполнена; итог: " + value)
	}
	c.mu.Unlock()
	return value, e
}
