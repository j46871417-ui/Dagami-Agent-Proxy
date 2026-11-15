//go:build !routing_only

package portable

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"testing"
)

func TestDNSRecoveryAfterFailedCommandAndConflict(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	journal := filepath.Join(dir, "dns-backup.json")
	servers := []string{"192.0.2.10"}
	read := func(context.Context, string) ([]string, error) { return slices.Clone(servers), nil }
	fail := true
	write := func(_ context.Context, _ string, target []string) error {
		if _, err := os.Stat(journal); err != nil {
			t.Fatal("wrote DNS before saving journal", err)
		}
		if fail {
			return errors.New("permission declined")
		}
		servers = slices.Clone(target)
		return nil
	}
	if _, err := changeUnixDNS(ctx, dir, "linux", "apply", "192.0.2.11", "eth0", read, write); err == nil {
		t.Fatal("failed apply reported success")
	}
	if _, err := os.Stat(journal); !os.IsNotExist(err) {
		t.Fatal("unchanged DNS left a blocking journal", err)
	}
	fail = false
	if _, err := changeUnixDNS(ctx, dir, "linux", "apply", "192.0.2.11", "eth0", read, write); err != nil {
		t.Fatal(err)
	}
	servers = []string{"192.0.2.12"}
	if _, err := changeUnixDNS(ctx, dir, "linux", "restore", "", "", read, write); err == nil || servers[0] != "192.0.2.12" {
		t.Fatal("overwrote externally changed DNS", err)
	}
	servers = []string{"192.0.2.10"}
	fail = true
	if _, err := changeUnixDNS(ctx, dir, "linux", "restore", "", "", read, write); err != nil {
		t.Fatal("already restored DNS should close the journal", err)
	}
}
