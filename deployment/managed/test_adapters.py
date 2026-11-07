"""Apply the overlays to a local snapshot and exercise them with fake users."""

import asyncio
import base64
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch


HERE = Path(__file__).resolve().parent
SNAPSHOT = Path(os.environ.get("DAGAMI_BOT_SNAPSHOT", HERE.parents[2] / "bot-us-migration" / "deployed" / "opt"))


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


class AdapterTests(unittest.TestCase):
    def prepare(self, directory, name):
        folder = Path(self.enterContext(tempfile.TemporaryDirectory()))
        shutil.copyfile(SNAPSHOT / directory / name, folder / name)
        overlay = HERE / (name + ".patch")
        subprocess.run(["git", "apply", "--check", str(overlay)], cwd=folder, check=True)
        subprocess.run(["git", "apply", str(overlay)], cwd=folder, check=True)
        return folder

    def test_controller_requires_internal_trust_and_keeps_policy(self):
        folder = self.prepare("agw-gateway", "controller.py")
        load("traffic_meter", SNAPSHOT / "agw-gateway" / "traffic_meter.py")
        controller = load("controller_fixture", folder / "controller.py")
        secret = base64.urlsafe_b64encode(b"z" * 32).decode().rstrip("=")
        (folder / "secret").write_text(secret)
        with patch.dict(os.environ, DAGAMI_MANAGED_BRIDGE_KEY_FILE=str(folder / "secret"), DAGAMI_MANAGED_ALLOW_LEGACY="0"):
            auth = controller.Controller({"route_count": 1, "nodes": {"us": {}}, "database": str(folder / "fixture.db"),
                                          "google_networks": [], "google_hosts": ["example.test"], "google_suffixes": [], "upstream_auth": "Basic fixture"})
        self.addCleanup(auth.db.close)
        auth.health["us"]["up"] = True
        auth.snapshot({"ttl": 30, "accounts": [{"login": "fixture", "hash": controller.password_hash("fixture-password"),
                       "subject": "42", "allowed": ["us"], "preferred": "us", "scope": "google"}]})
        basic = "Basic " + base64.b64encode(b"fixture:fixture-password").decode()
        self.assertEqual(auth.authorize(basic, "example.test:443")[0], 407)
        auth.allow_legacy = True
        self.assertEqual(auth.authorize(basic, "example.test:443")[0], 200)
        auth.allow_legacy = False
        self.assertEqual(auth.authorize("Dagami-Local fake.fixture", "example.test:443")[0], 407)
        header = "Dagami-Local " + secret + ".fixture"
        self.assertEqual(auth.authorize(header, "127.0.0.1:443")[0], 403)
        status, fields = auth.authorize(header, "example.test:443")
        self.assertEqual((status, fields["X-AGW-Backend"], len(fields["X-AGW-Subject"])), (200, "chain_us", 64))
        auth.db.execute("UPDATE accounts SET expires=0")
        self.assertEqual(auth.authorize(header, "example.test:443")[0], 407)

    def test_bot_menu_and_confirmation_do_not_render_credentials(self):
        folder = self.prepare("agw-bot", "dagami_menu.py")
        adapter = load("bot_access", HERE / "bot_access.py")
        aiogram, errors, warp = (types.ModuleType(name) for name in ("aiogram", "aiogram.exceptions", "warp_menu"))
        aiogram.BaseMiddleware = object
        aiogram.types = types.SimpleNamespace(**{name: types.SimpleNamespace for name in
                                               ("InlineKeyboardMarkup", "InlineKeyboardButton", "CopyTextButton")})
        errors.TelegramBadRequest = type("TelegramBadRequest", (Exception,), {})
        errors.TelegramForbiddenError = type("TelegramForbiddenError", (Exception,), {})
        warp.WarpMenu = warp.Store = object
        warp.VIEWS = set()
        with patch.dict(sys.modules, {"aiogram": aiogram, "aiogram.exceptions": errors, "warp_menu": warp}):
            menu = load("menu_fixture", folder / "dagami_menu.py")
        for view in ("home", "help", "routes", "rules", "renew", "gate", "donate"):
            session = menu.Session(42, view=view)
            result = repr(menu.screen(session, {"login": "fixture-login", "password": "fixture-password"}, {"routes": ["us"]}))
            self.assertNotIn("fixture-login", result)
            self.assertNotIn("fixture-password", result)
            self.assertNotIn("50128", result)
        code = "z" * 43
        self.assertEqual(adapter.start_code("/start dagami_" + code), code)
        self.assertEqual(adapter.start_code("/start dagami_bad"), "")
        self.assertIsNone(adapter.start_code("/proxy"))
        self.assertTrue(adapter.starts_key_flow("/start@bright26bot dagamikey"))
        self.assertFalse(adapter.starts_key_flow("/start dagami_" + code))
        online_key = "DAGAMI1-" + "A" * 43
        with patch.object(adapter, "_post", return_value=online_key) as post:
            self.assertEqual(adapter.issue_access(42, "fixture-login"), online_key)
            post.assert_called_once_with("/private/issue-access", {"subject": "42", "login": "fixture-login"}, read_code=True)
        content = repr(menu.screen(menu.Session(42, view="key"), {"access_code": online_key, "login": "fixture-login"}))
        self.assertIn(online_key, content)
        self.assertNotIn("fixture-login", content)
        for old_button in ("get_proxy_grant", "agw3:creds", "dagami_refresh:old"):
            self.assertEqual(menu.legacy_action(old_button), "home")
        self.assertEqual(menu.legacy_action("dagami_warp"), "warp")
        answers = []

        async def answer(text, **kwargs):
            answers.append((text, kwargs))

        bot = menu.DagamiMenu({}, object(), object())
        with patch.object(adapter, "pending", return_value=True):
            asyncio.run(bot.pair_prompt(types.SimpleNamespace(answer=answer), code))
        self.assertIn(code[-6:], answers[0][0])
        self.assertNotIn(code, answers[0][0])
        self.assertEqual(answers[0][1]["reply_markup"].inline_keyboard[0][0].callback_data, "dgp:" + code)


if __name__ == "__main__":
    unittest.main()
