import os
import json
import socket
import tempfile
import unittest
from devproxy_pkg.core.agent_ipc import EphemeralAgentIPC


class TestAgentIPC(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp()
        self.ipc = EphemeralAgentIPC(key_dir=self.tmp_dir)

    def tearDown(self):
        self.ipc.stop()
        if os.path.exists(self.tmp_dir):
            try:
                os.rmdir(self.tmp_dir)
            except Exception:
                pass

    def test_ipc_lifecycle_and_auth(self):
        port = self.ipc.start()
        self.assertGreater(port, 0)
        self.assertTrue(os.path.exists(self.ipc.key_file))

        # Test unauthorized request
        s = socket.create_connection(("127.0.0.1", port), timeout=2.0)
        s.sendall((json.dumps({"command": "ping", "auth": "wrong"})+"\n").encode("utf-8"))
        resp = json.loads(s.recv(4096).decode("utf-8"))
        s.close()
        self.assertEqual(resp.get("status"), "error")
        self.assertIn("Unauthorized", resp.get("message", ""))

        # Test authorized ping request
        s2 = socket.create_connection(("127.0.0.1", port), timeout=2.0)
        s2.sendall((json.dumps({"command": "ping", "auth": self.ipc.secret_token})+"\n").encode("utf-8"))
        resp2 = json.loads(s2.recv(4096).decode("utf-8"))
        s2.close()
        self.assertEqual(resp2.get("status"), "ok")
        self.assertTrue(resp2.get("result", {}).get("pong"))

        # Stop and verify key file cleanup
        self.ipc.stop()
        self.assertFalse(os.path.exists(self.ipc.key_file))


if __name__ == "__main__":
    unittest.main()
