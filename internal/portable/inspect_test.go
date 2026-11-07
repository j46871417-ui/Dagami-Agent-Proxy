//go:build !routing_only

package portable

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestRustCoreAcrossChunkBoundary(t *testing.T) {
	core, e := NewByteCore()
	if e != nil {
		t.Fatal(e)
	}
	defer core.Close()
	const chunk = 4 * 1024 * 1024
	data := make([]byte, chunk+20)
	copy(data[chunk-2:], []byte("abcdef"))
	copy(data[chunk+8:], []byte("abcdef"))
	hits, e := core.Find(data, []byte("abcdef"))
	if e != nil || len(hits) != 2 || hits[0] != chunk-2 || hits[1] != chunk+8 {
		t.Fatalf("hits=%v error=%v", hits, e)
	}
	if _, e = core.Find(data, nil); e == nil {
		t.Fatal("empty signature accepted")
	}
}
func TestReadOnlyBinaryInspection(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backend")
	data := []byte(strings.Join(knownSignatures, "\x00") + "\x00https_proxy")
	if e := os.WriteFile(path, data, 0600); e != nil {
		t.Fatal(e)
	}
	r, e := InspectBinary(path)
	if e != nil || !r.KnownShape || r.ProxySites != 1 {
		t.Fatalf("report=%+v e=%v", r, e)
	}
	got, _ := os.ReadFile(path)
	if !bytes.Equal(got, data) {
		t.Fatal("inspection changed the file")
	}
	os.WriteFile(path, []byte("unrelated"), 0600)
	r, e = InspectBinary(path)
	if e != nil || r.KnownShape {
		t.Fatalf("unknown file accepted: %+v %v", r, e)
	}
}
