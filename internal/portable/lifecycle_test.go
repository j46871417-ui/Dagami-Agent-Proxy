package portable

import (
	"os"
	"os/exec"
	"testing"
	"time"
)

func TestLifecycleChild(t *testing.T) {
	if os.Getenv("DAGAMI_LIFECYCLE_FIXTURE") == "1" {
		time.Sleep(time.Minute)
		os.Exit(0)
	}
}
func TestControllerClosePreservesEditor(t *testing.T) {
	cmd := exec.Command(os.Args[0], "-test.run=^TestLifecycleChild$")
	cmd.Env = append(os.Environ(), "DAGAMI_LIFECYCLE_FIXTURE=1")
	prepareProcess(cmd)
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	defer func() {
		cmd.Process.Kill()
		select {
		case <-done:
		case <-time.After(3 * time.Second):
		}
	}()
	stop, detach, err := manageProcess(cmd)
	if err != nil {
		t.Fatal(err)
	}
	bridge, err := StartBridge("direct")
	if err != nil {
		stop()
		t.Fatal(err)
	}
	c := &Controller{sessions: map[string]*session{"fixture": {stop: stop, detach: detach, bridge: bridge}}}
	c.Close()
	select {
	case err := <-done:
		t.Fatalf("launcher close terminated child: %v", err)
	case <-time.After(150 * time.Millisecond):
	}
}
