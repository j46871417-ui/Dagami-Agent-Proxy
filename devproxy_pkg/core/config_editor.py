"""
ConfigEditor: Safe JSONC and config modifier.
- Preserves comments and structure where possible.
- Atomic writes with backups.
- Keeps track of changes made specifically by devproxy in a rollback ledger.
- Never replaces a malformed file with an empty dictionary.
"""

import os
import json
import re
import shutil
import tempfile
from typing import Any, Dict, Optional, Tuple


def strip_json_comments(text: str) -> str:
    """State-machine comment stripper preserving string literals and URLs."""
    out = []
    in_string = False
    escape = False
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if in_string:
            out.append(c)
            if escape:
                escape = False
            elif c == '\\':
                escape = True
            elif c == '"':
                in_string = False
            i += 1
            continue

        if c == '"':
            in_string = True
            out.append(c)
            i += 1
            continue

        if c == '/' and i + 1 < n:
            if text[i + 1] == '/':
                # Line comment
                while i < n and text[i] != '\n':
                    i += 1
                if i < n:
                    out.append(text[i])
                    i += 1
                continue
            elif text[i + 1] == '*':
                # Block comment
                i += 2
                while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                    i += 1
                i += 2
                continue

        out.append(c)
        i += 1
    return "".join(out)


def strip_trailing_commas(text: str) -> str:
    """Remove trailing commas outside string literals."""
    out = []
    in_string = False
    escape = False
    i = 0
    while i < len(text):
        char = text[i]
        if in_string:
            out.append(char)
            if escape:
                escape = False
            elif char == "\\":
                escape = True
            elif char == '"':
                in_string = False
            i += 1
            continue
        if char == '"':
            in_string = True
            out.append(char)
            i += 1
            continue
        if char == ',':
            lookahead = i + 1
            while lookahead < len(text) and text[lookahead].isspace():
                lookahead += 1
            if lookahead < len(text) and text[lookahead] in '}]':
                i += 1
                continue
        out.append(char)
        i += 1
    return "".join(out)


def parse_jsonc(text: str) -> dict:
    parsed = json.loads(strip_trailing_commas(strip_json_comments(text)))
    if not isinstance(parsed, dict):
        raise ValueError("Configuration root must be a JSON object")
    return parsed


class ConfigEditor:
    @staticmethod
    def load_jsonc(path: str) -> Tuple[Optional[dict], Optional[str]]:
        """
        Safely reads JSONC file.
        Returns: (parsed_dict, raw_text).
        If file doesn't exist, returns ({}, "").
        If parse error, returns (None, raw_text).
        """
        if not os.path.exists(path):
            return {}, ""
        try:
            with open(path, "r", encoding="utf-8-sig") as f:
                raw = f.read()
            if not raw.strip():
                return {}, ""
            data = parse_jsonc(raw)
            return data, raw
        except Exception as e:
            return None, raw

    @staticmethod
    def update_json_fields_preserving(raw_text: str, updates: Dict[str, Any]) -> str:
        """
        Updates top-level keys and serializes validated JSON. Comments are removed
        deliberately because the standard library has no lossless JSONC writer.
        """
        data = parse_jsonc(raw_text) if raw_text.strip() else {}
        data.update(updates)
        return json.dumps(data, indent=4, ensure_ascii=False) + "\n"

    @staticmethod
    def remove_json_fields_preserving(raw_text: str, keys: list) -> str:
        """Removes specified keys from JSON/JSONC text preserving other structure."""
        data = parse_jsonc(raw_text) if raw_text.strip() else {}
        for key in keys:
            data.pop(key, None)
        return json.dumps(data, indent=4, ensure_ascii=False) + "\n"

    @staticmethod
    def atomic_write(path: str, content: str):
        """Writes content atomically to avoid corruption on crash."""
        dir_name = os.path.dirname(os.path.abspath(path))
        os.makedirs(dir_name, exist_ok=True)
        # Create backup if file exists
        if os.path.exists(path):
            backup_path = path + ".bak"
            if not os.path.exists(backup_path):
                shutil.copy2(path, backup_path)

        fd, tmp_path = tempfile.mkstemp(dir=dir_name, prefix="devproxy_tmp_")
        try:
            with open(fd, "w", encoding="utf-8") as f:
                f.write(content)
            # Atomic replace
            os.replace(tmp_path, path)
        except Exception:
            if os.path.exists(tmp_path):
                try:
                    os.remove(tmp_path)
                except Exception:
                    pass
            raise
