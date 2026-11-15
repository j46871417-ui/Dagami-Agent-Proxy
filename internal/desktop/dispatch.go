package desktop

import "sync"

// Native event threads must never wait on a producer holding the close lock.
// Only one wakeup is outstanding; closing drops work for destroyed widgets.
type dispatchQueue struct {
	mu              sync.Mutex
	jobs            []func()
	pending, closed bool
}

func (q *dispatchQueue) push(job func()) bool {
	q.mu.Lock()
	defer q.mu.Unlock()
	if q.closed || len(q.jobs) >= 128 {
		return false
	}
	q.jobs = append(q.jobs, job)
	if q.pending {
		return false
	}
	q.pending = true
	return true
}
func (q *dispatchQueue) drain() {
	q.mu.Lock()
	jobs := q.jobs
	q.jobs = nil
	q.pending = false
	closed := q.closed
	q.mu.Unlock()
	if closed {
		return
	}
	for _, job := range jobs {
		q.mu.Lock()
		closed = q.closed
		q.mu.Unlock()
		if closed {
			return
		}
		job()
	}
}
func (q *dispatchQueue) close() { q.mu.Lock(); q.closed = true; q.jobs = nil; q.mu.Unlock() }
