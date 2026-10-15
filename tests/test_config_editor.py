import unittest
import tempfile
import os
import json
from devproxy_pkg.core.config_editor import ConfigEditor, strip_json_comments


class TestConfigEditor(unittest.TestCase):
    def test_strip_comments(self):
        text = '''{
            // This is a comment
            "http.proxy": "http://example.com:8080", /* inline comment */
            "key": "val // not comment"
        }'''
        cleaned = strip_json_comments(text)
        data = json.loads(cleaned)
        self.assertEqual(data["http.proxy"], "http://example.com:8080")
        self.assertEqual(data["key"], "val // not comment")

    def test_update_jsonc_preserves_unrelated_values(self):
        original = '''{
            // Important setting
            "editor.fontSize": 14,
            "http.proxy": "http://old:8080"
        }'''
        updates = {"http.proxy": "http://new:9090", "http.proxySupport": "on"}
        result = ConfigEditor.update_json_fields_preserving(original, updates)
        self.assertEqual(json.loads(result)["editor.fontSize"],14)
        self.assertIn('"http.proxy": "http://new:9090"', result)
        self.assertIn('"http.proxySupport": "on"', result)

    def test_remove_json_fields(self):
        original = '''{
            "editor.fontSize": 14,
            "http.proxy": "http://old:8080",
            "http.proxySupport": "on"
        }'''
        result = ConfigEditor.remove_json_fields_preserving(original, ["http.proxy", "http.proxySupport"])
        self.assertNotIn("http.proxy", result)
        self.assertIn('"editor.fontSize": 14', result)


if __name__ == "__main__":
    unittest.main()
