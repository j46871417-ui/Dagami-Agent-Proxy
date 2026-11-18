package main

import (
	"context"
	"dagami.local/agentproxy/internal/managed"
	"net/http"
	"testing"
	"time"
)

func TestPaidSelectionNeverFallsBackToOwnOrFree(t *testing.T) {
	up := map[string]routeHealth{"openai": {Up: true, Checked: time.Now()}}
	pool := &routePool{routes: []*exitRoute{
		{spec: routeSpec{Name: "own-us"}, health: up},
		{spec: routeSpec{Name: "paid_accidental_direct", RequireForeign: true}, health: up, foreign: true},
		{spec: routeSpec{Name: "paid_free", Proxy: "http://1.1.1.1:80", RequireForeign: true, Public: true}, health: up, foreign: true},
	}}
	conn, status := pool.Dial(managed.WithPaidPool(context.Background()), "fixture", "api.openai.com:443")
	if conn != nil || status != http.StatusServiceUnavailable {
		t.Fatal("paid mode used an unowned, direct or public route", status)
	}
}

func TestOrdinaryRouteCannotUsePrivateInventory(t *testing.T) {
	pool := &routePool{routes: []*exitRoute{{spec: routeSpec{Name: "paid_owner", Proxy: "socks5://1.1.1.1:1080", RequireForeign: true}, foreign: true, health: map[string]routeHealth{"openai": {Up: true, Checked: time.Now()}}}}}
	conn, status := pool.Dial(context.Background(), "fixture", "api.openai.com:443")
	if conn != nil || status != http.StatusServiceUnavailable {
		t.Fatal("ordinary mode entered private inventory", status)
	}
}
