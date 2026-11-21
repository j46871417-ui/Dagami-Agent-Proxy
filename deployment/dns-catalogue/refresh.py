"""Validate provider definitions and publish a verified DNS bootstrap catalogue."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import http.client
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import socket
import ssl
import struct
import tempfile
from urllib.parse import urlsplit


QUESTION = b"\x07example\x03com\x00\x00\x01\x00\x01"
SSL = ssl.create_default_context()
SSL.minimum_version = ssl.TLSVersion.TLSv1_2
MAX_BYTES = 32768


def load(path):
    with path.open("rb") as stream:
        return json.loads(stream.read(MAX_BYTES + 1)) if path.stat().st_size <= MAX_BYTES else None


def public_ipv4(value):
    try:
        address = ipaddress.IPv4Address(value)
    except (ipaddress.AddressValueError, TypeError):
        return False
    return isinstance(value, str) and address.is_global and not address.is_multicast and not address.is_reserved


def definitions(data):
    if not isinstance(data, dict) or data.get("Version") != 1 or not isinstance(data.get("Providers"), list):
        raise ValueError("Expected Version 1 and a Providers array")
    records = data["Providers"]
    if len(records) > 16:
        raise ValueError("At most 16 providers")
    ids, urls = set(), set()
    for record in records:
        if not isinstance(record, dict) or set(record) != {"Id", "Name", "Url", "Addresses"}:
            raise ValueError("Every provider must have Id, Name, Url and Addresses")
        identity, name, url, seeds = (record[key] for key in ("Id", "Name", "Url", "Addresses"))
        if not isinstance(identity, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,31}", identity) or identity in ids:
            raise ValueError("Invalid or duplicate provider Id")
        if not isinstance(name, str) or not name.strip() or len(name) > 64 or any(not c.isprintable() for c in name):
            raise ValueError(f"{identity}: invalid name")
        if not isinstance(url, str) or len(url) > 512 or any(not 33 <= ord(c) <= 126 for c in url) or "\\" in url:
            raise ValueError(f"{identity}: invalid URL")
        parts = urlsplit(url)
        host = parts.hostname or ""
        host = host.rstrip(".")
        if (parts.scheme not in {"https", "tls"} or not re.fullmatch(r"(?:[a-z0-9-]+\.)+[a-z0-9-]+", host)
                or host.endswith((".local", ".localhost")) or not any(c.isalpha() for c in host)
                or parts.username is not None or "?" in url or "#" in url or parts.port == 0
                or (parts.scheme == "https" and parts.path in {"", "/"})
                or (parts.scheme == "tls" and parts.path not in {"", "/"})):
            raise ValueError(f"{identity}: expected a DoH URL with path or a DoT hostname")
        port = parts.port or (443 if parts.scheme == "https" else 853)
        target = (parts.scheme, host, port, parts.path.rstrip("/") if parts.scheme == "tls" else parts.path)
        if target in urls or not isinstance(seeds, list) or len(seeds) > 8 or not all(map(public_ipv4, seeds)):
            raise ValueError(f"{identity}: duplicate URL or invalid IPv4 seeds")
        ids.add(identity)
        urls.add(target)
    return records


def read_exact(stream, count):
    buffer = bytearray()
    while len(buffer) < count:
        part = stream.recv(count - len(buffer))
        if not part:
            raise OSError("Unexpected end of DNS stream")
        buffer.extend(part)
    return bytes(buffer)


def skip_name(packet, offset):
    for _ in range(128):
        length = packet[offset]
        if length == 0:
            return offset + 1
        if length & 0xC0 == 0xC0 and offset + 1 < len(packet):
            return offset + 2
        if length > 63:
            break
        offset += length + 1
    raise ValueError("Invalid DNS name")


def has_answer(packet, query):
    identity, flags, questions, answers, _, _ = struct.unpack_from("!6H", packet)
    if identity != int.from_bytes(query[:2], "big") or flags & 0xFA0F != 0x8000 or questions != 1:
        return False
    offset = 12 + len(QUESTION)
    if packet[12:offset].lower() != QUESTION:
        return False
    for _ in range(answers):
        offset = skip_name(packet, offset)
        kind, klass, _, length = struct.unpack_from("!HHIH", packet, offset)
        offset += 10
        data = packet[offset:offset + length]
        if kind == klass == 1 and length == len(data) == 4 and public_ipv4(socket.inet_ntoa(data)):
            return True
        offset += length
    return False


def probe(url, address):
    parts = urlsplit(url)
    host = parts.hostname.rstrip(".")
    port = parts.port or (443 if parts.scheme == "https" else 853)
    query = struct.pack("!6H", secrets.randbits(16), 0x0100, 1, 0, 0, 0) + QUESTION
    with socket.create_connection((address, port), timeout=3) as raw:
        with SSL.wrap_socket(raw, server_hostname=host) as stream:
            if parts.scheme == "tls":
                stream.sendall(struct.pack("!H", len(query)) + query)
                answer = read_exact(stream, struct.unpack("!H", read_exact(stream, 2))[0])
            else:
                request = (f"POST {parts.path} HTTP/1.1\r\nHost: {parts.netloc}\r\n"
                           "Accept: application/dns-message\r\nContent-Type: application/dns-message\r\n"
                           f"Content-Length: {len(query)}\r\nConnection: close\r\n\r\n").encode("ascii")
                stream.sendall(request + query)
                response = http.client.HTTPResponse(stream)
                response.begin()
                if response.status != 200 or response.headers.get_content_type() != "application/dns-message":
                    return False
                answer = response.read(65536)
    return has_answer(answer, query)


def resolve(host):
    try:
        return [result[4][0] for result in socket.getaddrinfo(host, None, socket.AF_INET, socket.SOCK_STREAM)]
    except socket.gaierror:
        return []


def refresh(records, previous, now, lookup=resolve, verify=probe):
    def check(url, address):
        try:
            return verify(url, address)
        except (OSError, ValueError, http.client.HTTPException, IndexError, struct.error):
            return False

    with ThreadPoolExecutor(max_workers=8) as pool:
        jobs = []
        for record in records:
            addresses = list(dict.fromkeys(record["Addresses"] + list(lookup(urlsplit(record["Url"]).hostname))))
            addresses = [address for address in addresses if public_ipv4(address)][:8]
            jobs.append((record, addresses, [pool.submit(check, record["Url"], ip) for ip in addresses]))
        updated = []
        for record, addresses, results in jobs:
            selected = [ip for ip, result in zip(addresses, results) if result.result()]
            stamp = now.isoformat() if selected else None
            if not selected:
                old = previous.get((record["Id"], record["Url"]), {})
                try:
                    stamp = old["VerifiedAt"]
                    age = now - datetime.fromisoformat(stamp)
                    if not timedelta(0) <= age <= timedelta(hours=24):
                        stamp = None
                except (KeyError, TypeError, ValueError):
                    stamp = None
                if stamp:
                    selected = [ip for ip in old.get("Addresses", []) if ip in addresses]
            print(f'{record["Id"]}: {len(selected)}/{len(addresses)} адресов' + (" (предыдущая проверка)" if selected and all(not result.result() for result in results) else ""))
            updated.append(dict(record, Addresses=selected, VerifiedAt=stamp if selected else None))
    return {"Version": 1, "Providers": updated}


def publish(path, document):
    data = json.dumps(document, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    if len(data) > MAX_BYTES:
        raise ValueError("Generated catalogue exceeds the client limit")
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".catalogue-")
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            os.fchmod(stream.fileno(), 0o644) if hasattr(os, "fchmod") else os.chmod(temporary, 0o644)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("providers", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--check", action="store_true", help="validate input without any network calls")
    args = parser.parse_args()
    if args.providers.resolve() == args.output.resolve():
        parser.error("Choose separate definitions and output paths")
    try:
        records = definitions(load(args.providers))
        if args.check:
            print(f"PASS: {len(records)} providers")
            return
        previous = {}
        try:
            stored = load(args.output)
            previous = {(r["Id"], r["Url"]): r for r in stored["Providers"]}
        except (OSError, KeyError, TypeError, ValueError):
            pass
        publish(args.output, refresh(records, previous, datetime.now(timezone.utc)))
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
