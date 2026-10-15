import unittest
import socket
import time
from devproxy_pkg.core.transport import AdaptiveTLSGate, tune_socket


class TestAdaptiveTLSGate(unittest.TestCase):
    def test_default_gate_acquisition(self):
        gate = AdaptiveTLSGate(base_slots=4, throttled_slots=2, rtt_threshold=1.0)
        self.assertEqual(gate.current_limit, 4)

        with gate.acquire(timeout=1.0):
            self.assertEqual(gate.current_limit, 4)

    def test_tiered_backpressure_throttling(self):
        gate = AdaptiveTLSGate(base_slots=4, throttled_slots=2, rtt_threshold=1.0)
        self.assertEqual(gate.current_limit, 4)

        # High latency RTT updates
        gate.record_rtt(2.5)
        self.assertEqual(gate.current_limit, 2)
        self.assertGreater(gate.ema_rtt, 1.0)

        # Recovering RTT updates
        gate.record_rtt(0.1)
        gate.record_rtt(0.1)
        gate.record_rtt(0.1)
        gate.record_rtt(0.1)
        gate.record_rtt(0.1)
        self.assertEqual(gate.current_limit, 4)

    def test_gate_timeout(self):
        gate = AdaptiveTLSGate(base_slots=1, throttled_slots=1)
        with gate.acquire():
            with self.assertRaises(TimeoutError):
                with gate.acquire(timeout=0.1):
                    pass

    def test_socket_tuning(self):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            tune_socket(s)
            nodelay = s.getsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY)
            self.assertEqual(nodelay, 1)
        finally:
            s.close()


if __name__ == "__main__":
    unittest.main()
