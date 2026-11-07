"""Telegram bot adapter for the managed Dagami gateway's private Unix socket."""

import http.client
import json
import os
import re
import socket


PAIR_CODE = re.compile(r"[A-Za-z0-9_-]{43}\Z")


def start_code(text):
    parts = (text or "").strip().split(maxsplit=1)
    if len(parts) == 2 and parts[0].split("@", 1)[0] == "/start" and parts[1].startswith("dagami_"):
        value = parts[1][7:]
        return value if PAIR_CODE.fullmatch(value) else ""
    return None


class _SocketConnection(http.client.HTTPConnection):
    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(os.environ.get("DAGAMI_MANAGED_BOT_SOCKET", "/run/dagami-managed/bot.sock"))


def _post(path, fields):
    connection = _SocketConnection("localhost", timeout=4)
    try:
        connection.request("POST", path, json.dumps(fields), {"Content-Type": "application/json"})
        response = connection.getresponse()
        return response.status
    finally:
        connection.close()


def pending(code):
    if not PAIR_CODE.fullmatch(code):
        return False
    status = _post("/private/pending", {"code": code})
    if status not in (204, 410):
        raise RuntimeError("managed gateway unavailable")
    return status == 204


def approve(code, user_id, login):
    if not PAIR_CODE.fullmatch(code):
        return 410
    status = _post("/private/approve", {"code": code, "subject": str(user_id), "login": login})
    if status not in (204, 409, 410):
        raise RuntimeError("managed gateway unavailable")
    return status


def revoke(user_id):
    if _post("/private/revoke", {"subject": str(user_id)}) != 204:
        raise RuntimeError("managed gateway unavailable")
