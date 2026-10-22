"""
ApplicationAdapter: Base abstract class for IDE and tool integrations.
Each adapter provides:
- app_id: Unique string identifier
- display_name: Human-friendly name
- detect(): Finds installed executables and config paths
- get_cli_launch_flags(): Recommended process arguments (e.g. --proxy-server=...)
- apply_persistent(): Safely applies proxy to config files
- restore_persistent(): Rolls back changes made specifically by devproxy
- limitations(): Known restrictions (e.g. extension host quirks, UDP/QUIC)
"""

from abc import ABC, abstractmethod
import sys
import struct
from typing import Dict, Any, List, Optional, Tuple
from ..core.profile import ProxyProfile


def is_cli_executable(path):
    if sys.platform != "win32":
        return True
    if str(path).lower().endswith((".cmd", ".bat", ".ps1")):
        return True
    try:
        with open(path, "rb") as stream:
            if stream.read(2) != b"MZ": return False
            stream.seek(0x3c)
            offset = struct.unpack("<I", stream.read(4))[0]
            stream.seek(offset)
            if stream.read(4) != b"PE\x00\x00": return False
            stream.seek(offset + 24 + 68)
            return struct.unpack("<H", stream.read(2))[0] == 3
    except (OSError, ValueError, struct.error):
        return False


class ApplicationAdapter(ABC):
    is_cli = False

    def get_launch_environment(self, local_proxy: str, executable: str = "") -> Dict[str, str]:
        return {"NODE_USE_ENV_PROXY": "1"}

    def launch_flags(self, profile, executable=None):
        return self.get_cli_launch_flags(profile)

    @property
    @abstractmethod
    def app_id(self) -> str:
        pass

    @property
    @abstractmethod
    def display_name(self) -> str:
        pass

    @property
    @abstractmethod
    def supported_os(self) -> List[str]:
        """List of 'win32', 'darwin', 'linux'"""
        pass

    @abstractmethod
    def detect_executable(self) -> Optional[str]:
        """Returns path to binary if installed, else None."""
        pass

    @abstractmethod
    def detect_config_files(self) -> List[str]:
        """Returns list of existing or expected config file paths."""
        pass

    @abstractmethod
    def get_cli_launch_flags(self, profile: ProxyProfile) -> List[str]:
        """Returns CLI flags for process launching, e.g. ['--proxy-server={PROXY_URL}']"""
        pass

    @abstractmethod
    def apply_persistent(self, profile: ProxyProfile) -> Tuple[bool, str]:
        """Applies persistent configuration with rollback recording."""
        pass

    @abstractmethod
    def restore_persistent(self) -> Tuple[bool, str]:
        """Rolls back changes made by devproxy."""
        pass

    @abstractmethod
    def get_limitations(self) -> List[str]:
        """Returns verified limitations and requirements."""
        pass
