import os
from pathlib import Path
import socket
import tempfile
import threading
import unittest
from unittest.mock import patch

from devproxy_pkg.adapters.antigravity import AntigravityAdapter
from devproxy_pkg.adapters.cli_tools import CliToolAdapter
from devproxy_pkg.core.profile import ProxyProfile
from devproxy_pkg.core.tunnel import LocalTunnel


class Ide26Tests(unittest.TestCase):
    def test_ide_downloaded_cli_is_found_without_path_and_not_used_for_other_tools(self):
        with tempfile.TemporaryDirectory() as temp:
            backend = Path(temp) / '.gemini' / 'bin' / 'agy.exe'
            backend.parent.mkdir(parents=True)
            backend.write_bytes(b'fixture')
            with patch('sys.platform', 'win32'), patch('os.path.expanduser', return_value=temp), \
                 patch('shutil.which', return_value=None), patch('devproxy_pkg.adapters.cli_tools.is_cli_executable', return_value=True):
                self.assertEqual(str(backend), CliToolAdapter('agy', 'agy', 'agy').detect_executable())
                self.assertIsNone(CliToolAdapter('claude', 'Claude', 'claude').detect_executable())

    def test_windows_ide_filename_and_separate_settings(self):
        with tempfile.TemporaryDirectory() as temp:
            executable = os.path.join(temp, 'Programs', 'Antigravity IDE', 'Antigravity IDE.exe')
            with patch('sys.platform', 'win32'), patch.dict(os.environ, {'LOCALAPPDATA': temp, 'APPDATA': temp}), \
                 patch('os.path.exists', side_effect=lambda path: path == executable), patch('shutil.which', return_value=None):
                adapter = AntigravityAdapter()
                self.assertEqual(executable, adapter.detect_executable())
                self.assertEqual([os.path.join(temp, 'Antigravity IDE', 'User', 'settings.json')], adapter.detect_config_files())

    @staticmethod
    def receive_all(peer):
        result = bytearray()
        while True:
            data = peer.recv(8192)
            if not data:
                return bytes(result)
            result.extend(data)

    def test_local_rpc_streams_and_preserves_csrf_without_proxy_credentials(self):
        for origin in (False, True):
            with self.subTest(origin=origin), socket.socket() as remote_proxy, socket.socket() as backend:
                remote_proxy.bind(('127.0.0.1', 0)); remote_proxy.listen()
                backend.bind(('127.0.0.1', 0)); backend.listen(); backend.settimeout(5)
                seen, errors = [], []
                payload = b'5\r\nhello\r\n0\r\n\r\n'
                response = b'HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nfirst\r\n6\r\nsecond\r\n0\r\nx-rpc-status: ok\r\n\r\n'

                def serve():
                    try:
                        with backend.accept()[0] as peer:
                            peer.settimeout(5)
                            data = bytearray()
                            while b'\r\n\r\n' not in data:
                                data.extend(peer.recv(4096))
                            header, body = bytes(data).split(b'\r\n\r\n', 1)
                            while len(body) < len(payload):
                                body += peer.recv(4096)
                            seen.extend([header, body])
                            peer.sendall(response[:70]); peer.sendall(response[70:])
                    except Exception as error:
                        errors.append(error)

                worker = threading.Thread(target=serve, daemon=True); worker.start()
                profile = ProxyProfile(name='fixture', protocol='http', host='127.0.0.1', port=remote_proxy.getsockname()[1], username='upstream', password='fixture-secret')
                tunnel = LocalTunnel(profile)
                try:
                    port = tunnel.start()
                    with socket.create_connection(('127.0.0.1', port), timeout=5) as peer:
                        authority = f'localhost:{backend.getsockname()[1]}'
                        target = '/test.Service/Stream?a=1' if origin else f'http://{authority}/test.Service/Stream?a=1'
                        peer.sendall((f'POST {target} HTTP/1.1\r\nHost: {authority}\r\nx-codeium-csrf-token: fixture-csrf\r\nProxy-Authorization: Basic fixture-proxy\r\nTransfer-Encoding: chunked\r\n\r\n').encode() + payload)
                        self.assertEqual(response, self.receive_all(peer))
                    worker.join(5)
                    self.assertFalse(worker.is_alive())
                    self.assertFalse(errors)
                    self.assertTrue(seen[0].startswith(b'POST /test.Service/Stream?a=1 HTTP/1.1'))
                    self.assertIn(b'x-codeium-csrf-token: fixture-csrf', seen[0])
                    self.assertNotIn(b'fixture-proxy', seen[0])
                    self.assertNotIn(b'fixture-secret', seen[0])
                    self.assertEqual(payload, seen[1])
                    remote_proxy.settimeout(.1)
                    with self.assertRaises(socket.timeout):
                        remote_proxy.accept()
                finally:
                    tunnel.stop()

    def test_self_loop_rejected_and_unreachable_local_backend_fails_closed(self):
        with socket.socket() as unused:
            unused.bind(('127.0.0.1', 0))
            tunnel = LocalTunnel(ProxyProfile(name='fixture', protocol='http', host='127.0.0.1', port=unused.getsockname()[1]))
            try:
                port = tunnel.start()
                for destination in (port, unused.getsockname()[1]):
                    with socket.create_connection(('127.0.0.1', port), timeout=5) as peer:
                        peer.sendall(f'POST http://127.0.0.1:{destination}/rpc HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n'.encode())
                        self.assertIn(b' 502 ', self.receive_all(peer))
            finally:
                tunnel.stop()


if __name__ == '__main__':
    unittest.main()
