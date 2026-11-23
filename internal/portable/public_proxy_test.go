package portable

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestIsPublicIPv4(t *testing.T) {
	cases := []struct {
		ip   string
		want bool
	}{
		{"8.8.8.8", true},
		{"1.1.1.1", true},
		{"90.156.160.234", true},
		{"127.0.0.1", false},
		{"10.0.0.1", false},
		{"192.168.1.1", false},
		{"172.16.0.1", false},
		{"100.64.0.1", false},
		{"169.254.1.1", false},
		{"224.0.0.1", false},
	}
	for _, tc := range cases {
		got := isPublicIPv4(net.ParseIP(tc.ip))
		if got != tc.want {
			t.Fatalf("isPublicIPv4(%s) = %v, want %v", tc.ip, got, tc.want)
		}
	}
}

func TestDownloadPublicCandidatesFiltersPrivateAndDuplicates(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(strings.Join([]string{
			"http://127.0.0.1:8080",
			"http://192.168.1.50:3128",
			"http://user:pass@8.8.8.8:8080",
			"socks5://1.1.1.1:1080",
			"http://8.8.8.8:3128",
			"http://8.8.8.8:3128",
			"http://1.1.1.1:8080",
		}, "\n")))
	}))
	defer srv.Close()

	list, err := downloadPublicCandidates(context.Background(), srv.URL)
	if err != nil {
		t.Fatalf("download failed: %v", err)
	}
	if len(list) != 2 || list[0].Host != "8.8.8.8:3128" || list[1].Host != "1.1.1.1:8080" {
		t.Fatalf("unexpected candidate list: %v", list)
	}
}

func TestSavePublicProxyReplacesAndActivates(t *testing.T) {
	c, err := NewController(t.TempDir(), false)
	if err != nil {
		t.Fatal(err)
	}
	if err := c.SavePublicProxy("http://8.8.8.8:3128", "github.com"); err != nil {
		t.Fatal(err)
	}
	if err := c.SavePublicProxy("http://1.1.1.1:8080", "gemini.google.com"); err != nil {
		t.Fatal(err)
	}
	snap := c.Snapshot()
	if snap.State.Active != "public-proxy" {
		t.Fatalf("active profile = %q, want public-proxy", snap.State.Active)
	}
	count := 0
	for _, p := range snap.State.Profiles {
		if p.ID == "public-proxy" {
			count++
			if p.Proxy != "http://1.1.1.1:8080" {
				t.Fatalf("proxy = %q", p.Proxy)
			}
		}
	}
	if count != 1 {
		t.Fatalf("expected 1 public-proxy entry, got %d", count)
	}
}
