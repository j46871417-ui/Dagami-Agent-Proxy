"""License key generator and validator for Dagami Agent Proxy.

Can be run directly or imported into a Telegram bot (e.g. @bright26bot).
Usage:
    python generate_license_key.py           # generate 1 key
    python generate_license_key.py 5         # generate 5 keys
    python generate_license_key.py --verify DAGAMI-XXXX-XXXX-XXXX
"""

import sys
import os
import re
import hashlib

ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
SALT = "DagamiAgentProxySecretSalt2026"


def compute_checksum(payload: str) -> str:
    """Compute 4-character cryptographic checksum from 8-char payload."""
    h = hashlib.sha256((payload + SALT).encode("utf-8")).digest()
    return "".join(ALPHABET[b % 32] for b in h[:4])


def generate_key(seed: bytes = None) -> str:
    """Generate a single valid license key."""
    if seed is None:
        seed = os.urandom(8)
    payload = "".join(ALPHABET[b % 32] for b in seed[:8])
    chk = compute_checksum(payload)
    return f"DAGAMI-{payload[:4]}-{payload[4:]}-{chk}"


def validate_key(key: str) -> bool:
    """Validate a license key."""
    if not key or not isinstance(key, str):
        return False
    k = re.sub(r"[^A-Za-z0-9]", "", key).upper()
    if not k.startswith("DAGAMI"):
        return False
    body = k[6:]
    if len(body) != 12:
        return False
    if any(c not in ALPHABET for c in body):
        return False
    payload = body[:8]
    chk = body[8:]
    return compute_checksum(payload) == chk


def main():
    args = sys.argv[1:]
    if args and args[0] in ("--verify", "-v"):
        if len(args) < 2:
            print("Usage: python generate_license_key.py --verify <KEY>")
            sys.exit(1)
        key = args[1]
        valid = validate_key(key)
        print(f"Key: {key} -> {'VALID [OK]' if valid else 'INVALID [FAIL]'}")
        sys.exit(0 if valid else 1)

    count = 1
    if args:
        try:
            count = int(args[0])
        except ValueError:
            pass

    for _ in range(count):
        print(generate_key())


if __name__ == "__main__":
    main()
