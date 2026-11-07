//go:build !routing_only

package portable

import (
	"context"
	_ "embed"
	"errors"
	"sync"

	"github.com/tetratelabs/wazero"
	"github.com/tetratelabs/wazero/api"
)

//go:embed core.wasm
var byteCore []byte

type ByteCore struct {
	mu      sync.Mutex
	runtime wazero.Runtime
	module  api.Module
	base    uint32
}

func NewByteCore() (*ByteCore, error) {
	ctx := context.Background()
	r := wazero.NewRuntimeWithConfig(ctx, wazero.NewRuntimeConfig().WithMemoryLimitPages(128).WithCloseOnContextDone(true))
	m, e := r.Instantiate(ctx, byteCore)
	if e != nil {
		r.Close(ctx)
		return nil, e
	}
	v, e := m.ExportedFunction("abi_version").Call(ctx)
	if e != nil || v[0] != 1 {
		r.Close(ctx)
		return nil, errors.New("несовместимое ядро Rust")
	}
	p, e := m.ExportedFunction("buffer").Call(ctx)
	if e != nil {
		r.Close(ctx)
		return nil, e
	}
	return &ByteCore{runtime: r, module: m, base: uint32(p[0])}, nil
}
func (b *ByteCore) Close() { b.runtime.Close(context.Background()) }

// Find handles large files in bounded chunks with overlap, never allocating
// module memory proportional to the binary size.
func (b *ByteCore) Find(data, needle []byte) ([]int, error) {
	if len(needle) == 0 || len(needle) > 4096 {
		return nil, errors.New("неверная длина сигнатуры")
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	ctx := context.Background()
	hits := []int{}
	const chunk = 4 * 1024 * 1024
	next := 0
	for offset := 0; offset < len(data); {
		end := offset + chunk
		if end > len(data) {
			end = len(data)
		}
		part := data[offset:end]
		if !b.module.Memory().Write(b.base, part) || !b.module.Memory().Write(b.base+uint32(len(part)), needle) {
			return nil, errors.New("границы памяти Rust")
		}
		start := next - offset
		if start < 0 {
			start = 0
		}
		for start <= len(part) {
			v, e := b.module.ExportedFunction("find").Call(ctx, uint64(len(part)), uint64(len(needle)), uint64(start))
			if e != nil {
				return nil, e
			}
			found := int32(v[0])
			if found == -1 {
				break
			}
			if found < 0 {
				return nil, errors.New("Rust отклонил границы поиска")
			}
			absolute := offset + int(found)
			hits = append(hits, absolute)
			if len(hits) > 1024 {
				return nil, errors.New("неоднозначная сигнатура: слишком много совпадений")
			}
			next = absolute + len(needle)
			start = int(found) + len(needle)
		}
		if end == len(data) {
			break
		}
		offset = end - len(needle) + 1
	}
	return hits, nil
}
