//go:build !routing_only

package portable

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestBinaryInspectionAcrossChunkBoundary(t *testing.T) {
	data := make([]byte, inspectChunkSize+64)
	copy(data[inspectChunkSize-2:], []byte("https_proxy"))
	copy(data[inspectChunkSize+10:], []byte("https_proxy"))
	report, err := scanBinary(bytes.NewReader(data))
	if err != nil || report.ProxySites != 2 || report.Bytes != len(data) {
		t.Fatalf("report=%+v error=%v", report, err)
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
