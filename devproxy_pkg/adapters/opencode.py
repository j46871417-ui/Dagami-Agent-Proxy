"""
OpenCode Application Adapter.
Supports:
- CLI mode (`opencode`)
- Web/Desktop GUI if launched
- Node.js network runtime environment (fetch, undici, node-fetch)
"""

import os
import sys
import shutil
from typing import List, Optional, Tuple
from .base import ApplicationAdapter, is_cli_executable
from ..core.profile import ProxyProfile
from ..core.config_editor import ConfigEditor
from ..core.recovery import StateManager


class OpenCodeAdapter(ApplicationAdapter):
    is_cli = True
    @property
    def app_id(self) -> str:
        return "opencode"

    @property
    def display_name(self) -> str:
        return "OpenCode"

    @property
    def supported_os(self) -> List[str]:
        return ["win32", "darwin", "linux"]

    def detect_executable(self) -> Optional[str]:
        p = shutil.which("opencode") or shutil.which("OpenCode")
        if p and is_cli_executable(p):
            return p

        home = os.path.expanduser("~")
        candidates = [
            os.path.join(home, ".opencode", "bin", "opencode"),
            os.path.join(home, ".opencode", "bin", "opencode.exe"),
            os.path.join(home, "AppData", "Roaming", "npm", "opencode.cmd")
        ]
        for c in candidates:
            if os.path.isfile(c) and is_cli_executable(c):
                return c
        return None

    def detect_config_files(self) -> List[str]:
        home = os.path.expanduser("~")
        return [
            os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.join(home, ".config")), "opencode", "opencode.json"),
            os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.join(home, ".config")), "opencode", "opencode.jsonc")
        ]

    def get_cli_launch_flags(self, profile: ProxyProfile) -> List[str]:
        return []

    def apply_persistent(self, profile: ProxyProfile) -> Tuple[bool, str]:
        return False, "OpenCode uses HTTP_PROXY/HTTPS_PROXY/NO_PROXY in a managed session; no unsupported JSON proxy key is written."

    def restore_persistent(self) -> Tuple[bool, str]:
        return StateManager.restore_json(self.app_id)

    def get_limitations(self) -> List[str]:
        return [
            "OpenCode CLI receives HTTP_PROXY / HTTPS_PROXY environment variables; runtime behavior depends on its installed version.",
            "Child tool executions inherit session environment variables.",
            "Streaming responses use HTTP chunked transfer / SSE."
        ]
