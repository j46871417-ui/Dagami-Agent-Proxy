import unittest
from generate_license_key import generate_key, validate_key, compute_checksum, ALPHABET


class TestLicenseKey(unittest.TestCase):
    def test_generate_and_validate(self):
        for _ in range(20):
            key = generate_key()
            self.assertTrue(validate_key(key), f"Generated key {key} must be valid")
            self.assertTrue(key.startswith("DAGAMI-"))
            parts = key.split("-")
            self.assertEqual(len(parts), 4)
            self.assertEqual(len(parts[1]), 4)
            self.assertEqual(len(parts[2]), 4)
            self.assertEqual(len(parts[3]), 4)

    def test_checksum_consistency(self):
        payload = "TEST9999"
        chk = compute_checksum(payload)
        self.assertEqual(chk, "XVSF")
        self.assertTrue(validate_key(f"DAGAMI-TEST-9999-{chk}"))

    def test_case_and_formatting_insensitivity(self):
        key = "DAGAMI-TEST-9999-XVSF"
        self.assertTrue(validate_key(key.lower()))
        self.assertTrue(validate_key("dagami test 9999 xvsf"))
        self.assertTrue(validate_key("DAGAMITEST9999XVSF"))

    def test_invalid_keys(self):
        self.assertFalse(validate_key(""))
        self.assertFalse(validate_key(None))
        self.assertFalse(validate_key("NOTDAGAMI-TEST-9999-XVSF"))
        self.assertFalse(validate_key("DAGAMI-TEST-9999-XXXX"))  # Corrupted checksum
        self.assertFalse(validate_key("DAGAMI-01OI-9999-XVSF"))  # Characters 0, 1, O, I not in alphabet
        self.assertFalse(validate_key("DAGAMI-TOO-SHORT"))


if __name__ == "__main__":
    unittest.main()
