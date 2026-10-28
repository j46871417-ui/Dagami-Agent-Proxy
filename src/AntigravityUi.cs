using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DevProxyApp
{
    public partial class MainForm
    {
        private TextBox _antigravityReport;

        private Panel BuildAntigravityPage()
        {
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = FluentTheme.Background };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var card = new FluentCard { Dock = DockStyle.Fill };
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = FluentTheme.Surface };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.Controls.Add(FluentLabel("Патчинг и проверка Antigravity / agy CLI", 15, true), 0, 0);
            _prepareAntigravity = new CheckBox { Text = "Автоматически подготавливать совместимые версии", AutoSize = true, Checked = ConfigManager.Instance.PrepareAntigravityClient };
            body.Controls.Add(_prepareAntigravity, 0, 1);
            body.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted,
                Text = "Подготовка — «Пропатчить» в разделе «Приложения». Откат — «Ещё → Дополнительно».\nIDE 2.6 использует общий agy. Закройте IDE и CLI перед изменением.\nНеизвестные версии не меняются. Вход и ответы модели проверяйте в самом клиенте." }, 0, 2);
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(body); page.Controls.Add(card, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            AddNativeButton(actions, "Вход через прокси", OpenAntigravityLogin);
            AddNativeButton(actions, "Проверить", () => RunAntigravityOperation("diagnose"));
            ((FluentButton)_nativeButtons[_nativeButtons.Count - 1]).Primary = true;
            page.Controls.Add(actions, 0, 1);
            var reportCard = new FluentCard { Dock = DockStyle.Fill };
            _antigravityReport = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10),
                BackColor = FluentTheme.Surface, ForeColor = FluentTheme.Text, AccessibleName = "Отчёт проверки Antigravity",
                Text = "Проверка выполняет GET /oauth2/v2/userinfo без токена и POST /token с вымышленными данными. Проверяются HTTP-ответы внутри TLS, сигнатуры клиента и DNS.\r\n\r\nHTTP 401 для userinfo ожидаем без токена; 502 означает ошибку маршрута. При сбое userinfo сравниваются два маршрута без изменения настроек.\r\n\r\nВход аккаунта нужно завершить в Antigravity. Отчёт можно выделить и скопировать Ctrl+A, Ctrl+C." };
            reportCard.Controls.Add(_antigravityReport); page.Controls.Add(reportCard, 0, 2);
            return page;
        }

        private void RunAntigravityOperation(string operation)
        {
            AppTarget target = _targetList.SelectedItems.Count > 0 ? _targetList.SelectedItems[0].Tag as AppTarget : null;
            if (operation != "diagnose" && (target == null || !target.IsInstalled || !AntigravityIntegration.Supports(target.ExePath)))
                throw new InvalidOperationException("Выберите Antigravity или agy в списке приложений.");
            if (target == null || !target.IsInstalled || !AntigravityIntegration.Supports(target.ExePath))
                target = _cachedTargets.Find(x => x.IsInstalled && AntigravityIntegration.Supports(x.ExePath));
            if (target == null) throw new InvalidOperationException("Antigravity не найден. Добавьте Antigravity.exe, Antigravity IDE.exe или agy.exe в список приложений.");
            if (operation == "restore" && MessageBox.Show(this, "Вернуть оригинальный backend выбранного клиента из резервной копии? Если IDE использует общий agy, изменение затронет и CLI.",
                "Отмена подготовки", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (operation == "diagnose") SaveNativeSettings();
            _nativeBusy = true; SetNativeButtonsEnabled(false);
            _antigravityReport.Text = "Выполняется проверка…";
            int bridgePort = _bridgePort;
            string dnsHost = ConfigManager.Instance.DnsHost;
            bool useDns = ConfigManager.Instance.UseDagamiDns;
            string proxy = ConfigManager.Instance.LastProxy;
            System.Threading.ThreadPool.QueueUserWorkItem(o => {
                var report = new StringBuilder();
                try {
                    report.AppendLine("Выбран клиент: " + target.ExePath);
                    if (operation == "prepare") report.AppendLine(AntigravityIntegration.PrepareClient(target.ExePath).Message);
                    else if (operation == "restore") report.AppendLine(AntigravityIntegration.RestoreClient(target.ExePath));
                    else {
                        report.AppendLine(AntigravityDiagnostics.LoginRouteReport(bridgePort,useDns,dnsHost,proxy));
                        try { report.AppendLine(AntigravityIntegration.InspectClient(target.ExePath)); }
                        catch (Exception ex) { report.AppendLine("Клиент: " + ex.Message); }
                        try { report.AppendLine(AntigravityDiagnostics.TokenRouteReport(bridgePort,useDns,dnsHost,proxy)); }
                        catch (Exception ex) { report.AppendLine("OAuth POST /token: " + ex.GetType().Name + ": " + ex.Message); }
                        foreach (string host in new[] { "accounts.google.com", "lh3.googleusercontent.com", "gemini.google.com" }) {
                            try { report.AppendLine(AntigravityDiagnostics.ProbePublic(host, bridgePort)); }
                            catch (Exception ex) { report.AppendLine(host + ": " + ex.Message); }
                        }
                        foreach (string host in ScopedDnsResolver.Hosts) {
                            try {
                                if (useDns) report.AppendLine("DNS " + dnsHost + ": " + host + " → " + new ScopedDnsResolver(dnsHost).Resolve(host));
                                report.AppendLine(AntigravityDiagnostics.Probe(host, bridgePort));
                            } catch (Exception ex) { report.AppendLine(host + ": " + ex.Message); }
                        }
                        if (_rescueBridge != null) report.AppendLine(_rescueBridge.RouteReport());
                        report.AppendLine("Авторизация и ответ модели: не проверены. Завершите вход в Antigravity.");
                    }
                } catch (UnauthorizedAccessException) {
                    report.AppendLine("Недостаточно прав на установленный клиент. Запустите Dagami Agent Proxy от имени администратора для этой операции.");
                } catch (Exception ex) { report.AppendLine("Ошибка: " + ex.Message); }
                try { BeginInvoke(new Action(() => {
                    _nativeBusy = false; SetNativeButtonsEnabled(true); _antigravityReport.Text = report.ToString();
                    Logger.Log("[Antigravity] " + report.ToString()); UpdateNativeStatus();
                })); } catch (InvalidOperationException) { }
            });
        }
    }
}
