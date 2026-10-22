import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from devproxy_pkg.adapters import get_adapter
from devproxy_pkg.core.config_editor import ConfigEditor
from devproxy_pkg.core.recovery import StateManager
from devproxy_pkg.core.launcher import Launcher


class ReviewRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.env = patch.dict(os.environ, {"DEVPROXY_HOME": str(self.root / "state")})
        self.env.start()

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def record(self, app):
        path = self.root / (app + (".toml" if app == "codex" else ".json"))
        key = "proxy" if app in ("codex", "opencode") else "http.proxy"
        path.write_text('proxy = "http://managed.invalid:80"\n' if app == "codex" else '{"' + key + '": "http://managed.invalid:80"}', encoding="utf-8")
        StateManager.record_applied_change(app, str(path), {key: "http://managed.invalid:80"}, {key: "http://original.invalid:80"})
        return path

    def test_failed_writes_keep_recovery_record(self):
        for app in ("antigravity", "vscode", "opencode", "codex"):
            with self.subTest(app=app):
                path = self.record(app)
                before = path.read_bytes()
                with patch.object(ConfigEditor, "atomic_write", side_effect=PermissionError("fixture locked")):
                    ok, message = get_adapter(app).restore_persistent()
                self.assertFalse(ok, message)
                self.assertIsNotNone(StateManager.get_applied_record(app))
                self.assertEqual(before, path.read_bytes())

    def test_missing_files_keep_recovery_record(self):
        self.record("vscode").unlink()
        ok, _ = get_adapter("vscode").restore_persistent()
        self.assertFalse(ok)
        self.assertIsNotNone(StateManager.get_applied_record("vscode"))

    def test_corrupt_state_is_not_replaced_with_empty_state(self):
        path = self.root / "state/state.json"
        path.parent.mkdir()
        path.write_text("{broken", encoding="utf-8")
        with self.assertRaises((ValueError, RuntimeError)):
            StateManager.record_applied_change("vscode", "unused", {}, {})
        self.assertEqual(path.read_text(), "{broken")

    def test_partial_restore_retires_only_successful_files(self):
        first = self.record("vscode")
        second = self.root / "locked.json"
        second.write_bytes(first.read_bytes())
        StateManager.record_applied_change("vscode", str(second), {"http.proxy": "managed"}, {"http.proxy": "original"})
        write = ConfigEditor.atomic_write
        def partial(path, content):
            if path == str(second): raise PermissionError("locked")
            return write(path, content)
        with patch.object(ConfigEditor, "atomic_write", side_effect=partial):
            self.assertFalse(get_adapter("vscode").restore_persistent()[0])
        record = StateManager.get_applied_record("vscode")
        self.assertNotIn(str(first), record)
        self.assertIn(str(second), record)
        self.assertTrue(get_adapter("vscode").restore_persistent()[0])
        self.assertIsNone(StateManager.get_applied_record("vscode"))

    def test_new_managed_keys_record_their_original_values(self):
        path = str(self.record("vscode"))
        StateManager.record_applied_change("vscode", path, {"http.proxy": "second", "http.proxyStrictSSL": True}, {"http.proxy": "intermediate", "http.proxyStrictSSL": False})
        values = StateManager.get_applied_record("vscode")[path]["original_values"]
        self.assertEqual(values["http.proxy"], "http://original.invalid:80")
        self.assertFalse(values["http.proxyStrictSSL"])

    def test_legacy_codex_restore_missing_root_proxy_preserves_profile(self):
        path = self.record("codex")
        path.write_text('model = "fixture"\n[profiles.demo]\nproxy = "http://profile.invalid"\n')
        state = StateManager.load_state()
        state["applied"]["codex"][str(path)]["original_values"]["proxy"] = ""
        StateManager.save_state(state)
        self.assertTrue(get_adapter("codex").restore_persistent()[0])
        self.assertTrue(path.read_text().startswith('proxy = ""\n'))
        self.assertIn('proxy = "http://profile.invalid"', path.read_text())

    def test_windows_npm_wrapper_resolves_without_shell(self):
        wrapper = self.root / "gemini.cmd"
        entry = self.root / "node_modules/fixture/cli.js"
        entry.parent.mkdir(parents=True)
        entry.touch()
        (self.root / "node.exe").touch()
        wrapper.write_text('"%dp0%\\node_modules\\fixture\\cli.js" %*')
        args = ["space value", "", "&|%USERPROFILE%", 'literal"quote']
        with patch("devproxy_pkg.core.launcher.sys.platform", "win32"):
            command = Launcher.command(str(wrapper), args)
        self.assertEqual(command, [str(self.root / "node.exe"), str(entry.resolve()), *args])

    def test_windows_unknown_wrapper_and_entry_escape_are_rejected(self):
        inside = self.root / "inside"
        inside.mkdir()
        wrapper = inside / "tool.cmd"
        wrapper.write_text("@echo arbitrary commands")
        with patch("devproxy_pkg.core.launcher.sys.platform", "win32"), self.assertRaises(ValueError):
            Launcher.command(str(wrapper))
        (self.root / "escape.js").touch()
        wrapper.write_text('"%dp0%\\..\\escape.js" %*')
        with patch("devproxy_pkg.core.launcher.sys.platform", "win32"), self.assertRaises(FileNotFoundError):
            Launcher.command(str(wrapper))

    def test_extensionless_npm_entrypoint_skips_node_executable(self):
        wrapper = self.root / "tool.cmd"
        entry = self.root / "entry"
        entry.touch()
        (self.root / "node.exe").touch()
        wrapper.write_text('"%dp0%\\node.exe" "%dp0%\\entry" %*')
        with patch("devproxy_pkg.core.launcher.sys.platform", "win32"):
            command = Launcher.command(str(wrapper), ["value"])
        self.assertEqual(command, [str(self.root / "node.exe"), str(entry.resolve()), "value"])

    def test_windows_job_creation_failure_stops_tunnel_before_launch(self):
        with patch("devproxy_pkg.core.launcher.sys.platform", "win32"), patch("devproxy_pkg.core.launcher.LocalTunnel") as tunnel, patch("devproxy_pkg.core.launcher.WindowsJobManager", side_effect=OSError("fixture job error")), patch.object(Launcher, "launch") as launch:
            tunnel.return_value.start.return_value = 12345
            with self.assertRaises(OSError):
                from devproxy_pkg.core.profile import ProxyProfile
                Launcher.launch_with_tunnel("unused", ProxyProfile.parse("http://127.0.0.1:9"), enable_ipc=False)
            tunnel.return_value.stop.assert_called_once()
            launch.assert_not_called()

    def test_codex_fallback_prefers_latest_cli_file(self):
        import struct
        binary = bytearray(256)
        binary[:2] = b"MZ"
        struct.pack_into("<I", binary, 0x3c, 128)
        binary[128:132] = b"PE\x00\x00"
        struct.pack_into("<H", binary, 128 + 24 + 68, 3)
        files = []
        for version in ("older", "newer"):
            file = self.root / "OpenAI/Codex/bin" / version / "codex.exe"
            file.parent.mkdir(parents=True)
            file.write_bytes(binary)
            files.append(file)
        os.utime(files[0], (100, 100))
        os.utime(files[1], (200, 200))
        with patch("devproxy_pkg.adapters.codex.sys.platform", "win32"), patch("devproxy_pkg.adapters.codex.shutil.which", return_value=None), patch.dict(os.environ, {"LOCALAPPDATA": str(self.root)}):
            self.assertEqual(get_adapter("codex").detect_executable(), str(files[1]))
