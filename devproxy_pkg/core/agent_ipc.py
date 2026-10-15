"""
core/agent_ipc.py
Ephemeral per-session background IPC server with Win32 SDDL ACL key security.
Zero-Dependencies implementation using ctypes and standard library.
"""

import os
import sys
import json
import socket
import secrets
import threading
import tempfile
import shutil
import hmac
from concurrent.futures import ThreadPoolExecutor
from typing import Dict, Any, Callable, Optional, Tuple

if sys.platform == "win32":
    import ctypes
    from ctypes import wintypes

    SDDL_REVISION_1 = 1

    class SECURITY_ATTRIBUTES(ctypes.Structure):
        _fields_ = [
            ("nLength", wintypes.DWORD),
            ("lpSecurityDescriptor", wintypes.LPVOID),
            ("bInheritHandle", wintypes.BOOL),
        ]


class AgentKeySecurity:
    """Manages creation and strict OS-level ACL enforcement on agent.key."""

    @staticmethod
    def write_restricted_key(file_path: str, secret_key: str) -> None:
        """
        Writes secret_key to file_path and guarantees that only the current user/SYSTEM
        has read and write access.
        """
        if sys.platform == "win32":
            # On Windows: write file first, then apply SDDL or write directly
            with open(file_path, "w", encoding="utf-8") as f:
                f.write(secret_key)

            # Apply strict SDDL: Owner Full Control (OW) + SYSTEM Full Control (SY)
            # D:P(A;;GA;;;SY)(A;;GA;;;OW)
            try:
                advapi32 = ctypes.windll.advapi32
                sddl = "D:P(A;;GA;;;SY)(A;;GA;;;OW)"
                p_sd = wintypes.LPVOID()
                size = wintypes.ULONG()

                advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
                    wintypes.LPCWSTR,
                    wintypes.DWORD,
                    ctypes.POINTER(wintypes.LPVOID),
                    ctypes.POINTER(wintypes.ULONG),
                ]
                advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL

                if advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl, SDDL_REVISION_1, ctypes.byref(p_sd), ctypes.byref(size)
                ):
                    # SetFileSecurityW(lpFileName, SecurityInformation, pSecurityDescriptor)
                    DACL_SECURITY_INFORMATION = 0x00000004
                    advapi32.SetFileSecurityW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.LPVOID]
                    advapi32.SetFileSecurityW.restype = wintypes.BOOL
                    secured = advapi32.SetFileSecurityW(file_path, DACL_SECURITY_INFORMATION, p_sd)
                    # Free security descriptor memory via LocalFree
                    ctypes.windll.kernel32.LocalFree(p_sd)
                    if not secured:
                        raise ctypes.WinError()
                else:
                    raise ctypes.WinError()
            except Exception:
                try:
                    os.remove(file_path)
                except OSError:
                    pass
                raise
        else:
            # POSIX: strict 0600 permissions
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
            if hasattr(os, "O_NOFOLLOW"):
                flags |= os.O_NOFOLLOW
            fd = os.open(file_path, flags, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(secret_key)


class EphemeralAgentIPC:
    """
    Session-bound lightweight IPC control plane.
    Runs exclusively as a daemon thread during a 'devproxy run' session.
    """

    def __init__(self, key_dir: Optional[str] = None, max_workers: int = 4):
        self._owns_key_dir = key_dir is None
        self.key_dir = key_dir or tempfile.mkdtemp(prefix="devproxy-ipc-")
        self.key_file = os.path.join(self.key_dir, "agent.key")
        self.secret_token = secrets.token_hex(32)

        self.server_sock: Optional[socket.socket] = None
        self.port: int = 0
        self.is_running = False

        self._thread: Optional[threading.Thread] = None
        self._executor = ThreadPoolExecutor(max_workers=max_workers, thread_name_prefix="AgentIPCWorker")
        self._work_slots = threading.BoundedSemaphore(max_workers * 2)
        self._handlers: Dict[str, Callable[[Dict[str, Any]], Dict[str, Any]]] = {}

        self._register_default_handlers()

    def _register_default_handlers(self) -> None:
        self.register_handler("ping", lambda args: {"pong": True, "ts": os.path.getmtime(self.key_file) if os.path.exists(self.key_file) else 0})
        self.register_handler("status", lambda args: {"running": self.is_running, "port": self.port})

    def register_handler(self, command: str, handler: Callable[[Dict[str, Any]], Dict[str, Any]]) -> None:
        self._handlers[command] = handler

    def start(self) -> int:
        """Starts loopback IPC server and writes protected key file."""
        # 1. Write protected key file
        AgentKeySecurity.write_restricted_key(self.key_file, self.secret_token)

        # 2. Bind loopback ephemeral port
        self.server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server_sock.bind(("127.0.0.1", 0))
        self.server_sock.listen(16)
        self.port = self.server_sock.getsockname()[1]
        self.is_running = True

        self._thread = threading.Thread(target=self._serve_loop, daemon=True, name="DevProxyAgentIPC")
        self._thread.start()
        return self.port

    def stop(self) -> None:
        """Stops server, shuts down worker pool, and removes key file."""
        self.is_running = False
        if self.server_sock:
            try:
                self.server_sock.close()
            except Exception:
                pass

        self._executor.shutdown(wait=True, cancel_futures=True)

        if os.path.exists(self.key_file):
            try:
                os.remove(self.key_file)
            except Exception:
                pass
        if self._owns_key_dir:
            shutil.rmtree(self.key_dir, ignore_errors=True)

    def _serve_loop(self) -> None:
        while self.is_running:
            try:
                client_sock, _ = self.server_sock.accept()
                if not self._work_slots.acquire(blocking=False):
                    client_sock.close()
                    continue
                self._executor.submit(self._handle_client_with_slot, client_sock)
            except Exception:
                if not self.is_running:
                    break

    def _handle_client_with_slot(self, client_sock: socket.socket) -> None:
        try:
            self._handle_client(client_sock)
        finally:
            self._work_slots.release()

    def _handle_client(self, client_sock: socket.socket) -> None:
        try:
            client_sock.settimeout(5.0)
            raw = bytearray()
            while b"\n" not in raw and len(raw) < 65536:
                chunk = client_sock.recv(min(4096, 65536 - len(raw)))
                if not chunk:
                    break
                raw.extend(chunk)
            if not raw:
                client_sock.close()
                return

            req = json.loads(bytes(raw).split(b"\n", 1)[0].decode("utf-8"))
            auth = req.get("auth")
            if not isinstance(auth, str) or not hmac.compare_digest(auth, self.secret_token):
                client_sock.sendall(json.dumps({"status": "error", "message": "Unauthorized"}).encode("utf-8"))
                client_sock.close()
                return

            cmd = req.get("command")
            args = req.get("args", {})

            handler = self._handlers.get(cmd)
            if not handler:
                resp = {"status": "error", "message": f"Command '{cmd}' not found"}
            else:
                resp = {"status": "ok", "result": handler(args)}

            client_sock.sendall(json.dumps(resp).encode("utf-8"))
        except Exception as e:
            try:
                client_sock.sendall(json.dumps({"status": "error", "message": str(e)}).encode("utf-8"))
            except Exception:
                pass
        finally:
            try:
                client_sock.close()
            except Exception:
                pass

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.stop()
