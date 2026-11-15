//go:build !windows

package portable

import (
	"context"
	"strings"
	"testing"
)

func TestInstanceForwardsLaunchAndRecovery(t *testing.T) {
	c, err := NewController(t.TempDir(), false)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	instance, err := StartInstance(c)
	if err != nil {
		t.Fatal(err)
	}
	defer instance.Close()
	if duplicate, err := StartInstance(c); err == nil {
		duplicate.Close()
		t.Fatal("second instance acquired lock")
	}
	if forwarded, err := ForwardLaunch(context.Background(), c.dir, "unknown-app"); !forwarded || err == nil || !strings.Contains(err.Error(), "путь") {
		t.Fatalf("local launch was not forwarded: %t %v", forwarded, err)
	}
	if err := c.SaveProfile("Fixture", "http://127.0.0.1:9"); err != nil {
		t.Fatal(err)
	}
	if report, err := StandaloneRecovery(context.Background(), c.dir); err != nil || !report.Success() || c.Snapshot().State.Active != "direct" {
		t.Fatalf("running instance failed recovery: %+v %v", report, err)
	}
}
