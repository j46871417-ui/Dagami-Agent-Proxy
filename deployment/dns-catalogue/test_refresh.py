import contextlib
from datetime import datetime, timedelta, timezone
import io
import struct
import unittest

from refresh import QUESTION, definitions, has_answer, refresh


class CatalogueTests(unittest.TestCase):
    def test_fresh_addresses_replace_stale_and_removal_is_respected(self):
        provider = {"Id": "fixture", "Name": "Test", "Url": "https://dns.fixture.net/query", "Addresses": []}
        old = dict(provider, Addresses=["8.8.4.4"])
        now = datetime.now(timezone.utc)
        seen = []

        def verify(_, address):
            seen.append(address)
            return address == "1.1.1.1"

        with contextlib.redirect_stdout(io.StringIO()):
            providers = refresh([provider], {("fixture", provider["Url"]): old}, now,
                                lambda _: ["127.0.0.1", "1.1.1.1", "1.1.1.1", "8.8.8.8"], verify)
            empty = refresh([], {("fixture", provider["Url"]): old}, now, lambda _: [], verify)
        self.assertCountEqual(seen, ["1.1.1.1", "8.8.8.8"])
        self.assertEqual(providers["Providers"][0]["Addresses"], ["1.1.1.1"])
        self.assertEqual(empty["Providers"], [])

    def test_last_verified_addresses_expire(self):
        provider = {"Id": "fixture", "Name": "Test", "Url": "tls://dns.fixture.net", "Addresses": ["1.1.1.1"]}
        now = datetime.now(timezone.utc)
        key = (provider["Id"], provider["Url"])
        for age, expected in [(1, ["1.1.1.1"]), (25, [])]:
            old = dict(provider, VerifiedAt=(now - timedelta(hours=age)).isoformat())
            with contextlib.redirect_stdout(io.StringIO()):
                result = refresh([provider], {key: old}, now, lambda _: [], lambda url, ip: False)
            self.assertEqual(result["Providers"][0]["Addresses"], expected)

    def test_rejects_private_addresses_and_duplicate_doh_authority(self):
        provider = {"Id": "fixture", "Name": "Test", "Url": "https://dns.fixture.net/query", "Addresses": ["8.8.8.8"]}
        self.assertEqual(definitions({"Version": 1, "Providers": [provider]}), [provider])
        for extra in (dict(provider, Id="other", Url="https://dns.fixture.net:443/query"),
                      dict(provider, Id="local", Url="https://172.16.0.1/query"),
                      dict(provider, Id="bad", Url="tls://dns.bad.net", Addresses=["127.0.0.1"])):
            with self.assertRaises(ValueError):
                definitions({"Version": 1, "Providers": [provider, extra]})

    def test_good_dns_reply_requires_a_matching_query_and_answer(self):
        query = struct.pack("!6H", 12, 0x0100, 1, 0, 0, 0) + QUESTION
        header = struct.pack("!6H", 12, 0x8180, 1, 1, 0, 0)
        record = struct.pack("!HHHIH", 0xC00C, 1, 1, 60, 4)
        self.assertTrue(has_answer(header + QUESTION + record + b"\x01\x01\x01\x01", query))
        self.assertFalse(has_answer(header + QUESTION + record + b"\x7f\x00\x00\x01", query))
        self.assertFalse(has_answer(bytes([0, 13]) + header[2:] + QUESTION + record + b"\x01\x01\x01\x01", query))


if __name__ == "__main__":
    unittest.main()
