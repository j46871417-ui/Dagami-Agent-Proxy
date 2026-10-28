"""
Tunnel: Local loopback forwarder and transport proxy bridge.
Ensures:
- Local loopback only (127.0.0.1 or ::1) on an ephemeral or designated port.
- Transparent streaming of encrypted bytes (NO MitM decryption, NO inspecting prompts/code, NO tampering with responses).
- Bridges tools that only understand plain HTTP proxy to SOCKS5 or authenticated upstreams.
- Injects Proxy-Authorization only toward the designated upstream proxy.
- FAIL-CLOSED: Errors from upstream proxy return 502/504 Bad Gateway, NEVER falling back to unproxied direct connection.
- Integrates AdaptiveTLSGate for tiered backpressure concurrency control.
"""

import socket
import ipaddress
import ssl
import threading
import base64
import time
import urllib.parse
from typing import Optional
from .profile import ProxyProfile
from .transport import AdaptiveTLSGate, tune_socket, FastDuplexRelay


class LocalTunnel:
    def __init__(self, profile: ProxyProfile, bind_host: str = "127.0.0.1", bind_port: int = 0):
        self.profile = profile
        self.bind_host = bind_host
        self.bind_port = bind_port
        self.server_sock: Optional[socket.socket] = None
        self.is_running = False
        self._thread: Optional[threading.Thread] = None
        self.allocated_port: int = 0
        self.tls_gate = AdaptiveTLSGate()
        self._connections = set()
        self._connections_lock = threading.Lock()

    def start(self, timeout: float = 3.0) -> int:
        """Starts loopback server and returns bound port."""
        family = socket.AF_INET6 if ":" in self.bind_host else socket.AF_INET
        self.server_sock = socket.socket(family, socket.SOCK_STREAM)
        self.server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server_sock.bind((self.bind_host, self.bind_port))
        self.server_sock.listen(128)
        self.allocated_port = self.server_sock.getsockname()[1]
        self.is_running = True

        self._thread = threading.Thread(target=self._serve_loop, daemon=True, name="DevProxyTunnel")
        self._thread.start()

        # Readiness check
        t0 = time.time()
        while time.time() - t0 < timeout:
            try:
                test_sock = socket.create_connection((self.bind_host, self.allocated_port), timeout=0.2)
                test_sock.close()
                return self.allocated_port
            except Exception:
                time.sleep(0.05)

        return self.allocated_port

    def stop(self):
        """Stops server and terminates connections."""
        self.is_running = False
        if self.server_sock:
            try:
                self.server_sock.close()
            except Exception:
                pass
        with self._connections_lock:
            connections = list(self._connections)
        for connection in connections:
            try:
                connection.shutdown(socket.SHUT_RDWR)
            except Exception:
                pass
            try:
                connection.close()
            except Exception:
                pass
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=1.0)

    def _serve_loop(self):
        while self.is_running:
            try:
                client_sock, addr = self.server_sock.accept()
                tune_socket(client_sock)
                t = threading.Thread(target=self._handle_client, args=(client_sock,), daemon=True)
                t.start()
            except Exception:
                if not self.is_running:
                    break

    def _handle_client(self, client_sock: socket.socket):
        upstream_sock = None
        response_started = False
        self._track_socket(client_sock, True)
        try:
            client_sock.settimeout(15.0)
            req_data = self._recv_headers(client_sock)
            header_end = req_data.find(b"\r\n\r\n") + 4
            header_bytes = req_data[:header_end]
            initial_payload = req_data[header_end:]

            lines = header_bytes.split(b"\r\n")
            if not lines:
                client_sock.close()
                return

            first_line = lines[0].decode("latin-1", errors="replace")
            parts = first_line.split(" ")
            if len(parts) < 3:
                client_sock.close()
                return

            method, target_uri, proto = parts[0].upper(), parts[1], parts[2]
            
            dest_host = ""
            dest_port = 80

            if method == "CONNECT":
                parsed_target = urllib.parse.urlsplit("//" + target_uri)
                dest_host = parsed_target.hostname or ""
                dest_port = parsed_target.port or 443
            else:
                # Normal HTTP request (GET http://host:port/path HTTP/1.1 or GET /path with Host: header)
                if target_uri.startswith("http://") or target_uri.startswith("https://"):
                    p = urllib.parse.urlsplit(target_uri)
                    dest_host = p.hostname
                    dest_port = p.port or (443 if p.scheme == "https" else 80)
                else:
                    for line in lines[1:]:
                        if line.lower().startswith(b"host:"):
                            hval = line.split(b":", 1)[1].strip().decode("latin-1")
                            parsed_host = urllib.parse.urlsplit("//" + hval)
                            dest_host = parsed_host.hostname or ""
                            dest_port = parsed_host.port or 80
                            break

            if not dest_host:
                client_sock.sendall(b"HTTP/1.1 400 Bad Request\r\n\r\n")
                client_sock.close()
                return

            # Local RPC/OAuth/backend traffic must not leave the machine or
            # receive upstream credentials. Only literal loopback/localhost;
            # an arbitrary hostname resolving to a private address is not a bypass.
            try:
                address = ipaddress.ip_address('127.0.0.1' if dest_host.lower() == 'localhost' else dest_host)
                address = getattr(address, 'ipv4_mapped', None) or address
                local = address.is_loopback
            except ValueError:
                local = False
            if local:
                if dest_port == self.allocated_port and address == ipaddress.ip_address(self.bind_host):
                    client_sock.sendall(b'HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n')
                    return
                address = '127.0.0.1' if dest_host.lower() == 'localhost' else dest_host
                upstream_sock = socket.create_connection((address, dest_port), timeout=8.0)
                tune_socket(upstream_sock)
                if method != 'CONNECT' and target_uri.startswith('https://'):
                    upstream_sock = ssl.create_default_context().wrap_socket(upstream_sock, server_hostname=dest_host)
                self._track_socket(upstream_sock, True)
                if method == 'CONNECT':
                    client_sock.sendall(b'HTTP/1.1 200 Connection established\r\n\r\n')
                    response_started = True
                    upstream_sock.sendall(initial_payload)
                else:
                    local_header = self._origin_form_request(header_bytes, target_uri)
                    safe = [line for line in local_header.split(b'\r\n') if line and
                            line.split(b':', 1)[0].strip().lower() not in (b'proxy-authorization', b'proxy-connection', b'connection')]
                    upstream_sock.sendall(b'\r\n'.join(safe) + b'\r\nConnection: close\r\n\r\n' + initial_payload)
                client_sock.settimeout(None)
                upstream_sock.settimeout(None)
                response_started = True
                FastDuplexRelay.pipe(client_sock, upstream_sock)
                return

            # Establish connection to UPSTREAM proxy under AdaptiveTLSGate protection
            try:
                with self.tls_gate.acquire(timeout=8.0):
                    t0 = time.monotonic()
                    upstream_sock = socket.create_connection((self.profile.host, self.profile.port), timeout=8.0)
                    tune_socket(upstream_sock)
                    if self.profile.protocol == "https":
                        context = ssl.create_default_context()
                        upstream_sock = context.wrap_socket(upstream_sock, server_hostname=self.profile.host)
                    self.tls_gate.record_rtt(time.monotonic() - t0)
                    self._track_socket(upstream_sock, True)
            except TimeoutError:
                client_sock.sendall(b"HTTP/1.1 504 Gateway Timeout (TLS Handshake Queue Deadline Exceeded)\r\n\r\n")
                client_sock.close()
                return
            except Exception:
                # Return 502 Bad Gateway to client (FAIL-CLOSED)
                client_sock.sendall(b"HTTP/1.1 502 Bad Gateway (Proxy Unreachable)\r\n\r\n")
                client_sock.close()
                return

            # Route through upstream depending on its protocol
            if self.profile.is_socks:
                # Upstream is SOCKS5
                if not self._socks5_connect_upstream(upstream_sock, dest_host, dest_port):
                    client_sock.sendall(b"HTTP/1.1 502 Bad Gateway (SOCKS5 Handshake Failed)\r\n\r\n")
                    client_sock.close()
                    upstream_sock.close()
                    return

                if method == "CONNECT":
                    # Tell client HTTP 200 Connection established
                    client_sock.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
                else:
                    # Forward the plain HTTP request bytes to SOCKS5 tunnel
                    upstream_sock.sendall(self._origin_form_request(header_bytes, target_uri) + initial_payload)

            else:
                # Upstream is HTTP/HTTPS proxy
                if method == "CONNECT":
                    # Send CONNECT to upstream proxy with Proxy-Authorization if required
                    conn_headers = [f"CONNECT {dest_host}:{dest_port} HTTP/1.1\r\nHost: {dest_host}:{dest_port}\r\n"]
                    if self.profile.has_auth:
                        cred = f"{self.profile.username or ''}:{self.profile.password or ''}"
                        b64 = base64.b64encode(cred.encode("utf-8")).decode("ascii")
                        conn_headers.append(f"Proxy-Authorization: Basic {b64}\r\n")
                    conn_headers.append("\r\n")
                    upstream_sock.sendall("".join(conn_headers).encode("latin-1"))

                    # Read upstream response
                    resp_buf = self._recv_headers(upstream_sock)
                    response_end = resp_buf.find(b"\r\n\r\n") + 4
                    status_line = resp_buf[:response_end].split(b"\r\n", 1)[0].split()
                    status_code = int(status_line[1]) if len(status_line) >= 2 and status_line[1].isdigit() else 0
                    if status_code != 200:
                        client_sock.sendall(resp_buf if resp_buf else b"HTTP/1.1 502 Bad Gateway\r\n\r\n")
                        client_sock.close()
                        upstream_sock.close()
                        return
                    
                    # Notify client 200 OK
                    client_sock.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
                    if response_end < len(resp_buf):
                        client_sock.sendall(resp_buf[response_end:])
                else:
                    # Forward HTTP request with injected Proxy-Authorization
                    if self.profile.has_auth and b"proxy-authorization:" not in header_bytes.lower():
                        cred = f"{self.profile.username or ''}:{self.profile.password or ''}"
                        b64 = base64.b64encode(cred.encode("utf-8")).decode("ascii")
                        auth_hdr = f"Proxy-Authorization: Basic {b64}\r\n".encode("latin-1")
                        header_bytes = header_bytes.replace(b"\r\n", b"\r\n" + auth_hdr, 1)
                    upstream_sock.sendall(header_bytes + initial_payload)

            if method == "CONNECT" and initial_payload:
                upstream_sock.sendall(initial_payload)

            # Bidirectional streaming of raw bytes (end-to-end TLS preserved without decryption)
            client_sock.settimeout(None)
            upstream_sock.settimeout(None)
            response_started = True
            FastDuplexRelay.pipe(client_sock, upstream_sock)

        except Exception:
            if not response_started:
                try:
                    client_sock.sendall(b'HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n')
                except OSError:
                    pass
        finally:
            for connection in (client_sock, upstream_sock):
                if connection is None:
                    continue
                self._track_socket(connection, False)
                try:
                    connection.close()
                except Exception:
                    pass

    @staticmethod
    def _recv_headers(sock: socket.socket, limit: int = 65536) -> bytes:
        data = bytearray()
        while b"\r\n\r\n" not in data:
            chunk = sock.recv(min(4096, limit - len(data)))
            if not chunk:
                raise ConnectionError("Connection closed before complete HTTP headers")
            data.extend(chunk)
            if len(data) >= limit:
                raise ValueError("HTTP headers exceed 64 KiB")
        return bytes(data)

    @staticmethod
    def _origin_form_request(headers: bytes, target_uri: str) -> bytes:
        parsed = urllib.parse.urlsplit(target_uri)
        if parsed.scheme not in ("http", "https"):
            return headers
        path = urllib.parse.urlunsplit(("", "", parsed.path or "/", parsed.query, ""))
        first_end = headers.find(b"\r\n")
        first_line = headers[:first_end].decode("latin-1").split(" ")
        first_line[1] = path
        return " ".join(first_line).encode("latin-1") + headers[first_end:]

    def _track_socket(self, sock: socket.socket, add: bool):
        with self._connections_lock:
            if add:
                self._connections.add(sock)
            else:
                self._connections.discard(sock)

    def _socks5_connect_upstream(self, sock: socket.socket, host: str, port: int) -> bool:
        """Performs SOCKS5 greeting + auth + CONNECT command."""
        try:
            sock.settimeout(10.0)
            if self.profile.has_auth:
                sock.sendall(b"\x05\x02\x00\x02")
            else:
                sock.sendall(b"\x05\x01\x00")
            
            resp = self._recv_exact(sock, 2)
            if len(resp) < 2 or resp[0] != 0x05:
                return False
            
            auth_method = resp[1]
            if auth_method == 0x02:
                user_b = (self.profile.username or "").encode("utf-8")
                pwd_b = (self.profile.password or "").encode("utf-8")
                req = bytearray([0x01, len(user_b)]) + user_b + bytearray([len(pwd_b)]) + pwd_b
                sock.sendall(req)
                auth_resp = self._recv_exact(sock, 2)
                if len(auth_resp) < 2 or auth_resp[1] != 0x00:
                    return False
            elif auth_method != 0x00:
                return False

            # CONNECT by domain name (0x03)
            hb = host.encode("utf-8")
            pb = port.to_bytes(2, byteorder="big")
            cmd = bytearray([0x05, 0x01, 0x00, 0x03, len(hb)]) + hb + pb
            sock.sendall(cmd)

            conn_resp = self._recv_exact(sock, 4)
            if len(conn_resp) < 4 or conn_resp[1] != 0x00:
                return False
            
            atyp = conn_resp[3]
            if atyp == 0x01: self._recv_exact(sock, 4 + 2)
            elif atyp == 0x03:
                dlen = self._recv_exact(sock, 1)[0]
                self._recv_exact(sock, dlen + 2)
            elif atyp == 0x04: self._recv_exact(sock, 16 + 2)
            
            return True
        except Exception:
            return False

    @staticmethod
    def _recv_exact(sock: socket.socket, length: int) -> bytes:
        chunks = bytearray()
        while len(chunks) < length:
            chunk = sock.recv(length - len(chunks))
            if not chunk:
                raise ConnectionError("Unexpected end of SOCKS5 response")
            chunks.extend(chunk)
        return bytes(chunks)
