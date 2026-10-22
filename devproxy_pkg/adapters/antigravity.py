"""
Google Antigravity Application Adapter.
Supports:
- GUI (Electron / Chromium shell)
- language_server.exe (Go / gRPC / Protobuf backend)
- web_bundle_ru / web UI
- Streaming gRPC / HTTP/2 responses
- Chromium flags: --proxy-server=...
"""

import os
import sys
import shutil
from typing import List, Optional, Tuple
from .base import ApplicationAdapter
from ..core.profile import ProxyProfile
from ..core.config_editor import ConfigEditor
from ..core.recovery import StateManager


class AntigravityAdapter(ApplicationAdapter):
    @property
    def app_id(self) -> str:
        return "antigravity"

    @property
    def display_name(self) -> str:
        return "Google Antigravity"

    @property
    def supported_os(self) -> List[str]:
        return ["win32", "darwin", "linux"]

    def detect_executable(self) -> Optional[str]:
        candidates = []
        if sys.platform == "win32":
            local_appdata = os.environ.get("LOCALAPPDATA", "")
            program_files = os.environ.get("ProgramFiles", "C:\\Program Files")
            candidates = [
                os.path.join(local_appdata, "Programs", "antigravity", "Antigravity.exe"),
                os.path.join(local_appdata, "antigravity", "Antigravity.exe"),
                os.path.join(program_files, "Antigravity", "Antigravity.exe"),
            ]
        elif sys.platform == "darwin":
            candidates = [
                "/Applications/Antigravity.app/Contents/MacOS/Antigravity",
                os.path.expanduser("~/Applications/Antigravity.app/Contents/MacOS/Antigravity")
            ]
        else:
            candidates = [
                "/usr/bin/antigravity",
                "/usr/local/bin/antigravity",
                os.path.expanduser("~/.local/bin/antigravity")
            ]

        for p in candidates:
            if os.path.exists(p):
                return p

        # Check PATH
        which_path = shutil.which("antigravity") or shutil.which("Antigravity")
        return which_path

    def detect_config_files(self) -> List[str]:
        if sys.platform == "win32":
            appdata = os.environ.get("APPDATA", "")
            return [os.path.join(appdata, "Antigravity", "User", "settings.json")]
        elif sys.platform == "darwin":
            home = os.path.expanduser("~")
            return [os.path.join(home, "Library", "Application Support", "Antigravity", "User", "settings.json")]
        else:
            home = os.path.expanduser("~")
            return [os.path.join(home, ".config", "Antigravity", "User", "settings.json")]

    def get_cli_launch_flags(self, profile: ProxyProfile) -> List[str]:
        # Electron / Chromium network flags
        # Important: pass --proxy-server with the scheme and host:port
        # If proxy requires auth or is SOCKS5, LocalTunnel will bridge it to http://127.0.0.1:port
        return [
            "--proxy-server={PROXY_URL}",
            "--disable-quic",
            "--proxy-bypass-list=localhost;127.0.0.1;[::1]"
        ]

    def apply_persistent(self, profile: ProxyProfile) -> Tuple[bool, str]:
        if profile.has_auth:
            return False, "Authenticated proxies are supported only in isolated session mode; credentials are kept in the local tunnel."
        configs = self.detect_config_files()
        if not configs:
            return False, "No configuration paths determined for Antigravity"
        
        target_path = configs[0]
        parsed, raw = ConfigEditor.load_jsonc(target_path)
        if parsed is None:
            return False, f"Failed to parse JSONC in {target_path} (file may contain syntax errors). Refusing to overwrite."

        proxy_url = profile.to_url()
        # Track original values
        orig_proxy = parsed.get("http.proxy")
        orig_support = parsed.get("http.proxySupport")
        
        updates = {
            "http.proxy": proxy_url,
            "http.proxySupport": "on"
            # NOTE: http.proxyStrictSSL is NOT set to false (keeps certificates secure)
        }

        StateManager.record_applied_change(
            self.app_id,
            target_path,
            updates,
            {"http.proxy": orig_proxy, "http.proxySupport": orig_support}
        )

        new_content = ConfigEditor.update_json_fields_preserving(raw, updates)
        try:
            ConfigEditor.atomic_write(target_path, new_content)
            return True, f"Updated {target_path} (http.proxy set to {proxy_url})"
        except Exception as e:
            return False, f"Write error: {e}"

    def restore_persistent(self) -> Tuple[bool, str]:
        return StateManager.restore_json(self.app_id)

    def get_limitations(self) -> List[str]:
        return [
            "Chromium UI and language_server.exe run as separate processes.",
            "Session mode (Launcher with LocalTunnel) guarantees both UI and language_server inherit proxy.",
            "OAuth authentication (Google sign-in) in browser popup follows system network or loopback.",
            "gRPC streaming for model interactions requires full HTTP/2 or SOCKS5 TCP pass-through."
        ]
