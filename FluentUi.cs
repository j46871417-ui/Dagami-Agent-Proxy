using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DevProxyApp
{
    internal static class FluentTheme
    {
        internal static readonly Color Background = Color.FromArgb(243, 245, 248);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Text = Color.FromArgb(29, 35, 44);
        internal static readonly Color Muted = Color.FromArgb(91, 101, 116);
        internal static readonly Color Border = Color.FromArgb(222, 228, 236);
        internal static readonly Color Accent = Color.FromArgb(0, 95, 184);

        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal class FluentLayout : TableLayoutPanel
    {
        public FluentLayout() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true); }
    }

    internal class FluentPageHost : Panel
    {
        public FluentPageHost() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true); }
    }

    internal class FluentAppList : ListView
    {
        public FluentAppList() { DoubleBuffered = true; }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Invalidate(); }
    }

    internal class FluentCard : Panel
    {
        public FluentCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(20);
            BackColor = FluentTheme.Surface;
            Margin = new Padding(0, 0, 0, 16);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var background = new SolidBrush(Parent == null ? FluentTheme.Background : Parent.BackColor))
                e.Graphics.FillRectangle(background, e.ClipRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 24 || Height < 24) return;
            using (var path = FluentTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (var brush = new SolidBrush(BackColor))
            using (var pen = new Pen(FluentTheme.Border)) {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal class FluentButton : Button
    {
        public bool Primary { get; set; }
        public bool NavigationSelected { get; set; }
        private bool _hover;
        private bool _pressed;

        public FluentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = FluentTheme.Surface;
            ForeColor = FluentTheme.Text;
            MinimumSize = new Size(0, 34);
            Padding = new Padding(14, 2, 14, 2);
            Margin = new Padding(0, 0, 8, 6);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var background = new SolidBrush(Parent == null ? FluentTheme.Background : Parent.BackColor))
                e.Graphics.FillRectangle(background, e.ClipRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? FluentTheme.Accent : NavigationSelected ? Color.FromArgb(226, 237, 249) : BackColor;
            if (_hover && Enabled) fill = Primary ? Color.FromArgb(0, 78, 154) : Color.FromArgb(235, 240, 247);
            if (_pressed && Enabled) fill = Primary ? Color.FromArgb(0, 66, 130) : Color.FromArgb(219, 229, 242);
            if (!Enabled) fill = Color.FromArgb(238, 240, 243);
            using (var path = FluentTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(Primary || NavigationSelected ? fill : FluentTheme.Border)) {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
            if (NavigationSelected) {
                using (var brush = new SolidBrush(FluentTheme.Accent)) e.Graphics.FillRectangle(brush, 2, 10, 3, Height - 20);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(12, 0, Width - 24, Height),
                !Enabled ? FluentTheme.Muted : Primary ? Color.White : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 8, Height - 8), FluentTheme.Text, fill);
        }
    }

    public partial class MainForm
    {
        private Panel _fluentPages;
        private Label _fluentHeading;
        private Label _fluentSubtitle;
        private Label _connectionSummary;
        private readonly List<FluentButton> _navigation = new List<FluentButton>();

        private Label FluentLabel(string text, float size, bool bold)
        {
            return new Label {
                Text = text, AutoSize = true, ForeColor = bold ? FluentTheme.Text : FluentTheme.Muted,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0, 0, 0, 8)
            };
        }

        private void BuildFluentUi()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
            Text = Brand.Name + " — приложения и подключения";
            Icon = Brand.LoadIcon();
            Font = new Font("Segoe UI", 10);
            ForeColor = FluentTheme.Text;
            BackColor = FluentTheme.Background;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            MaximizeBox = true;
            MinimumSize = new Size(940, 650);
            ClientSize = new Size(1040, 680);
            Opacity = 1;

            var shell = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = BackColor };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(shell);
            var sidebar = new FluentLayout {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(14, 18, 12, 12), BackColor = Color.FromArgb(235, 240, 246)
            };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            shell.Controls.Add(sidebar, 0, 0);
            var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            var brandIcon = new PictureBox { Location = new Point(0, 3), Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom, Image = Brand.LoadImage() };
            var brandHeader = new Panel { Width = 148, Height = 48, Margin = Padding.Empty };
            var brandName = FluentLabel("DAGAMI", 14, true); brandName.Location = new Point(42, 0);
            var brandSubtitle = FluentLabel("Agent Proxy", 9, false); brandSubtitle.Location = new Point(43, 25);
            brandHeader.Controls.Add(brandIcon); brandHeader.Controls.Add(brandName); brandHeader.Controls.Add(brandSubtitle);
            brand.Controls.Add(brandHeader);
            sidebar.Controls.Add(brand, 0, 0);
            var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            sidebar.Controls.Add(nav, 0, 1);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            _updateLink = new LinkLabel { Text = "Обновления\n"+Brand.ReleaseTag, Size = new Size(148,36), AutoSize=false, Font=new Font("Segoe UI",9), LinkColor=FluentTheme.Accent, AccessibleName="Обновления GitHub; установлено "+Brand.ReleaseTag, Margin=new Padding(0,0,0,8) };
            _updateLink.LinkClicked+=(sender,e)=>ExecuteNativeAction(OpenUpdates);
            footer.Controls.Add(_updateLink);
            AddNativeButton(footer, "Поддержать", () => OpenCommunityLink("https://pay.cloudtips.ru/p/671fda86"));
            var donate = _nativeButtons[_nativeButtons.Count - 1];
            donate.Tag = "https://pay.cloudtips.ru/p/671fda86"; donate.AccessibleName = "Поддержать проект — CloudTips";
            donate.Width = 148; donate.AutoSize = false; donate.Margin = new Padding(0, 0, 0, 8);
            foreach (var entry in new string[][] { new [] { "t.me/dagamvi", "https://t.me/dagamvi", "Паблик Dagamvi" }, new [] { "@bright26bot", "https://t.me/bright26bot", "Открыть бота bright26bot" } }) {
                string address = entry[1];
                var link = new LinkLabel { Text = entry[0], Tag = address, AccessibleName = entry[2], AutoSize = true,
                    LinkColor = FluentTheme.Accent, ActiveLinkColor = FluentTheme.Text, Margin = new Padding(0, 0, 0, 10) };
                link.LinkClicked += (sender, e) => ExecuteNativeAction(() => OpenCommunityLink(address));
                footer.Controls.Add(link);
            }
            var proxyOffer = FluentLabel("Получить прокси\nКодовое слово «DAGAMI»", 9, false);
            proxyOffer.AutoSize = false; proxyOffer.Size = new Size(148, 48);
            proxyOffer.AccessibleName = "Получить прокси. Кодовое слово DAGAMI";
            footer.Controls.Add(proxyOffer);
            sidebar.Controls.Add(footer, 0, 2);

            var main = new FluentLayout {
                Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3,
                BackColor = BackColor
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.Controls.Add(main, 1, 0);
            var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _fluentHeading = FluentLabel("Приложения", 19, true);
            _fluentSubtitle = FluentLabel("Запускайте рабочие инструменты через выбранное подключение.", 10, false);
            heading.Controls.Add(_fluentHeading);
            _fluentSubtitle.Visible = false;
            main.Controls.Add(heading, 0, 0);
            _fluentPages = new FluentPageHost { Dock = DockStyle.Fill, BackColor = BackColor };
            main.Controls.Add(_fluentPages, 0, 1);
            _nativeStatus = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9) };
            main.Controls.Add(_nativeStatus, 0, 2);

            var apps = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = BackColor };
            apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            apps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
            var connection = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 2), Margin = new Padding(0, 0, 0, 8) };
            _connectionSummary = FluentLabel("Подключение", 9, true);
            _connectionSummary.Dock = DockStyle.Fill;
            _connectionSummary.AutoSize = false;
            _connectionSummary.TextAlign = ContentAlignment.MiddleLeft;
            connection.Controls.Add(_connectionSummary);
            apps.Controls.Add(connection, 0, 0);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            AddNativeButton(tools, "Добавить", AddNativeTarget);
            AddNativeButton(tools, "Найти приложения", RefreshNativeTargets);
            apps.Controls.Add(tools, 0, 1);

            var listCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
            _targetList = new FluentAppList {
                Dock = DockStyle.Fill, View = View.Details, CheckBoxes = false, BorderStyle = BorderStyle.None,
                FullRowSelect = true, HideSelection = false, MultiSelect = false, BackColor = FluentTheme.Surface,
                ForeColor = FluentTheme.Text, ShowItemToolTips = true,
                AccessibleName = "Выберите приложение. Все действия ниже относятся к выбранной строке."
            };
            _targetList.Columns.Add("Приложение", 205);
            _targetList.Columns.Add("Наличие", 100);
            _targetList.Columns.Add("Расположение", 600);
            _targetList.SmallImageList = new ImageList { ImageSize = new Size(1, 28) };
            _targetList.SizeChanged += (sender, e) => FitNativeListColumns();
            _targetList.HandleCreated += (sender, e) => FitNativeListColumns();
            _targetList.SelectedIndexChanged += (sender, e) => {
                if (_appFeedback != null) _appFeedback.Text = _proxyCheckMessage ?? "«Запустить» — разово. «Пропатчить» — для привычных ярлыков.";
                UpdateNativeDetails();
            };
            listCard.Controls.Add(_targetList);
            apps.Controls.Add(listCard, 0, 2);
            var actionCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), Margin = Padding.Empty };
            var actionRows = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = FluentTheme.Surface };
            actionRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 28, 42, 25, 29 }) actionRows.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            actionRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var selectedHeader = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            selectedHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            selectedHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            selectedHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _selectedAppHeading = FluentLabel("Выберите приложение", 13, true);
            _selectedAppHeading.AutoSize = false; _selectedAppHeading.Dock = DockStyle.Fill; _selectedAppHeading.Margin = Padding.Empty;
            _targetDetails = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Font = new Font("Segoe UI", 9), TextAlign = ContentAlignment.MiddleRight, Margin = Padding.Empty };
            selectedHeader.Controls.Add(_selectedAppHeading, 0, 0); selectedHeader.Controls.Add(_targetDetails, 1, 0);
            actionRows.Controls.Add(selectedHeader, 0, 0);
            var launch = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            AddNativeButton(launch, "Запустить", RunPrimaryNativeAction);
            _primaryAppAction = (FluentButton)_nativeButtons[_nativeButtons.Count - 1];
            _primaryAppAction.Primary = true; _primaryAppAction.AutoSize = false; _primaryAppAction.Width = 130;
            AddNativeButton(launch, "Пропатчить", PatchSelectedNativeTarget);
            _patchAppAction = (FluentButton)_nativeButtons[_nativeButtons.Count - 1];
            BuildAppActionsMenu();
            AddNativeButton(launch, "Ещё…", () => _appActions.Show(_moreAppActions, new Point(0, _moreAppActions.Height)));
            _moreAppActions = (FluentButton)_nativeButtons[_nativeButtons.Count - 1];
            actionRows.Controls.Add(launch, 0, 1);
            _shortcutToggle = new CheckBox { Text = "Запускать через прокси из обычного ярлыка", AutoSize = true,
                AccessibleName = "Запускать выбранное приложение через прокси из обычного ярлыка", Margin = new Padding(0, 2, 0, 0) };
            _shortcutToggle.CheckedChanged += (sender, e) => ToggleNativeShortcuts();
            actionRows.Controls.Add(_shortcutToggle, 0, 2);
            _shortcutExplanation = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Font = new Font("Segoe UI", 9), Margin = Padding.Empty };
            actionRows.Controls.Add(_shortcutExplanation, 0, 3);
            _appFeedback = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Font = new Font("Segoe UI", 9), Margin = Padding.Empty,
                Text = "«Запустить» — разово. «Пропатчить» — для привычных ярлыков." };
            actionRows.Controls.Add(_appFeedback, 0, 4);
            actionCard.Controls.Add(actionRows); apps.Controls.Add(actionCard, 0, 3);

            var settingsPage = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = BackColor };
            settingsPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var settingsCard = new FluentCard { Dock = DockStyle.Fill };
            var settings = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, BackColor = FluentTheme.Surface };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            foreach (int height in new int[] { 35, 25, 42, 32, 42, 40 }) settings.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            settings.Controls.Add(FluentLabel("Прокси и DNS", 15, true), 0, 0);
            settings.Controls.Add(FluentLabel("Адрес HTTP / HTTPS прокси", 9, false), 0, 1);
            _proxyBox = new TextBox { Text = ConfigManager.Instance.LastProxy, UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, AccessibleName = "Адрес HTTP или HTTPS прокси" };
            settings.Controls.Add(_proxyBox, 0, 2);
            var reveal = new CheckBox { Text = "Показать адрес", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(14, 3, 0, 0) };
            reveal.CheckedChanged += (s, e) => _proxyBox.UseSystemPasswordChar = !reveal.Checked; settings.Controls.Add(reveal, 1, 2);
            _dnsEnabled = new CheckBox { Text = "Smart DNS для входа Google, Antigravity и Gemini", Checked = ConfigManager.Instance.UseDagamiDns, AutoSize = true };
            settings.Controls.Add(_dnsEnabled, 0, 3);
            _dnsBox = new TextBox { Text = ConfigManager.Instance.DnsHost, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Имя DNS сервера", Enabled = _dnsEnabled.Checked };
            _dnsEnabled.CheckedChanged += (s, e) => _dnsBox.Enabled = _dnsEnabled.Checked; settings.Controls.Add(_dnsBox, 0, 4);
            var settingsActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            settings.Controls.Add(settingsActions, 0, 5); settings.SetColumnSpan(settingsActions, 2);
            AddNativeButton(settingsActions, "Сохранить", () => { SaveNativeSettings(); Logger.Log("[✓] Настройки сохранены."); });
            ((FluentButton)_nativeButtons[_nativeButtons.Count - 1]).Primary = true;
            AddNativeButton(settingsActions, "Проверить прокси", CheckFluentProxy);
            AddNativeButton(settingsActions, "Записать DNS в систему…", () => { using (var dialog = new SystemDnsForm(_dnsBox.Text.Trim())) dialog.ShowDialog(this); UpdateNativeStatus(); });
            settingsCard.Controls.Add(settings); settingsPage.Controls.Add(settingsCard, 0, 0);
            var behaviorCard = new FluentCard { Dock = DockStyle.Fill };
            var behavior = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            behavior.Controls.Add(FluentLabel("Постоянные настройки", 13, true));
            _systemEnvironment = new CheckBox { Text = "Записывать переменные прокси пользователя Windows", AutoSize = true, Checked = ConfigManager.Instance.SetSystemEnv };
            behavior.Controls.Add(_systemEnvironment); behavior.Controls.Add(FluentLabel("Применяются через «Ещё → Дополнительно → Записать настройки».", 9, false));
            behaviorCard.Controls.Add(behavior); settingsPage.Controls.Add(behaviorCard, 0, 1);
            var note = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Text = "Smart DNS: вход Google, OAuth, аватар, Gemini и API Antigravity. Остальные адреса — через прокси; TLS проверяется.\nСистемный DNS: «Записать DNS в систему…» → подтвердить запись и UAC. Есть резервная копия и откат.", Padding = new Padding(2, 4, 2, 0) };
            settingsPage.Controls.Add(note, 0, 2);

            var logCard = new FluentCard { Dock = DockStyle.Fill };
            _nativeLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, WordWrap = true, BackColor = FluentTheme.Surface, ForeColor = FluentTheme.Text,
                Font = new Font("Consolas", 10), AccessibleName = "Журнал Dagami Agent Proxy" };
            logCard.Controls.Add(_nativeLog);
            Panel antigravityPage = BuildAntigravityPage();
            Panel[] pages = new Panel[] { apps, settingsPage, antigravityPage, logCard };
            string[] names = new string[] { "Приложения", "Подключение", "Antigravity", "Журнал" };
            string[] descriptions = new string[] {
                "Запускайте рабочие инструменты через выбранное подключение.",
                "Настройте прокси и DNS для приложений и управляемых ярлыков.",
                "Вход в аккаунт и дополнительные средства проверки клиента.",
                "События запуска, состояние подключения и сообщения приложения."
            };
            for (int i = 0; i < pages.Length; i++) {
                int pageIndex = i; pages[i].Visible = i == 0; _fluentPages.Controls.Add(pages[i]);
                var button = new FluentButton { Text = names[i], Width = 148, Height = 36, Margin = new Padding(0, 0, 0, 6), NavigationSelected = i == 0, BackColor = sidebar.BackColor };
                button.Click += (s, e) => ShowFluentPage(pageIndex);
                nav.Controls.Add(button); _navigation.Add(button);
            }
            _pageControls = pages; _pageNames = names; _pageDescriptions = descriptions;
            apps.BringToFront();
            _proxyBox.TextChanged += (sender, e) => { _proxyCheckFailed = false; _proxyCheckMessage = null; UpdateNativeStatus(); };
            _nativeLogTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _nativeLogTimer.Tick += (s, e) => {
                List<string> messages; lock (_logLock) { messages = new List<string>(_logQueue); _logQueue.Clear(); }
                if (_nativeLog.TextLength > 100000) _nativeLog.Clear();
                foreach (string message in messages) _nativeLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
                if (!_nativeBusy) {
                    if (++_shortcutRefreshTicks % 10 == 0) UpdateNativeDetails();
                    UpdateNativeStatus();
                }
            };
            Resize += (sender, e) => {
                if (WindowState != FormWindowState.Minimized) _nativeRestoreState = WindowState;
            };
            _nativeLogTimer.Start(); RefreshNativeTargets();
        }

        private void CheckFluentProxy()
        {
            SaveNativeSettings(); string proxy = ConfigManager.Instance.LastProxy;
            _nativeBusy = true; SetNativeButtonsEnabled(false); _nativeStatus.Text = "Проверка TCP-соединения…";
            if (_primaryAppAction != null) _primaryAppAction.Text = "Проверка…";
            System.Threading.ThreadPool.QueueUserWorkItem(o => {
                bool available = CheckProxyConnection(proxy);
                try { BeginInvoke(new Action(() => {
                    _nativeBusy = false; SetNativeButtonsEnabled(true);
                    _proxyCheckFailed = !available;
                    _proxyCheckMessage = available ? "Прокси доступен по TCP. Вход в аккаунт и модели не проверены." :
                        "Прокси недоступен. Повторите проверку или измените адрес в разделе «Подключение».";
                    if (_appFeedback != null) _appFeedback.Text = _proxyCheckMessage;
                    Logger.Log(available ? "[✓] Прокси доступен по TCP." : "[-] Прокси недоступен по TCP."); UpdateNativeStatus();
                })); } catch (InvalidOperationException) { }
            });
        }

        private void OpenCommunityLink(string address)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
        }
    }
}
