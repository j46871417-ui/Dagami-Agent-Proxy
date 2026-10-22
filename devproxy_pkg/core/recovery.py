"""
Recovery: State management and atomic rollback for persistent changes.
Tracks exact keys and values modified by devproxy in ~/.devproxy/state.json.
Restoring replaces manual edits of managed proxy keys with the original values.
Unrelated settings and failed recovery records are retained.
"""

import os
import json
import tempfile
from typing import Dict, Any, Optional
from .config_editor import ConfigEditor


def get_devproxy_state_path() -> str:
    home = os.path.expanduser("~")
    state_dir = os.environ.get("DEVPROXY_HOME") or os.path.join(home, ".devproxy")
    os.makedirs(state_dir, exist_ok=True)
    return os.path.join(state_dir, "state.json")


class StateManager:
    @staticmethod
    def load_state() -> dict:
        path = get_devproxy_state_path()
        if not os.path.exists(path):
            return {"profiles": {}, "applied": {}}
        with open(path, "r", encoding="utf-8") as f:
            state = json.load(f)
        if not isinstance(state, dict) or any(not isinstance(state.get(key, {}), dict) for key in ("profiles", "applied")):
            raise ValueError("Invalid recovery state; the original file was not changed.")
        return state

    @staticmethod
    def save_state(state: dict):
        path = get_devproxy_state_path()
        dir_name = os.path.dirname(path)
        os.makedirs(dir_name, exist_ok=True)
        fd, tmp_path = tempfile.mkstemp(dir=dir_name, prefix="state_")
        try:
            with open(fd, "w", encoding="utf-8") as f:
                json.dump(state, f, indent=2, ensure_ascii=False)
            os.replace(tmp_path, path)
        except Exception:
            if os.path.exists(tmp_path):
                try: os.remove(tmp_path)
                except Exception: pass
            raise

    @classmethod
    def record_applied_change(cls, app_id: str, file_path: str, modified_keys: Dict[str, Any], original_values: Dict[str, Any]):
        """Records modification made to a specific application file."""
        state = cls.load_state()
        applied = state.setdefault("applied", {})
        app_record = applied.setdefault(app_id, {})
        existing = app_record.get(file_path)
        if existing:
            existing["modified_keys"] = sorted(set(existing.get("modified_keys", [])) | set(modified_keys.keys()))
            existing["applied_values"] = dict(modified_keys)
            for key, value in original_values.items():
                existing.setdefault("original_values", {}).setdefault(key, value)
        else:
            app_record[file_path] = {
                "modified_keys": list(modified_keys.keys()),
                "original_values": dict(original_values),
                "applied_values": dict(modified_keys),
            }
        cls.save_state(state)

    @classmethod
    def get_applied_record(cls, app_id: str) -> Optional[dict]:
        state = cls.load_state()
        return state.get("applied", {}).get(app_id)

    @classmethod
    def clear_applied_record(cls, app_id: str):
        state = cls.load_state()
        if "applied" in state and app_id in state["applied"]:
            del state["applied"][app_id]
            cls.save_state(state)

    @classmethod
    def clear_applied_file(cls, app_id: str, file_path: str):
        state = cls.load_state()
        record = state.get("applied", {}).get(app_id, {})
        record.pop(file_path, None)
        if not record:
            state.get("applied", {}).pop(app_id, None)
        cls.save_state(state)

    @classmethod
    def restore_json(cls, app_id: str):
        record = cls.get_applied_record(app_id)
        if not record:
            return False, "No persistent changes recorded for " + app_id
        messages, errors = [], []
        for file_path, data in record.items():
            try:
                if not os.path.isfile(file_path):
                    raise FileNotFoundError("Configuration is missing; recovery record retained")
                parsed, raw = ConfigEditor.load_jsonc(file_path)
                if parsed is None:
                    raise ValueError("Invalid JSONC; recovery record retained")
                original = data.get("original_values", {})
                remove = [key for key, value in original.items() if value is None]
                updates = {key: value for key, value in original.items() if value is not None}
                content = ConfigEditor.remove_json_fields_preserving(raw, remove) if remove else raw
                if updates:
                    content = ConfigEditor.update_json_fields_preserving(content, updates)
                ConfigEditor.atomic_write(file_path, content)
                cls.clear_applied_file(app_id, file_path)
                messages.append("Restored " + file_path)
            except Exception as error:
                errors.append(file_path + ": " + str(error))
        return not errors, "; ".join(messages + errors)

    @classmethod
    def save_profile(cls, profile_dict: dict):
        state = cls.load_state()
        profiles = state.setdefault("profiles", {})
        profiles[profile_dict["name"]] = profile_dict
        cls.save_state(state)

    @classmethod
    def get_profile(cls, name: str) -> Optional[dict]:
        state = cls.load_state()
        return state.get("profiles", {}).get(name)

    @classmethod
    def list_profiles(cls) -> Dict[str, dict]:
        state = cls.load_state()
        return state.get("profiles", {})

    @classmethod
    def delete_profile(cls, name: str) -> bool:
        state = cls.load_state()
        if "profiles" in state and name in state["profiles"]:
            del state["profiles"][name]
            cls.save_state(state)
            return True
        return False
