"""Authorize a loopback TCP CLIENT by its OS account, failing closed.

Same-account processes remain inside the local trust boundary. No persistent
firewall, account, privilege, registry or network settings are changed.
"""
import ctypes
import ipaddress
import os
import socket
import struct
import subprocess
import sys
from pathlib import Path

from .process_env import external_environment


def _endpoint(address):
    ip = ipaddress.ip_address(address[0])
    return str(ip), address[1]


def _windows_pid(peer, local):
    family = socket.AF_INET6 if ':' in peer[0] else socket.AF_INET
    table_fn = ctypes.WinDLL('iphlpapi').GetExtendedTcpTable
    table_fn.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong), ctypes.c_int, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    table_fn.restype = ctypes.c_ulong
    size = ctypes.c_ulong()
    for _ in range(3):
        result = table_fn(None, ctypes.byref(size), False, family, 5, 0)
        if result != 122 or not 4 <= size.value <= 16 * 1024 * 1024:
            return None
        table = ctypes.create_string_buffer(size.value)
        result = table_fn(table, ctypes.byref(size), False, family, 5, 0)
        if result == 122:
            continue
        if result:
            return None
        raw = table.raw
        count, = struct.unpack_from('<I', raw)
        stride = 56 if family == socket.AF_INET6 else 24
        if count > (len(raw) - 4) // stride:
            return None
        for offset in range(4, 4 + count * stride, stride):
            if family == socket.AF_INET:
                source = socket.inet_ntop(family, raw[offset+4:offset+8]), struct.unpack_from('!H', raw, offset+8)[0]
                target = socket.inet_ntop(family, raw[offset+12:offset+16]), struct.unpack_from('!H', raw, offset+16)[0]
                pid, = struct.unpack_from('<I', raw, offset+20)
            else:
                source = socket.inet_ntop(family, raw[offset:offset+16]), struct.unpack_from('!H', raw, offset+20)[0]
                target = socket.inet_ntop(family, raw[offset+24:offset+40]), struct.unpack_from('!H', raw, offset+44)[0]
                pid, = struct.unpack_from('<I', raw, offset+52)
            if source == peer and target == local:
                return pid
        return None
    return None


def _windows_same_user(pid):
    if not pid:
        return False
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    advapi = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    advapi.EqualSid.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    process = kernel.OpenProcess(0x1000, False, pid)
    if not process:
        return False
    handles = []
    try:
        buffers = []
        for subject in (process, kernel.GetCurrentProcess()):
            token = wintypes.HANDLE()
            if not advapi.OpenProcessToken(subject, 8, ctypes.byref(token)):
                return False
            handles.append(token)
            length = wintypes.DWORD()
            advapi.GetTokenInformation(token, 1, None, 0, ctypes.byref(length))
            if not 0 < length.value <= 65536:
                return False
            data = ctypes.create_string_buffer(length.value)
            if not advapi.GetTokenInformation(token, 1, data, length, ctypes.byref(length)):
                return False
            buffers.append(data)
        return bool(advapi.EqualSid(*(ctypes.cast(data, ctypes.POINTER(ctypes.c_void_p))[0] for data in buffers)))
    finally:
        for token in handles:
            kernel.CloseHandle(token)
        kernel.CloseHandle(process)


def _linux_uid(peer, local):
    ipv6 = ':' in peer[0]
    def decode(value):
        address, port = value.split(':')
        raw = bytes.fromhex(address)
        if sys.byteorder == 'little':
            raw = b''.join(raw[i:i+4][::-1] for i in range(0, len(raw), 4))
        return str(ipaddress.ip_address(raw)), int(port, 16)
    with Path('/proc/net/tcp6' if ipv6 else '/proc/net/tcp').open(encoding='ascii') as rows:
        next(rows)
        for row in rows:
            fields = row.split()
            if decode(fields[1]) == peer and decode(fields[2]) == local:
                return int(fields[7])
    return None


def _mac_uid(peer, local):
    def render(value):
        host, port = value
        return f'[{host}]:{port}' if ':' in host else f'{host}:{port}'
    expected = render(peer) + '->' + render(local)
    result = subprocess.run(['/usr/sbin/lsof', '-nP', '-a', f'-iTCP:{peer[1]}', '-F', 'un'],
                            capture_output=True, text=True, timeout=2, env=external_environment())
    uid = None
    for line in result.stdout.splitlines():
        if line.startswith('u'):
            uid = int(line[1:])
        if line == 'n' + expected:
            return uid
    return None


def is_current_user(client):
    try:
        peer, local = _endpoint(client.getpeername()), _endpoint(client.getsockname())
        if not ipaddress.ip_address(peer[0]).is_loopback:
            return False
        if sys.platform == 'win32':
            return _windows_same_user(_windows_pid(peer, local))
        if sys.platform.startswith('linux'):
            return _linux_uid(peer, local) == os.geteuid()
        if sys.platform == 'darwin':
            return _mac_uid(peer, local) == os.geteuid()
    except (OSError, ValueError, IndexError, AttributeError, struct.error, subprocess.SubprocessError):
        pass
    return False
