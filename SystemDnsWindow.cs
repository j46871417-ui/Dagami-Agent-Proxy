using System;
using System.Windows.Forms;

namespace DevProxyApp
{
    // A direct entry point for DNS maintenance. It deliberately bypasses the
    // main app's per-user broker, which may still belong to an older build.
    static class SystemDnsWindow
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0) {
                try {
                    if (args.Length != 3 || args[0] != "--system-dns-helper") throw new ArgumentException("Invalid DNS helper request.");
                    Environment.ExitCode = SystemDnsManager.RunElevatedHelper(args[1], args[2]);
                } catch { Environment.ExitCode = 1; }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SystemDnsForm()) {
                form.Text += " — up7: полный сброс DNS";
                Application.Run(form);
            }
        }
    }
}
