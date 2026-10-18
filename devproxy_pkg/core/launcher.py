"""
Launcher: Safe process execution with isolated environment and command-line arguments.
Rules:
- NEVER uses shell=True.
- Passes clean list of arguments.
- Safely injects HTTP_PROXY / HTTPS_PROXY / ALL_PROXY into the target child process ONLY.
- Leaves global machine / user environment untouched.
- Integrates Win32 Job Objects on Windows to guarantee child process tree cleanup on exit.
"""

import os
import sys
import subprocess
import threading
from typing import List, Dict, Optional, Tuple, Any
from .tunnel import LocalTunnel
from .profile import ProxyProfile
from .process_env import external_environment

if sys.platform == "win32":
    from .windows_job import WindowsJobManager
else:
    WindowsJobManager = None

from .agent_ipc import EphemeralAgentIPC


class Launcher:
    @staticmethod
    def launch(
        executable: str,
        extra_args: Optional[List[str]] = None,
        env_vars: Optional[Dict[str, str]] = None,
        wait: bool = False,
        cwd: Optional[str] = None,
        job_manager: Optional[Any] = None,
        stdin=None, stdout=None, stderr=None,
        new_session: bool = True
    ) -> subprocess.Popen:
        """
        Executes binary safely without shell=True.
        Assigns process to WindowsJobManager if provided on Windows.
        """
        if not os.path.exists(executable) and not shutil_which(executable):
            raise FileNotFoundError(f"Executable not found: {executable}")

        cmd = [executable]
        if extra_args:
            cmd.extend(extra_args)

        env = external_environment(env_vars)

        creationflags = 0
        
        proc = subprocess.Popen(
            cmd,
            env=env,
            cwd=cwd,
            creationflags=creationflags,
            start_new_session=sys.platform != "win32" and new_session,
            stdin=stdin, stdout=stdout, stderr=stderr
        )

        if sys.platform == "win32" and job_manager is not None:
            try:
                # proc._handle is the OS handle opened by subprocess.Popen on Windows
                job_manager.assign_process(proc._handle)
            except Exception:
                proc.terminate()
                proc.wait(timeout=5)
                raise

        if wait:
            proc.wait()
        return proc

    @classmethod
    def launch_with_tunnel(
        cls,
        executable: str,
        profile: ProxyProfile,
        adapter_cli_flags: Optional[List[str]] = None,
        extra_args: Optional[List[str]] = None,
        wait: bool = True,
        enable_ipc: bool = True
    ) -> Tuple[subprocess.Popen, LocalTunnel]:
        """
        Starts ephemeral LocalTunnel and launches application configured to use it.
        Guarantees that child processes are bound to a Win32 Job Object (on Windows)
        and all tunnels and IPC threads are cleanly torn down upon exit.
        """
        tunnel = LocalTunnel(profile)
        port = tunnel.start()

        local_proxy_url = f"http://127.0.0.1:{port}"
        
        # Prepare environment
        env_vars = {
            "HTTP_PROXY": local_proxy_url,
            "HTTPS_PROXY": local_proxy_url,
            "ALL_PROXY": local_proxy_url,
            "AG_LS_PROXY": local_proxy_url,
            "AGY_PROXY_URL": local_proxy_url,
            "agy_proxy_url": local_proxy_url,
            "http_proxy": local_proxy_url,
            "https_proxy": local_proxy_url,
            "all_proxy": local_proxy_url,
            "NO_PROXY": "localhost,127.0.0.1,::1",
            "no_proxy": "localhost,127.0.0.1,::1"
        }

        # Combine arguments
        cmd_args = []
        if adapter_cli_flags:
            for flag in adapter_cli_flags:
                cmd_args.append(flag.replace("{PROXY_URL}", local_proxy_url))
        if extra_args:
            cmd_args.extend(extra_args)

        # Initialize per-session Job Object on Windows
        job_mgr = None
        if sys.platform == "win32":
            try:
                job_mgr = WindowsJobManager()
            except Exception:
                job_mgr = None

        # Initialize Ephemeral per-session IPC server
        ipc_server = None
        if enable_ipc:
            try:
                ipc_server = EphemeralAgentIPC()
                ipc_port = ipc_server.start()
                env_vars["DEVPROXY_IPC_PORT"] = str(ipc_port)
                env_vars["DEVPROXY_IPC_KEY_FILE"] = ipc_server.key_file
            except Exception:
                ipc_server = None

        proc = None
        try:
            proc = cls.launch(
                executable,
                extra_args=cmd_args,
                env_vars=env_vars,
                wait=False,
                job_manager=job_mgr,
                new_session=not wait
            )
            if wait:
                proc.wait()
            if not wait:
                def cleanup_when_done():
                    try:
                        proc.wait()
                    finally:
                        tunnel.stop()
                        if ipc_server:
                            ipc_server.stop()
                        if job_mgr:
                            job_mgr.close()

                threading.Thread(
                    target=cleanup_when_done,
                    daemon=True,
                    name=f"DevProxySessionCleanup-{proc.pid}",
                ).start()
            return proc, tunnel
        except BaseException:
            if proc is not None and proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait(timeout=3)
            tunnel.stop()
            if ipc_server:
                ipc_server.stop()
            if job_mgr:
                job_mgr.close()
            raise
        finally:
            if wait:
                # If we waited for process exit, clean up resources
                tunnel.stop()
                if ipc_server:
                    ipc_server.stop()
                if job_mgr:
                    job_mgr.close()


def shutil_which(cmd: str) -> Optional[str]:
    import shutil
    return shutil.which(cmd)
