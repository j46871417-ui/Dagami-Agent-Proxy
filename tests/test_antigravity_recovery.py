import os
import tempfile
import unittest
from devproxy_pkg.core.antigravity_recovery import ASARRecoveryManager


class TestASARRecoveryManager(unittest.TestCase):
    def test_writable_check(self):
        with tempfile.NamedTemporaryFile(delete=False) as tf:
            path = tf.name
        try:
            self.assertTrue(ASARRecoveryManager.is_writable(path))
        finally:
            if os.path.exists(path):
                os.remove(path)

    def test_evaluate_injection_strategy_system_path(self):
        eval_res = ASARRecoveryManager.evaluate_injection_strategy(
            "C:\\Program Files\\Antigravity\\Antigravity.exe",
            ["C:\\Program Files\\Antigravity\\resources\\app.asar"]
        )
        self.assertEqual(eval_res["strategy"], "env_only_fallback")
        self.assertFalse(eval_res["requires_uac"])
        self.assertIn("--proxy-server={PROXY_URL}", eval_res["safe_cli_flags"])

    def test_evaluate_injection_strategy_user_path(self):
        with tempfile.NamedTemporaryFile(delete=False) as tf:
            conf_path = tf.name
        try:
            user_exe = os.path.join(tempfile.gettempdir(), "test_app.exe")
            eval_res = ASARRecoveryManager.evaluate_injection_strategy(
                user_exe,
                [conf_path]
            )
            # In user temp directory, if writable and not in Program Files:
            self.assertEqual(eval_res["strategy"], "standard_persistent")
        finally:
            if os.path.exists(conf_path):
                os.remove(conf_path)


if __name__ == "__main__":
    unittest.main()
