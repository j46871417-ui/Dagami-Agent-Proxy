"""Experimental external-TUN launcher. Never patches an app or changes OS routes."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import sys

PROXY_KEYS = frozenset(k.lower() for k in (
    'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'AG_LS_PROXY',
    'AGY_PROXY_URL', 'NODE_USE_ENV_PROXY', 'DEVPROXY_IPC_PORT', 'DEVPROXY_IPC_KEY_FILE',
))


def clean_environment(source):
    # Preserve certificates, PATH and all unrelated user settings.
    result = {k: v for k, v in source.items() if k.lower() not in PROXY_KEYS}
    # Do not silently remove arbitrary user JVM/Chromium options.
    for key, value in result.items():
        if key.upper() in ('NODE_OPTIONS', 'JAVA_TOOL_OPTIONS', 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'):
            if any(word in value.lower() for word in ('proxy', 'require', 'loader')):
                raise ValueError('Conflicting inherited option: ' + key)
    return result


def socks_probe(port, timeout=2):
    """Only negotiate SOCKS5; never send a destination or account data."""
    if not 1 <= port <= 65535:
        raise ValueError('Port must be 1..65535')
    with socket.create_connection(('127.0.0.1', port), timeout=timeout) as stream:
        stream.settimeout(timeout)
        stream.sendall(b'\x05\x01\x00')
        reply = bytearray()
        while len(reply) < 2:
            part = stream.recv(2 - len(reply))
            if not part:
                raise ValueError('SOCKS5 negotiation ended early')
            reply.extend(part)
        if reply != b'\x05\x00':
            raise ValueError('Port does not accept SOCKS5 without authentication')
    return {'socks5': True, 'tun_verified': False,
            'note': 'A working SOCKS5 port does not prove TUN routing or model access.'}


def validate_install(executable, settings):
    executable = Path(executable).resolve(strict=True)
    if executable.name.lower() not in ('antigravity.exe', 'antigravity ide.exe'):
        raise ValueError('Select the original Antigravity EXE')
    if not executable.is_file():
        raise ValueError('Not an executable file')
    root = executable.parent
    # Refuse DLL side-loading markers, including an otherwise legitimate DLL:
    # this experiment cannot establish its provenance.
    for name in ('version.dll', 'winhttp.dll', 'wininet.dll', 'ws2_32.dll', 'config.json'):
        if (root / name).exists():
            raise ValueError('Review/remove conflicting sidecar before TUN launch: ' + name)
    resources = root / 'resources'
    if not ((resources / 'app.asar').is_file() or (resources / 'app' / 'package.json').is_file()):
        raise ValueError('Antigravity application resources not found')
    # Known Dagami ownership/backup records. Unknown third-party patches cannot
    # be certified absent; this is a preflight, not a binary authenticity check.
    for directory in (root, resources):
        if directory.exists():
            for item in directory.rglob('*'):
                if item.is_file() and ('devproxy' in item.name.lower() or 'dagami' in item.name.lower()) and any(
                    marker in item.name.lower() for marker in ('manifest', '.bak', 'backup', '.json')
                ):
                    raise ValueError('Existing patch/backup record needs review: ' + item.name)
    settings = Path(settings)
    if settings.exists():
        # VS Code settings allow comments/commas. Be conservative rather than
        # accepting a failed JSON parse as evidence of no configured proxy.
        text = settings.read_text(encoding='utf-8-sig')
        if 'http.proxy' in text or 'http.experimental.systemCertificates' in text:
            raise ValueError('Review proxy/TLS settings first: ' + str(settings))
    return executable


def running_at(executable):
    if sys.platform != 'win32':
        raise ValueError('The experimental launcher supports Windows only')
    # No command interpolation and no command lines/credentials in the response.
    script = 'Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ExecutablePath,ProcessId | ConvertTo-Json -Compress'
    shell = Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    result = subprocess.run([str(shell), '-NoProfile', '-NonInteractive', '-Command', script],
                            check=True, capture_output=True, text=True, timeout=15)
    rows = json.loads(result.stdout or '[]')
    if isinstance(rows, dict):
        rows = [rows]
    expected = os.path.normcase(str(executable))
    return [row['ProcessId'] for row in rows if row.get('ExecutablePath') and
            os.path.normcase(row['ExecutablePath']) == expected]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('doctor', 'plan', 'run'))
    parser.add_argument('--port', type=int, default=7897, help='Clash SOCKS5/mixed port; verify in Clash')
    parser.add_argument('--exe', type=Path)
    parser.add_argument('--settings', type=Path, help='Actual Antigravity User/settings.json, required for plan/run')
    args = parser.parse_args(argv)
    try:
        probe = socks_probe(args.port)
        if args.command == 'doctor':
            print(json.dumps(probe)); return 0
        if not args.exe or not args.settings:
            raise ValueError('--exe and --settings are required; use the actual profile path')
        executable = validate_install(args.exe, args.settings)
        env = clean_environment(os.environ)
        if running_at(executable):
            raise ValueError('Close Antigravity first; an existing process may retain its old route')
        report = dict(probe, mode='external-tun', app=executable.name,
                      proxy_env=False, bridge=False, binary_authenticity_verified=False)
        print(json.dumps(report), flush=True)
        if args.command == 'run':
            # No shell, no injected args, no patch/restore, no profile writes.
            child = subprocess.Popen([str(executable)], cwd=executable.parent, env=env)
            print(json.dumps({'started_pid': child.pid, 'model_access_verified': False}))
        return 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print('TUN preflight failed: ' + str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
