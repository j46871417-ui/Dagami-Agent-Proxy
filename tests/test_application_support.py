import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from devproxy_pkg.adapters import get_adapter, list_adapters
from devproxy_pkg.core.launcher import Launcher
from devproxy_pkg.core.profile import ProxyProfile


class ApplicationSupportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.profile = ProxyProfile.parse("http://127.0.0.1:12345")

    def tearDown(self): self.temp.cleanup()

    def test_cli_and_desktop_are_distinct(self):
        for name in ("codex", "opencode", "gemini", "claude", "agy", "git"):
            self.assertTrue(get_adapter(name).is_cli, name)
        for name in ("codex-gui", "opencode-gui", "vscode", "cursor", "windsurf", "vscodium"):
            self.assertFalse(get_adapter(name).is_cli, name)
        ids = [a.app_id for a in list_adapters()]
        self.assertEqual(len(ids), len(set(ids)))

    def test_no_fake_proxy_configuration(self):
        with patch("devproxy_pkg.core.recovery.StateManager.record_applied_change", side_effect=AssertionError("No config writes expected")):
            for name in ("codex", "codex-gui", "opencode", "opencode-gui"):
                ok, reason = get_adapter(name).apply_persistent(self.profile)
                self.assertFalse(ok, name)
                self.assertTrue(reason)

    def test_git_process_configuration_preserves_prior_entries(self):
        with patch.dict(os.environ, {"GIT_CONFIG_COUNT": "3", "GIT_CONFIG_KEY_0": "existing"}):
            env = get_adapter("git").get_launch_environment("http://127.0.0.1:12345")
        self.assertEqual(env["GIT_CONFIG_COUNT"], "5")
        self.assertEqual(env["GIT_CONFIG_KEY_3"], "http.proxy")
        self.assertEqual(env["GIT_CONFIG_VALUE_3"], "http://127.0.0.1:12345")
        self.assertEqual(env["GIT_CONFIG_VALUE_4"], "true")
        self.assertNotIn("GIT_CONFIG_KEY_0", env)
        for raw in ("-1", "invalid", "65"):
            with patch.dict(os.environ, {"GIT_CONFIG_COUNT": raw}), self.assertRaises(ValueError):
                get_adapter("git").get_launch_environment("http://127.0.0.1:12345")

    def test_vscode_path_wrapper_resolves_gui_and_portable_settings(self):
        wrapper = self.root / "bin/code.cmd"
        wrapper.parent.mkdir()
        wrapper.touch()
        (self.root / "Code.exe").touch()
        (self.root / "data").mkdir()
        adapter = get_adapter("vscode")
        with patch("devproxy_pkg.adapters.vscode_family.sys.platform", "win32"), patch("devproxy_pkg.adapters.vscode_family.shutil.which", return_value=str(wrapper)):
            self.assertEqual(adapter.detect_executable(), str(self.root / "Code.exe"))
            self.assertEqual(adapter.detect_config_files(), [str(self.root / "data/user-data/User/settings.json")])

    def test_opencode_manual_electron_and_old_webview_generations(self):
        adapter = get_adapter("opencode-gui")
        exe = self.root / "OpenCode.exe"
        exe.touch()
        self.assertEqual(adapter.launch_flags(self.profile, str(exe)), [])
        (self.root / "resources").mkdir()
        (self.root / "resources/app.asar").touch()
        self.assertTrue(any("--proxy-server" in f for f in adapter.launch_flags(self.profile, str(exe))))
        with patch("devproxy_pkg.adapters.opencode_desktop.sys.platform", "win32"):
            env = adapter.get_launch_environment("http://127.0.0.1:12345", str(exe))
        self.assertIn("12345", env["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"])

    def test_environment_factory_failure_stops_new_tunnel(self):
        with patch("devproxy_pkg.core.launcher.LocalTunnel") as tunnel:
            tunnel.return_value.start.return_value = 12345
            with self.assertRaisesRegex(ValueError, "factory"):
                Launcher.launch_with_tunnel("unused", self.profile, enable_ipc=False, environment_factory=lambda *_: (_ for _ in ()).throw(ValueError("factory")))
            tunnel.return_value.stop.assert_called_once()

    def test_opencode_config_paths_and_xdg(self):
        with patch.dict(os.environ, {"XDG_CONFIG_HOME": str(self.root)}):
            paths = get_adapter("opencode").detect_config_files()
        self.assertEqual(paths, [str(self.root / "opencode/opencode.json"), str(self.root / "opencode/opencode.jsonc")])
