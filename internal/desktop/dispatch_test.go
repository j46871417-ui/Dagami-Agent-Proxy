package desktop

import (
	"sync"
	"testing"
)

func TestDispatchNeverBlocksAndDropsAfterClose(t *testing.T) {
	var q dispatchQueue
	var group sync.WaitGroup
	calls := 0
	wake := 0
	for i := 0; i < 1000; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			if q.push(func() { calls++ }) {
				q.mu.Lock()
				wake++
				q.mu.Unlock()
			}
		}()
	}
	group.Wait()
	if wake != 1 {
		t.Fatal("expected one pending native wakeup", wake)
	}
	q.drain()
	if calls != 128 {
		t.Fatal("unbounded or missing jobs", calls)
	}
	q.push(func() { t.Fatal("callback after close") })
	q.close()
	q.drain()
	if q.push(func() {}) {
		t.Fatal("closed queue scheduled UI")
	}
}
