"""
Generic VS Code Family Adapter for:
- Visual Studio Code
- Cursor
- Windsurf
- VSCodium
"""

import os
import sys
import shutil
from typing import List, Optional, Tuple
from .base import ApplicationAdapter
from ..core.profile import ProxyProfile
from ..core.config_editor import ConfigEditor
from ..core.recovery import StateManager


class VSCodeFamilyAdapter(ApplicationAdapter):
    def __init__(self, app_id: str, display_name: str, folder_name: str, bin_names: List[str]):
        self._app_id = app_id
        self._display_name = display_name
        self._folder_name = folder_name
        self._bin_names = bin_names

    @property
    def app_id(self) -> str:
        return self._app_id

    @property
    def display_name(self) -> str:
        return self._display_name

    @property
    def supported_os(self) -> List[str]:
        return ["win32", "darwin", "linux"]

    def detect_executable(self) -> Optional[str]:
        # Check PATH first
        for b in self._bin_names:
            p = shutil.which(b)
            if p:
                if sys.platform == "win32" and p.lower().endswith((".cmd", ".bat", ".ps1")):
                    # PATH usually exposes bin/code.cmd; GUI needs the actual EXE.
                    exe = "Code.exe" if self.app_id == "vscode" else self._folder_name + ".exe"
                    for folder in (os.path.dirname(p), os.path.dirname(os.path.dirname(p))):
                        candidate = os.path.join(folder, exe)
                        if os.path.isfile(candidate): return candidate
                    continue
                return p

        # Check standard installation folders
        candidates = []
        if sys.platform == "win32":
            local = os.environ.get("LOCALAPPDATA", "")
            prog = os.environ.get("ProgramFiles", "C:\\Program Files")
            for b in self._bin_names:
                exe = f"{b}.exe" if not b.endswith(".exe") else b
                candidates.extend([
                    os.path.join(local, "Programs", "Microsoft VS Code" if self.app_id == "vscode" else self._folder_name, exe),
                    os.path.join(prog, "Microsoft VS Code" if self.app_id == "vscode" else self._folder_name, exe),
                    os.path.join(local, "Programs", self._folder_name, exe),
                    os.path.join(local, "Programs", self._display_name, exe),
                    os.path.join(prog, self._folder_name, exe),
                    os.path.join(prog, self._display_name, exe),
                ])
        elif sys.platform == "darwin":
            app_name = f"{self._display_name}.app"
            candidates.extend([
                f"/Applications/{app_name}/Contents/Resources/app/bin/{self._bin_names[0]}",
                f"/Applications/{app_name}/Contents/MacOS/Electron",
                os.path.expanduser(f"~/Applications/{app_name}/Contents/MacOS/Electron")
            ])
        else:
            for b in self._bin_names:
                candidates.extend([
                    f"/usr/bin/{b}",
                    f"/usr/local/bin/{b}",
                    os.path.expanduser(f"~/.local/bin/{b}")
                ])

        for c in candidates:
            if os.path.exists(c):
                return c
        return None

    def detect_config_files(self) -> List[str]:
        executable = self.detect_executable()
        if executable:
            portable = os.path.join(os.path.dirname(executable), "data")
            if os.path.isdir(portable):
                return [os.path.join(portable, "user-data", "User", "settings.json")]
        if sys.platform == "win32":
            appdata = os.environ.get("APPDATA", "")
            return [os.path.join(appdata, self._folder_name, "User", "settings.json")]
        elif sys.platform == "darwin":
            home = os.path.expanduser("~")
            return [os.path.join(home, "Library", "Application Support", self._folder_name, "User", "settings.json")]
        else:
            home = os.path.expanduser("~")
            return [
                os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.join(home, ".config")), self._folder_name, "User", "settings.json"),
                # Flatpak path
                os.path.expanduser(f"~/.var/app/{'com.vscodium.codium' if self.app_id == 'vscodium' else 'com.visualstudio.code'}/config/{self._folder_name}/User/settings.json")
            ]

    def get_cli_launch_flags(self, profile: ProxyProfile) -> List[str]:
        return ["--proxy-server={PROXY_URL}", "--proxy-bypass-list=localhost;127.0.0.1;[::1]", "--disable-quic"]

    def apply_persistent(self, profile: ProxyProfile) -> Tuple[bool, str]:
        if profile.has_auth:
            return False, "Authenticated proxies are supported only in isolated session mode; credentials are kept in the local tunnel."
        configs = self.detect_config_files()
        target_path = configs[0]
        parsed, raw = ConfigEditor.load_jsonc(target_path)
        if parsed is None:
            return False, f"Failed to parse JSONC in {target_path}. File may contain syntax errors."

        proxy_url = profile.to_url()
        orig_proxy = parsed.get("http.proxy")
        orig_support = parsed.get("http.proxySupport")

        updates = {
            "http.proxy": proxy_url,
            "http.proxySupport": "override"
            # Strict SSL remains unmodified (never disabled)
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
            return True, f"Configured {target_path}"
        except Exception as e:
            return False, str(e)

    def restore_persistent(self) -> Tuple[bool, str]:
        return StateManager.restore_json(self.app_id)

    def get_limitations(self) -> List[str]:
        return [
            f"{self.display_name} extensionHost runs out-of-process and requires env vars HTTP_PROXY/HTTPS_PROXY.",
            "Integrated terminal sessions inherit shell environment variables.",
            "Some 3rd-party extensions ignore 'http.proxy' and only respect process env vars."
        ]


# Specific subclasses
class VSCodeAdapter(VSCodeFamilyAdapter):
    def __init__(self):
        super().__init__("vscode", "Visual Studio Code", "Code", ["code", "Code"])


class CursorAdapter(VSCodeFamilyAdapter):
    def __init__(self):
        super().__init__("cursor", "Cursor", "Cursor", ["cursor", "Cursor"])

    def get_limitations(self) -> List[str]:
        lim = super().get_limitations()
        lim.append("Check AI HTTP/2/SSE streaming against the selected upstream; individual extensions may use separate network settings.")
        return lim


class WindsurfAdapter(VSCodeFamilyAdapter):
    def __init__(self):
        super().__init__("windsurf", "Windsurf", "Windsurf", ["windsurf", "Windsurf"])

    def get_limitations(self) -> List[str]:
        lim = super().get_limitations()
        lim.append("A newly spawned language server can inherit proxy environment; an already running daemon needs a separate restart/check.")
        return lim


class VSCodiumAdapter(VSCodeFamilyAdapter):
    def __init__(self):
        super().__init__("vscodium", "VSCodium", "VSCodium", ["codium", "VSCodium"])
