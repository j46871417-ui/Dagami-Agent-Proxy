"""CLI tools use process environment, without inventing application config keys."""
import os
import shutil
import sys
from .base import ApplicationAdapter, is_cli_executable


class CliToolAdapter(ApplicationAdapter):
    is_cli = True

    def __init__(self, app_id, name, command):
        self._id, self._name, self._command = app_id, name, command

    @property
    def app_id(self): return self._id

    @property
    def display_name(self): return self._name

    @property
    def supported_os(self): return ["win32", "darwin", "linux"]

    def detect_executable(self):
        found = shutil.which(self._command)
        if found and is_cli_executable(found): return found
        home = os.path.expanduser("~")
        folders = [os.path.join(home, ".local", "bin"), os.path.join(home, ".agy", "bin"), os.path.join(home, ".claude", "local")]
        if self._command == "agy":
            folders.append(os.path.join(home, ".gemini", "bin"))
        for folder in folders:
            candidate = os.path.join(folder, self._command + (".exe" if sys.platform == "win32" else ""))
            if os.path.isfile(candidate) and is_cli_executable(candidate): return candidate
        return None

    def detect_config_files(self): return []

    def get_cli_launch_flags(self, profile): return []

    def apply_persistent(self, profile):
        return False, "Use the managed launcher; application/account configuration is not changed."

    def restore_persistent(self): return False, "No persistent application configuration was changed."

    def get_launch_environment(self, local_proxy, executable=""):
        env = super().get_launch_environment(local_proxy, executable)
        if self.app_id == "git":
            raw = os.environ.get("GIT_CONFIG_COUNT", "0")
            if not raw.isdigit() or not 0 <= int(raw) <= 64: raise ValueError("Invalid GIT_CONFIG_COUNT")
            count = int(raw)
            for key, value in (("http.proxy", local_proxy), ("http.sslVerify", "true")):
                env[f"GIT_CONFIG_KEY_{count}"] = key
                env[f"GIT_CONFIG_VALUE_{count}"] = value
                count += 1
            env["GIT_CONFIG_COUNT"] = str(count)
        return env

    def get_limitations(self):
        return ["HTTP(S) proxy environment applies to the child process and compatible descendants; isolated sandboxes and SSH need separate settings."]
