import io
import socket
import threading
import time
import unittest
from unittest.mock import patch

from devproxy_pkg.core import http_wire, peer_identity
from devproxy_pkg.core.profile import ProxyProfile
from devproxy_pkg.core.tunnel import LocalTunnel


def header(peer):
    result = bytearray()
    while not result.endswith(b'\r\n\r\n'):
        value = peer.recv(1)
        if not value:
            raise ConnectionError('Truncated fixture header')
        result.extend(value)
        if len(result) > 65536:
            raise ValueError('Fixture header too large')
    return bytes(result)


class TunnelSecurityTests(unittest.TestCase):
    def test_os_identifies_current_loopback_user_and_denies_unknown(self):
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); listener.listen()
            with socket.create_connection(listener.getsockname()) as client, listener.accept()[0] as accepted:
                self.assertTrue(peer_identity.is_current_user(accepted))
                with patch('devproxy_pkg.core.peer_identity.sys.platform', 'unsupported'):
                    self.assertFalse(peer_identity.is_current_user(accepted))

    def test_foreign_identity_never_reaches_upstream(self):
        with socket.socket() as upstream:
            upstream.bind(('127.0.0.1', 0)); upstream.listen(); upstream.settimeout(.2)
            tunnel = LocalTunnel(ProxyProfile('fixture', 'http', '127.0.0.1', upstream.getsockname()[1]))
            with patch('devproxy_pkg.core.tunnel.is_current_user', return_value=False):
                try:
                    port = tunnel.start()
                    with socket.create_connection(('127.0.0.1', port)) as client:
                        client.settimeout(3)
                        self.assertIn(b' 403 ', header(client))
                    with self.assertRaises(socket.timeout):
                        upstream.accept()
                finally:
                    tunnel.stop()

    def test_linux_tuple_owner_parser(self):
        table = 'header\n0: 0100007F:ABCD 0100007F:CFD0 01 0:0 0:0 0 1234 0 1\n'
        with patch('pathlib.Path.open', return_value=io.StringIO(table)):
            self.assertEqual(1234, peer_identity._linux_uid(('127.0.0.1', 0xABCD), ('127.0.0.1', 0xCFD0)))

    def test_live_connection_limit_and_recovery(self):
        clients, accepted = [], []
        accepted_lock = threading.Lock()
        with socket.socket() as unavailable, socket.socket() as backend, patch.object(LocalTunnel, 'MAX_CONNECTIONS', 4):
            unavailable.bind(('127.0.0.1', 0))
            backend.bind(('127.0.0.1', 0)); backend.listen(); backend.settimeout(.2)
            stopping = threading.Event()
            def accept():
                while not stopping.is_set():
                    try:
                        peer = backend.accept()[0]
                        with accepted_lock:
                            accepted.append(peer)
                    except socket.timeout:
                        pass
                    except OSError:
                        return
            worker = threading.Thread(target=accept, daemon=True); worker.start()
            tunnel = LocalTunnel(ProxyProfile('fixture', 'http', '127.0.0.1', unavailable.getsockname()[1]))
            try:
                port = tunnel.start()
                for index in range(4):
                    client = socket.create_connection(('127.0.0.1', port)); client.settimeout(5)
                    clients.append(client)
                    authority = f'127.0.0.1:{backend.getsockname()[1]}'
                    client.sendall(f'CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n'.encode())
                    self.assertIn(b' 200 ', header(client))
                    if index == 0:
                        # FIFO accept plus a completed CONNECT proves the readiness
                        # probe was admitted. Wait for that probe's lease to leave;
                        # merely observing an initially empty counter is racy.
                        deadline = time.monotonic() + 5
                        while tunnel._work_slots._value != 3 and time.monotonic() < deadline:
                            time.sleep(.01)
                        self.assertEqual(3, tunnel._work_slots._value)
                with socket.create_connection(('127.0.0.1', port)) as denied:
                    denied.settimeout(5)
                    self.assertIn(b' 503 ', header(denied))
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline:
                    with accepted_lock:
                        if len(accepted) == 4:
                            break
                    time.sleep(.01)
                for client in clients:
                    client.close()
                with accepted_lock:
                    self.assertEqual(4, len(accepted))
                    for peer in accepted:
                        peer.close()
                deadline = time.monotonic() + 5
                while tunnel._work_slots._value != 4 and time.monotonic() < deadline:
                    time.sleep(.01)
                self.assertEqual(4, tunnel._work_slots._value)
                with socket.create_connection(('127.0.0.1', port)) as client:
                    client.settimeout(5)
                    client.sendall(b'CONNECT fixture.invalid:443 HTTP/1.1\r\nHost: fixture.invalid\r\n\r\n')
                    self.assertIn(b' 502 ', header(client))
            finally:
                for client in clients:
                    client.close()
                with accepted_lock:
                    for peer in accepted:
                        peer.close()
                tunnel.stop(); stopping.set(); worker.join(2)

    def test_http_closes_each_framed_transaction_and_authenticates_reconnect(self):
        seen, errors = [], []
        with socket.socket() as upstream:
            upstream.bind(('127.0.0.1', 0)); upstream.listen(); upstream.settimeout(5)
            def serve():
                try:
                    for _ in range(2):
                        with upstream.accept()[0] as peer:
                            peer.settimeout(5)
                            seen.append(header(peer))
                            peer.sendall(b'HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK')
                            self.assertEqual(b'', peer.recv(1))
                except Exception as error:
                    errors.append(error)
            worker = threading.Thread(target=serve, daemon=True); worker.start()
            tunnel = LocalTunnel(ProxyProfile('fixture', 'http', '127.0.0.1', upstream.getsockname()[1], 'fixture', 'password'))
            try:
                port = tunnel.start()
                for _ in range(2):
                    with socket.create_connection(('127.0.0.1', port)) as client:
                        client.settimeout(5)
                        client.sendall(b'GET http://fixture.invalid/first HTTP/1.1\r\nHost: fixture.invalid\r\nProxy-Authorization: Basic client-secret\r\n\r\nGET http://fixture.invalid/second HTTP/1.1\r\nHost: fixture.invalid\r\n\r\n')
                        self.assertIn(b'Connection: close', header(client))
                        body = bytearray()
                        while True:
                            chunk = client.recv(1024)
                            if not chunk:
                                break
                            body.extend(chunk)
                        self.assertEqual(b'OK', body)
                worker.join(5)
                self.assertFalse(worker.is_alive())
                self.assertFalse(errors)
                self.assertEqual(2, len(seen))
                for request in seen:
                    self.assertIn(b'Proxy-Authorization: Basic Zml4dHVyZTpwYXNzd29yZA==', request)
                    self.assertNotIn(b'client-secret', request)
                    self.assertIn(b'Connection: close', request)
            finally:
                tunnel.stop()

    def test_reject_ambiguous_http_and_nonloopback_bind(self):
        with self.assertRaises(ValueError):
            http_wire.request_headers(b'POST / HTTP/1.1\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n', 'http://fixture.invalid/', True)
        with self.assertRaises(ValueError):
            LocalTunnel(ProxyProfile('fixture', 'http', '127.0.0.1', 1), '0.0.0.0')

    def test_rpc_response_can_start_before_chunked_upload_finishes(self):
        errors = []
        with socket.socket() as backend:
            backend.bind(('127.0.0.1', 0)); backend.listen(); backend.settimeout(5)
            def serve():
                try:
                    with backend.accept()[0] as peer:
                        peer.settimeout(5)
                        header(peer)
                        def exact(count):
                            result = bytearray()
                            while len(result) < count:
                                value = peer.recv(count - len(result))
                                if not value:
                                    raise ConnectionError('Incomplete RPC upload')
                                result.extend(value)
                            return bytes(result)
                        self.assertEqual(b'5\r\nhello\r\n', exact(10))
                        peer.sendall(b'HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nfirst\r\n')
                        self.assertEqual(b'0\r\n\r\n', exact(5))
                        peer.sendall(b'0\r\nx-rpc-status: ok\r\n\r\n')
                except Exception as error:
                    errors.append(error)
            worker = threading.Thread(target=serve, daemon=True); worker.start()
            tunnel = LocalTunnel(ProxyProfile('fixture', 'http', '127.0.0.1', 1))
            try:
                port = tunnel.start()
                with socket.create_connection(('127.0.0.1', port)) as client:
                    client.settimeout(5)
                    target = backend.getsockname()[1]
                    client.sendall(f'POST http://127.0.0.1:{target}/rpc HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n'.encode())
                    self.assertIn(b' 200 ', header(client))
                    first = bytearray()
                    while len(first) < 10:
                        first.extend(client.recv(10 - len(first)))
                    self.assertEqual(b'5\r\nfirst\r\n', first)
                    client.sendall(b'0\r\n\r\n')
                    rest = bytearray()
                    while True:
                        chunk = client.recv(1024)
                        if not chunk:
                            break
                        rest.extend(chunk)
                    self.assertEqual(b'0\r\nx-rpc-status: ok\r\n\r\n', rest)
                worker.join(5)
                self.assertFalse(worker.is_alive())
                self.assertFalse(errors)
            finally:
                tunnel.stop()


if __name__ == '__main__':
    unittest.main()
