package managed

import (
	"context"
	"crypto/ecdsa"
	"strings"
)

type poolContextKey struct{}

// PaidPool requests only owner-configured provider exits. It is part of the
// signed audience, so changing a route selector invalidates the device proof.
func PaidPool(ctx context.Context) bool { value, _ := ctx.Value(poolContextKey{}).(bool); return value }
func WithPaidPool(ctx context.Context) context.Context {
	return context.WithValue(ctx, poolContextKey{}, true)
}
func SignPaid(key *ecdsa.PrivateKey, audience, method, target string) (string, error) {
	proof, err := Sign(key, audience+"#paid", method, target)
	if err != nil {
		return "", err
	}
	return "Dagami-Paid " + strings.TrimPrefix(proof, "Dagami "), nil
}
