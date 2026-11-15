//go:build routing_only

package portable

import (
	"context"
	"errors"
)

const RoutingOnly = true

var errRoutingOnly = errors.New("эта функция доступна в полной версии Dagami")

func InspectBinary(string) (any, error) { return nil, errRoutingOnly }

func (c *Controller) InspectApp(string) (any, error) { return nil, errRoutingOnly }

func (c *Controller) DNSStatus(context.Context) (any, error) { return nil, errRoutingOnly }

func (c *Controller) DNSChange(context.Context, string, string, string) (any, error) {
	return nil, errRoutingOnly
}

func restoreRecordedDNS(context.Context, string) (bool, error) { return false, nil }

func (c *Controller) restoreDNS(context.Context) (bool, error) { return false, nil }
