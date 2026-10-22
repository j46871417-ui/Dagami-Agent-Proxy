"""
Codex Application Adapters:
1. CodexCliAdapter: CLI binary (`codex.exe` or `codex`), `.codex/config.toml`, sandbox processes.
2. CodexGuiAdapter: Codex Desktop GUI application (packaged MSIX/WindowsApps or Electron desktop binary).
"""

import os
import sys
import shutil
import re
from typing import List, Optional, Tuple
from .base import ApplicationAdapter, is_cli_executable
from ..core.profile import ProxyProfile
from ..core.config_editor import ConfigEditor
from ..core.recovery import StateManager


class CodexCliAdapter(ApplicationAdapter):
    is_cli = True
    @property
    def app_id(self) -> str:
        return "codex"

    @property
    def display_name(self) -> str:
        return "Codex CLI"

    @property
    def supported_os(self) -> List[str]:
        return ["win32", "darwin", "linux"]

    def detect_executable(self) -> Optional[str]:
        # 1. Check PATH
        p = shutil.which("codex") or shutil.which("codex.exe")
        if p and is_cli_executable(p):
            return p

        # 2. Check OpenAI Codex standard installed path
        if sys.platform == "win32":
            local = os.environ.get("LOCALAPPDATA", "")
            base = os.path.join(local, "OpenAI", "Codex", "bin")
            if os.path.exists(base):
                try:
                    files = [os.path.join(base, entry, "codex.exe") for entry in os.listdir(base)]
                    files = [path for path in files if os.path.isfile(path)]
                    for path in sorted(files, key=os.path.getmtime, reverse=True):
                        if is_cli_executable(path): return path
                except OSError:
                    pass

        home = os.path.expanduser("~")
        candidates = [
            os.path.join(home, ".codex", "bin", "codex"),
            os.path.join(home, ".local", "bin", "codex"),
            "/usr/local/bin/codex"
        ]
        for c in candidates:
            if os.path.isfile(c) and is_cli_executable(c):
                return c
        return None

    def detect_config_files(self) -> List[str]:
        home = os.path.expanduser("~")
        return [
            os.path.join(home, ".codex", "config.toml")
        ]

    def get_cli_launch_flags(self, profile: ProxyProfile) -> List[str]:
        return []

    def apply_persistent(self, profile: ProxyProfile) -> Tuple[bool, str]:
        return False, "Codex has no verified global upstream proxy key in config.toml. Use a managed session; sandbox settings are not changed."

    def restore_persistent(self) -> Tuple[bool, str]:
        record = StateManager.get_applied_record(self.app_id)
        if not record:
            return False, "No persistent changes recorded for Codex CLI"

        messages, errors = [], []
        for file_path, data in record.items():
            try:
                if not os.path.isfile(file_path):
                    raise FileNotFoundError("Configuration is missing; recovery record retained")
                with open(file_path, "r", encoding="utf-8") as stream:
                    content = stream.read()
                orig_proxy = data.get("original_values", {}).get("proxy")
                table = re.search(r'^\s*\[', content, re.MULTILINE)
                split = table.start() if table else len(content)
                root, sections = content[:split], content[split:]
                if orig_proxy is not None:
                    import json
                    line = 'proxy = ' + json.dumps(orig_proxy)
                    if re.search(r'^\s*proxy\s*=', root, re.MULTILINE):
                        root = re.sub(r'^\s*proxy\s*=\s*"[^\"]*"', lambda _: line, root, flags=re.MULTILINE)
                    else:
                        root = line + '\n' + root
                else:
                    root = re.sub(r'^\s*proxy\s*=\s*"[^\"]*"\n?', '', root, flags=re.MULTILINE)
                content = root + sections
                ConfigEditor.atomic_write(file_path, content)
                StateManager.clear_applied_file(self.app_id, file_path)
                messages.append("Restored " + file_path)
            except Exception as error:
                errors.append(file_path + ": " + str(error))
        return not errors, "; ".join(messages + errors)

    def get_limitations(self) -> List[str]:
        return [
            "Codex CLI spawns sandbox subprocesses (cmd, node_repl, powershell).",
            "Proxy environment is passed to the local process; sandbox, WSL and remote execution may need separate network configuration.",
            "The tunnel preserves streaming bytes; upstream availability and account access require a live check."
        ]


class CodexGuiAdapter(ApplicationAdapter):
    """
    Adapter for Codex Desktop GUI (packaged app / desktop window).
    """
    @property
    def app_id(self) -> str:
        return "codex-gui"

    @property
    def display_name(self) -> str:
        return "Codex Desktop (GUI)"

    @property
    def supported_os(self) -> List[str]:
        return ["win32", "darwin"]

    def detect_executable(self) -> Optional[str]:
        # On Windows, Codex Desktop can be launched via standard URI protocol or WindowsApps
        if sys.platform == "win32":
            # Check Start menu shortcut or WindowsApps package
            winapps = "C:\\Program Files\\WindowsApps"
            if os.path.exists(winapps):
                try:
                    for entry in os.listdir(winapps):
                        if "OpenAI.Codex" in entry:
                            exe = os.path.join(winapps, entry, "app", "Codex.exe")
                            if os.path.exists(exe):
                                return exe
                except Exception:
                    pass

            local = os.environ.get("LOCALAPPDATA", "")
            candidates = [
                os.path.join(local, "Programs", "Codex", "Codex.exe"),
                os.path.join(local, "Programs", "OpenAI Codex", "Codex.exe")
            ]
            for c in candidates:
                if os.path.exists(c):
                    return c

        elif sys.platform == "darwin":
            candidates = [
                "/Applications/Codex.app/Contents/MacOS/Codex",
                os.path.expanduser("~/Applications/Codex.app/Contents/MacOS/Codex")
            ]
            for c in candidates:
                if os.path.exists(c):
                    return c

        return None

    def detect_config_files(self) -> List[str]:
        home = os.path.expanduser("~")
        return [
            os.path.join(home, ".codex", "config.toml")
        ]

    def get_cli_launch_flags(self, profile: ProxyProfile) -> List[str]:
        return ["--proxy-server={PROXY_URL}"]

    def apply_persistent(self, profile: ProxyProfile) -> Tuple[bool, str]:
        # Shares the underlying config.toml with Codex CLI
        cli = CodexCliAdapter()
        return cli.apply_persistent(profile)

    def restore_persistent(self) -> Tuple[bool, str]:
        cli = CodexCliAdapter()
        return cli.restore_persistent()

    def get_limitations(self) -> List[str]:
        return [
            "Codex Desktop GUI coordinates with background stdio core worker (codex.exe).",
            "Launch configures the GUI and new local backend processes; reused workers and sandboxes require a separate check.",
            "Packaged Windows Store MSIX versions may require launching via protocol handler if executable permissions are locked by OS."
        ]
