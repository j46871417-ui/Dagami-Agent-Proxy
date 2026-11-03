import importlib.util
from pathlib import Path
import socket
import tempfile
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('external_tun', Path(__file__).resolve().parents[1] / 'tools/external_tun.py')
tun = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tun)


class ExternalTunTests(unittest.TestCase):
    def test_environment_is_child_only(self):
        original = {'http_proxy': 'old', 'HTTPS_PROXY': 'old', 'AG_LS_PROXY': 'old',
                    'NODE_USE_ENV_PROXY': '1', 'PATH': 'keep', 'SSL_CERT_FILE': 'keep'}
        result = tun.clean_environment(original)
        self.assertEqual(result, {'PATH': 'keep', 'SSL_CERT_FILE': 'keep'})
        self.assertIn('http_proxy', original)

    def test_injected_node_option_is_not_silently_retained(self):
        with self.assertRaises(ValueError):
            tun.clean_environment({'NODE_OPTIONS': '--require proxy-hook.js'})

    def test_socks_probe_fragmentation_and_rejection(self):
        for reply, good in ((b'\x05\x00', True), (b'\x05\xff', False), (b'\x05', False)):
            with socket.socket() as server:
                server.bind(('127.0.0.1', 0)); server.listen(1); server.settimeout(3)
                received = []
                def serve():
                    with server.accept()[0] as client:
                        client.settimeout(3)
                        data = bytearray()
                        while len(data) < 3:
                            data.extend(client.recv(3 - len(data)))
                        received.append(bytes(data))
                        for value in reply:
                            client.sendall(bytes([value]))
                thread = threading.Thread(target=serve); thread.start()
                try:
                    if good:
                        self.assertFalse(tun.socks_probe(server.getsockname()[1])['tun_verified'])
                    else:
                        with self.assertRaises(ValueError):
                            tun.socks_probe(server.getsockname()[1])
                finally:
                    thread.join(4)
                self.assertFalse(thread.is_alive())
                self.assertEqual(received, [b'\x05\x01\x00'])

    def test_preflight_preserves_files_and_blocks_sidecar(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); exe = root / 'Antigravity.exe'; exe.write_bytes(b'fixture')
            (root / 'resources').mkdir(); (root / 'resources/app.asar').write_bytes(b'fixture')
            settings = root / 'settings.json'; settings.write_text('{/* jsonc */}')
            self.assertEqual(tun.validate_install(exe, settings), exe)
            (root / 'version.dll').write_bytes(b'fixture')
            with self.assertRaises(ValueError):
                tun.validate_install(exe, settings)
            self.assertEqual(exe.read_bytes(), b'fixture')
            self.assertEqual(settings.read_text(), '{/* jsonc */}')

    def test_existing_proxy_and_patch_records_refuse_launch(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); exe = root / 'Antigravity.exe'; exe.touch()
            (root / 'resources').mkdir(); (root / 'resources/app.asar').touch()
            settings = root / 'settings.json'; settings.write_text('{"http.proxy":"http://localhost:1",}')
            with self.assertRaises(ValueError): tun.validate_install(exe, settings)
            settings.write_text('{}')
            (root / 'resources/backend.devproxy-region.json').write_text('{}')
            with self.assertRaises(ValueError): tun.validate_install(exe, settings)

    def test_plan_never_starts_app_and_run_uses_clean_env(self):
        with patch.object(tun, 'socks_probe', return_value={'tun_verified': False}), \
             patch.object(tun, 'validate_install', return_value=Path('Antigravity.exe')), \
             patch.object(tun, 'running_at', return_value=[]), \
             patch.object(tun.subprocess, 'Popen') as start:
            args = ['--exe', 'Antigravity.exe', '--settings', 'settings.json']
            self.assertEqual(tun.main(['plan', *args]), 0); start.assert_not_called()
            start.return_value.pid = 123
            with patch.dict(tun.os.environ, {'HTTPS_PROXY': 'old'}, clear=True):
                self.assertEqual(tun.main(['run', *args]), 0)
            self.assertNotIn('HTTPS_PROXY', start.call_args.kwargs['env'])
            self.assertEqual(start.call_args.args[0], ['Antigravity.exe'])

    def test_running_app_refuses_reuse(self):
        with patch.object(tun, 'socks_probe', return_value={}), \
             patch.object(tun, 'validate_install', return_value=Path('Antigravity.exe')), \
             patch.object(tun, 'running_at', return_value=[7]), \
             patch.object(tun.subprocess, 'Popen') as start:
            self.assertEqual(tun.main(['run', '--exe', 'Antigravity.exe', '--settings', 'settings.json']), 2)
            start.assert_not_called()
