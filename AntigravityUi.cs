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
            body.Controls.Add(FluentLabel("Вход и проверка Antigravity", 15, true), 0, 0);
            _prepareAntigravity = new CheckBox { Text = "Дополнительная бинарная подготовка совместимых версий", AutoSize = true, Checked = ConfigManager.Instance.PrepareAntigravityClient };
            body.Controls.Add(_prepareAntigravity, 0, 1);
            body.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted,
                Text = "Прокси и обычные ярлыки настраиваются независимо от совместимости бинарного патча. Неизвестная версия backend не изменяется.\nДля поддерживаемых версий сохраняются резервная копия и откат. Доступ аккаунта и моделей проверяет Google." }, 0, 2);
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(body); page.Controls.Add(card, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            AddNativeButton(actions, "Вход через прокси", OpenAntigravityLogin);
            AddNativeButton(actions, "Проверить", () => RunAntigravityOperation("diagnose"));
            var advanced = new ContextMenuStrip();
            advanced.Items.Add("Подготовить клиент", null, (sender, e) => ExecuteNativeAction(() => RunAntigravityOperation("prepare")));
            advanced.Items.Add("Отменить подготовку", null, (sender, e) => ExecuteNativeAction(() => RunAntigravityOperation("restore")));
            FluentButton advancedButton = null;
            AddNativeButton(actions, "Дополнительно…", () => advanced.Show(advancedButton, new Point(0, advancedButton.Height)));
            advancedButton = (FluentButton)_nativeButtons[_nativeButtons.Count - 1];
            Disposed += (sender, e) => advanced.Dispose();
            page.Controls.Add(actions, 0, 1);
            var reportCard = new FluentCard { Dock = DockStyle.Fill };
            _antigravityReport = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10),
                BackColor = FluentTheme.Surface, ForeColor = FluentTheme.Text, AccessibleName = "Отчёт проверки Antigravity",
                Text = "Диагностика проверяет POST /token с вымышленными данными, сигнатуры клиента, DNS и TLS через локальный мост.\r\n\r\nHTTP 400/401 в тесте OAuth ожидаем: Google отклоняет вымышленный код. Это проверка связи, вход в аккаунт нужно завершить в Antigravity.\r\n\r\nЗакрытие окна Dagami Agent Proxy оставляет мост в трее. Для полного завершения используйте «Выход» в меню значка." };
            reportCard.Controls.Add(_antigravityReport); page.Controls.Add(reportCard, 0, 2);
            return page;
        }

        private void RunAntigravityOperation(string operation)
        {
            AppTarget target = _cachedTargets.Find(x => x.IsInstalled && Path.GetFileName(x.ExePath).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase));
            if (target == null) throw new InvalidOperationException("Antigravity Manager не найден. Добавьте Antigravity.exe в список приложений.");
            if (operation == "diagnose") SaveNativeSettings();
            _nativeBusy = true; SetNativeButtonsEnabled(false);
            _antigravityReport.Text = "Выполняется проверка…";
            int bridgePort = _bridgePort;
            string dnsHost = ConfigManager.Instance.DnsHost;
            bool useDns = ConfigManager.Instance.UseDagamiDns;
            System.Threading.ThreadPool.QueueUserWorkItem(o => {
                var report = new StringBuilder();
                try {
                    if (operation == "prepare") report.AppendLine(AntigravityIntegration.Prepare(target.ExePath));
                    else if (operation == "restore") report.AppendLine(AntigravityIntegration.Restore(target.ExePath));
                    else {
                        try { report.AppendLine(AntigravityIntegration.Inspect(target.ExePath)); }
                        catch (Exception ex) { report.AppendLine("Клиент: " + ex.Message); }
                        try { report.AppendLine(AntigravityDiagnostics.ProbeToken(bridgePort)); }
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
