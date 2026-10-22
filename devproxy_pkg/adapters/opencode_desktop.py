"""Current Electron and older Windows WebView2 OpenCode Desktop launch policies."""
import os
import sys
from pathlib import Path
from .base import ApplicationAdapter


class OpenCodeDesktopAdapter(ApplicationAdapter):
    @property
    def app_id(self): return "opencode-gui"

    @property
    def display_name(self): return "OpenCode Desktop"

    @property
    def supported_os(self): return ["win32", "darwin", "linux"]

    def detect_executable(self):
        if sys.platform == "win32":
            local = os.environ.get("LOCALAPPDATA", "")
            candidates = [Path(local)/"Programs/OpenCode/OpenCode.exe", Path(local)/"OpenCode/OpenCode.exe", Path(os.environ.get("ProgramFiles", r"C:\Program Files"))/"OpenCode/OpenCode.exe"]
        elif sys.platform == "darwin":
            candidates = [Path("/Applications/OpenCode.app/Contents/MacOS/OpenCode"), Path.home()/"Applications/OpenCode.app/Contents/MacOS/OpenCode"]
        else:
            candidates = [Path("/opt/OpenCode/opencode"), Path("/opt/opencode/opencode-desktop")]
        return next((str(p) for p in candidates if p.is_file()), None)

    @staticmethod
    def is_electron(executable):
        root = Path(executable).parent
        return any(p.is_file() for p in (root/"resources/app.asar", root/"resources/app/package.json", root.parent/"Resources/app.asar"))

    def detect_config_files(self): return []

    def get_cli_launch_flags(self, profile):
        return self.launch_flags(profile, self.detect_executable())

    def launch_flags(self, profile, executable=None):
        exe = executable
        if exe and self.is_electron(exe):
            return ["--proxy-server={PROXY_URL}", "--proxy-bypass-list=localhost;127.0.0.1;[::1]", "--disable-quic"]
        return []

    def get_launch_environment(self, local_proxy, executable=""):
        env = super().get_launch_environment(local_proxy, executable)
        if sys.platform == "win32":
            env["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = os.environ.get("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "") + f" --proxy-server={local_proxy} --proxy-bypass-list=localhost;127.0.0.1;[::1] --disable-quic"
        return env

    def apply_persistent(self, profile): return False, "Use the managed Desktop launcher; no unsupported proxy JSON setting is written."

    def restore_persistent(self): return False, "No Desktop application configuration was changed."

    def get_limitations(self):
        return ["Close the old Desktop and background OpenCode server before launch: reused services do not receive the new environment.", "Electron is detected from installed resources; older Windows releases use WebView2. Account and provider access need a live check."]
