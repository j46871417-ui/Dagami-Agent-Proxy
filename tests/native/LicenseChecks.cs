using System;
using System.Drawing;
using System.Windows.Forms;
using DevProxyApp;

class LicenseChecks {
    static int checks;
    static void Assert(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }

    [STAThread]
    static int Main() {
        try {
            // 1. Checksum consistency
            Assert(LicenseManager.ComputeChecksum("TEST9999") == "XVSF", "ComputeChecksum mismatch");

            // 2. Key validation
            Assert(LicenseManager.ValidateKey("DAGAMI-TEST-9999-XVSF"), "Valid key failed validation");
            Assert(LicenseManager.ValidateKey("dagami-test-9999-xvsf"), "Lowercase key failed validation");
            Assert(LicenseManager.ValidateKey("DAGAMI TEST 9999 XVSF"), "Key with spaces failed validation");
            Assert(LicenseManager.ValidateKey("DAGAMITEST9999XVSF"), "Unformatted key failed validation");

            // 3. Rejection of invalid keys
            Assert(!LicenseManager.ValidateKey(""), "Empty key accepted");
            Assert(!LicenseManager.ValidateKey(null), "Null key accepted");
            Assert(!LicenseManager.ValidateKey("DAGAMI-TEST-9999-XXXX"), "Corrupt checksum accepted");
            Assert(!LicenseManager.ValidateKey("OTHER-TEST-9999-XVSF"), "Wrong prefix accepted");
            Assert(!LicenseManager.ValidateKey("DAGAMI-01OI-9999-XVSF"), "Disallowed chars accepted");

            // Construct the form without showing it or submitting a key.
            Application.EnableVisualStyles();
            using (var form = new ActivationForm()) {
                Assert(form.ClientSize.Width >= 500 && form.ClientSize.Height >= 360, "ActivationForm size too small");
                Assert(form.Controls.Count >= 5, "ActivationForm controls missing");
                Assert(form.Text.Contains("Активация"), "Form title missing activation label");
            }

            Console.WriteLine("PASS " + checks + " license validation and ActivationForm UI checks; no license store writes");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
