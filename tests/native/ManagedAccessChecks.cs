using System;
using System.Drawing;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevProxyApp;

class ManagedAccessChecks {
    static Form ShowPairDialog(string endpoint, bool checkOnOpen,
        Func<Uri,bool,Task<Tuple<ManagedAccess,ManagedAccess.Result>>> request, Action<string> openLink) {
        var type = typeof(ManagedAccess).Assembly.GetType("DevProxyApp.ManagedPairDialog", true);
        var form = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { endpoint, checkOnOpen, request, openLink }, null);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
        Application.DoEvents();
        return form;
    }

    static Control Find(Form form, string name) { return form.Controls.Find(name, true)[0]; }

    static void Click(Form form, string name) {
        var button = (Button)Find(form, name);
        if (!button.Visible || !button.Enabled || !form.ClientRectangle.Contains(form.PointToClient(button.PointToScreen(new Point(button.Width - 1, button.Height - 1)))))
            throw new Exception("Pairing button is not usable: " + name);
        button.PerformClick();
        Application.DoEvents();
    }

    static void CheckStatus(Form form, string expected) {
        var status = Find(form, "ManagedPairStatus");
        if (!status.Visible || status.Height < status.Font.Height * 2 || !status.Text.Contains(expected))
            throw new Exception("Expected visible pairing status: " + expected + "; got " + status.Text);
    }

    static void CheckPairingDialog() {
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        string opened = null;
        int requests = 0;
        string code = new string('A',37) + "ABC123";
        Func<Uri,bool,Task<Tuple<ManagedAccess,ManagedAccess.Result>>> gateway = (address, begin) => {
            requests++;
            if (address.Host != "gateway.test") throw new Exception("Incorrect gateway");
            return Task.FromResult(Tuple.Create((ManagedAccess)null, new ManagedAccess.Result { code = begin ? code : null }));
        };
        using (var form = ShowPairDialog("", true, gateway, address => opened = address)) {
            CheckStatus(form, "для проверки привязки");
            Click(form, "OpenTelegramButton");
            if (opened != ManagedAccess.BotChat || requests != 0) throw new Exception("Opening the bot requires a configured gateway");
            CheckStatus(form, "Чат Telegram открыт");
            Find(form, "ManagedGatewayEndpoint").Text = "https://gateway.test";
            Click(form, "OpenTelegramButton");
            if (opened != ManagedAccess.BotLink + code) throw new Exception("The device approval link was not opened");
            CheckStatus(form, "ABC123");
            Click(form, "CheckManagedPairButton");
            CheckStatus(form, "Подтверждение ещё не получено");
            if (requests != 2) throw new Exception("Unexpected number of gateway requests");
        }
        using (var form = ShowPairDialog("https://gateway.test", true,
            (address, begin) => { throw new WebException("Test gateway timed out.", WebExceptionStatus.Timeout); },
            address => { throw new System.ComponentModel.Win32Exception(); })) {
            CheckStatus(form, "Не удалось связаться со шлюзом");
            Find(form, "ManagedGatewayEndpoint").Text = "";
            Click(form, "OpenTelegramButton");
            CheckStatus(form, ManagedAccess.BotChat);
        }
        var pending = new TaskCompletionSource<Tuple<ManagedAccess,ManagedAccess.Result>>();
        using (var form = ShowPairDialog("https://gateway.test", false, (address, begin) => pending.Task, address => opened = address)) {
            opened = null;
            Click(form, "OpenTelegramButton");
            CheckStatus(form, "Запрашиваем код привязки");
            form.Close();
            pending.SetResult(Tuple.Create((ManagedAccess)null, new ManagedAccess.Result { code = code }));
            Application.DoEvents();
            if (opened != null) throw new Exception("Telegram opened after the pairing dialog closed");
        }
    }

    [STAThread] static int Main(string[] args) {
        try {
            string key = ManagedAccess.CreateKey();
            if (args.Length == 3 && args[0] == "proof") {
                Console.WriteLine(ManagedAccess.Sign(key, args[1], "CONNECT", args[2]));
                return 0;
            }
            string a = ManagedAccess.Sign(key, "gateway.test", "CONNECT", "example.test:443");
            string b = ManagedAccess.Sign(key, "gateway.test", "CONNECT", "example.test:443");
            if (!a.StartsWith("Dagami v1.") || a == b || a.Split('.')[1].Length != 87 || a.Split('.')[4].Length != 86)
                throw new Exception("Incorrect proof encoding or nonce reuse.");
            try { ManagedAccess.ParseEndpoint("https://user:password@example.test"); throw new Exception("Accepted credentials in endpoint."); }
            catch (ArgumentException) { }
            CheckPairingDialog();
            Console.WriteLine("PASS managed P-256 signatures, endpoint validation, Telegram and pairing button feedback, timeout and close");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
