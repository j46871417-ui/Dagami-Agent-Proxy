"""
core/transport.py
Network transport layer with Tiered Backpressure Concurrency Gate and socket buffer tuning.
Zero-Dependencies implementation using standard library.
"""

import time
import socket
import threading
from contextlib import contextmanager
from typing import Generator, Optional


class AdaptiveTLSGate:
    """
    Tiered Backpressure concurrency limiter for TLS handshakes.
    - Base limit: 64 concurrent slots (fast, unthrottled for healthy upstreams).
    - Throttled limit: 16 slots (when upstream latency degrades).
    - Threshold: Exponential Moving Average (EMA) RTT > 2.0 seconds.
    """

    BASE_SLOTS = 64
    THROTTLED_SLOTS = 16
    RTT_THRESHOLD_SEC = 2.0
    EMA_ALPHA = 0.2

    def __init__(
        self,
        base_slots: int = BASE_SLOTS,
        throttled_slots: int = THROTTLED_SLOTS,
        rtt_threshold: float = RTT_THRESHOLD_SEC,
    ):
        self.base_slots = base_slots
        self.throttled_slots = throttled_slots
        self.rtt_threshold = rtt_threshold

        self._active_limit = self.base_slots
        self._current_in_flight = 0
        self._ema_rtt: Optional[float] = None
        self._lock = threading.Lock()
        self._cond = threading.Condition(self._lock)

    @property
    def current_limit(self) -> int:
        with self._lock:
            return self._active_limit

    @property
    def ema_rtt(self) -> Optional[float]:
        with self._lock:
            return self._ema_rtt

    def record_rtt(self, rtt: float) -> None:
        """Updates moving average RTT and recalibrates gate limit."""
        with self._cond:
            if self._ema_rtt is None:
                self._ema_rtt = rtt
            else:
                self._ema_rtt = (self.EMA_ALPHA * rtt) + ((1.0 - self.EMA_ALPHA) * self._ema_rtt)

            new_limit = (
                self.throttled_slots
                if self._ema_rtt > self.rtt_threshold
                else self.base_slots
            )

            if new_limit != self._active_limit:
                self._active_limit = new_limit
                self._cond.notify_all()

    @contextmanager
    def acquire(self, timeout: float = 10.0) -> Generator[bool, None, None]:
        """
        Acquires an execution slot under the active capacity limit.
        Raises TimeoutError if deadline is exceeded.
        """
        deadline = time.monotonic() + timeout
        with self._cond:
            while self._current_in_flight >= self._active_limit:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(
                        f"Proxy TLS handshake queue deadline exceeded "
                        f"(in_flight={self._current_in_flight}, limit={self._active_limit}, "
                        f"ema_rtt={self._ema_rtt:.2f}s)"
                        if self._ema_rtt is not None
                        else f"Proxy TLS handshake queue deadline exceeded"
                    )
                self._cond.wait(timeout=remaining)

            self._current_in_flight += 1

        try:
            yield True
        finally:
            with self._cond:
                self._current_in_flight -= 1
                self._cond.notify()


def tune_socket(sock: socket.socket) -> None:
    """
    Applies high-throughput low-latency options to a socket:
    - TCP_NODELAY: Disables Nagle's algorithm for instant streaming chunk dispatch.
    - SO_RCVBUF / SO_SNDBUF: Configured to 128KB for smoother high-bandwidth data pipes.
    """
    try:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    except (OSError, socket.error):
        pass

    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 131072)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 131072)
    except (OSError, socket.error):
        pass


class FastDuplexRelay:
    """
    High-performance bidirectional raw byte streaming between two sockets.
    Handles half-close states via shutdown(SHUT_WR).
    """

    BUFFER_SIZE = 65536  # 64 KB

    @classmethod
    def pipe(cls, sock1: socket.socket, sock2: socket.socket) -> None:
        tune_socket(sock1)
        tune_socket(sock2)

        sock1.setblocking(True)
        sock2.setblocking(True)

        def _forward(src: socket.socket, dst: socket.socket):
            try:
                while True:
                    data = src.recv(cls.BUFFER_SIZE)
                    if not data:
                        break
                    dst.sendall(data)
            except Exception:
                pass
            finally:
                try:
                    dst.shutdown(socket.SHUT_WR)
                except Exception:
                    pass

        t1 = threading.Thread(target=_forward, args=(sock1, sock2), daemon=True, name="Relay-1to2")
        t2 = threading.Thread(target=_forward, args=(sock2, sock1), daemon=True, name="Relay-2to1")

        t1.start()
        t2.start()
        t1.join()
        t2.join()

        for s in (sock1, sock2):
            try:
                s.close()
            except Exception:
                pass
