using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DevProxyApp
{
    internal static class FluentTheme
    {
        internal static readonly Color Background = Color.FromArgb(245, 246, 248);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Text = Color.FromArgb(30, 32, 38);
        internal static readonly Color Muted = Color.FromArgb(100, 106, 117);
        internal static readonly Color Border = Color.FromArgb(228, 230, 235);
        internal static readonly Color Accent = Color.FromArgb(30, 107, 235);

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
            using (var path = FluentTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 18))
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
        public int NavigationGlyph = -1;
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
            Color fill = Primary ? FluentTheme.Accent : NavigationSelected ? Color.FromArgb(225, 235, 252) : BackColor;
            if (_hover && Enabled) fill = Primary ? Color.FromArgb(24, 91, 213) : Color.FromArgb(235, 240, 247);
            if (_pressed && Enabled) fill = Primary ? Color.FromArgb(20, 76, 180) : Color.FromArgb(219, 229, 242);
            if (!Enabled) fill = Color.FromArgb(238, 240, 243);
            using (var path = FluentTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(Primary || NavigationSelected || NavigationGlyph >= 0 ? fill : FluentTheme.Border)) {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
            if (NavigationGlyph >= 0) MacGlyph.Draw(e.Graphics, NavigationGlyph, new Rectangle(10, (Height - 25) / 2, 25, 25));
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(NavigationGlyph >= 0 ? 45 : 12, 0, Width - (NavigationGlyph >= 0 ? 49 : 24), Height),
                !Enabled ? FluentTheme.Muted : Primary ? Color.White : ForeColor,
                (NavigationGlyph >= 0 ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 8, Height - 8), FluentTheme.Text, fill);
        }
    }

    // Original vector marks: keep icon edges sharp at Windows display scales.
    internal static class MacGlyph
    {
        internal static void Draw(Graphics g, int kind, Rectangle box)
        {
            Color color = kind == 0 ? FluentTheme.Accent : kind == 2 ? Color.FromArgb(24, 141, 146) : kind == 3 ? Color.FromArgb(191, 118, 32) : Color.FromArgb(112, 119, 132);
            using (var path = FluentTheme.Rounded(box, 7))
            using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
            var saved = g.Save(); g.TranslateTransform(box.Left, box.Top); g.ScaleTransform(box.Width / 25f, box.Height / 25f);
            using (var pen = new Pen(Color.White, 1.6f)) {
                pen.StartCap = pen.EndCap = LineCap.Round;
                if (kind == 0) {
                    g.DrawLines(pen, new[] { new Point(6, 12), new Point(12, 6), new Point(19, 12) });
                    g.DrawLines(pen, new[] { new Point(8, 11), new Point(8, 19), new Point(17, 19), new Point(17, 11) });
                } else if (kind == 2) {
                    g.DrawEllipse(pen, 6, 6, 13, 13); g.DrawEllipse(pen, 10, 6, 5, 13); g.DrawLine(pen, 6, 12, 19, 12);
                } else if (kind == 3) {
                    g.DrawRectangle(pen, 7, 6, 11, 14); g.DrawLine(pen, 10, 10, 15, 10); g.DrawLine(pen, 10, 13, 15, 13); g.DrawLine(pen, 10, 16, 13, 16);
                } else {
                    for (int y = 7; y <= 19; y += 6) g.DrawLine(pen, 6, y, 19, y);
                    g.DrawEllipse(pen, 9, 5, 4, 4); g.DrawEllipse(pen, 14, 11, 4, 4); g.DrawEllipse(pen, 8, 17, 4, 4);
                }
            }
            g.Restore(saved);
        }
    }

    internal class FluentHero : Panel
    {
        public FluentHero() { DoubleBuffered = true; BackColor = Color.FromArgb(36, 102, 231); SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(Parent == null ? FluentTheme.Background : Parent.BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
            if (Width < 44 || Height < 44) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = FluentTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 22))
            using (var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(30, 110, 242), Color.FromArgb(67, 80, 210), 20f)) e.Graphics.FillPath(brush, path);
        }
    }

    public partial class MainForm
    {
        private void StyleMacAppList(ListView list)
        {
            var icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
            list.OwnerDraw = true;
            list.DrawColumnHeader += (sender, e) => {
                using (var brush = new SolidBrush(FluentTheme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, Rectangle.Inflate(e.Bounds, -12, 0), FluentTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            list.DrawItem += (sender, e) => { if (list.View != View.Details) e.DrawDefault = true; };
            list.DrawSubItem += (sender, e) => {
                bool selected = e.Item.Selected;
                using (var brush = new SolidBrush(selected ? Color.FromArgb(231, 239, 253) : FluentTheme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                using (var pen = new Pen(Color.FromArgb(240, 241, 244))) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var bounds = Rectangle.Inflate(e.Bounds, -12, 0);
                if (e.ColumnIndex == 0) {
                    var target = e.Item.Tag as AppTarget;
                    string executable = target == null ? "" : target.ExePath ?? "";
                    Image artwork;
                    if (!icons.TryGetValue(executable, out artwork)) {
                        artwork = null;
                        try { if (File.Exists(executable)) using (var icon = Icon.ExtractAssociatedIcon(executable)) { if (icon != null) artwork = icon.ToBitmap(); } } catch (Exception) { }
                        icons[executable] = artwork;
                    }
                    var tile = new Rectangle(bounds.Left, bounds.Top + (bounds.Height - 30) / 2, 30, 30);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var shape = FluentTheme.Rounded(tile, 9))
                    using (var brush = new SolidBrush(Color.FromArgb(239, 242, 248))) e.Graphics.FillPath(brush, shape);
                    if (artwork != null) e.Graphics.DrawImage(artwork, Rectangle.Inflate(tile, -4, -4));
                    else TextRenderer.DrawText(e.Graphics, e.Item.Text.Length == 0 ? "A" : e.Item.Text.Substring(0, 1), list.Font, tile, FluentTheme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    bounds.X += 42; bounds.Width -= 42;
                    using (var font = new Font(list.Font, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, bounds, FluentTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    if (selected && list.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Item.Bounds, -2, -2));
                } else TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, bounds, FluentTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            list.Disposed += (sender, e) => { foreach (var image in icons.Values) if (image != null) image.Dispose(); };
        }
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
                Margin = new Padding(0, 0, 0, 2)
            };
        }

        private void OpenBugReport()
        {
            using (var dialog = new BugReportDialog()) dialog.ShowDialog(this);
        }

        private void SaveJournalToFile()
        {
            using (var picker = new SaveFileDialog {
                Title = "Сохранить журнал", Filter = "Журнал (*.log)|*.log|Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
                DefaultExt = "log", AddExtension = true, OverwritePrompt = true, RestoreDirectory = true,
                FileName = "DagamiAgentProxy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"
            }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try {
                    Logger.SaveToFile(picker.FileName, _nativeLog.Text);
                    MessageBox.Show(this, "Журнал сохранён.", "Журнал", MessageBoxButtons.OK, MessageBoxIcon.Information);
                } catch (Exception ex) {
                    MessageBox.Show(this, "Не удалось сохранить журнал: " + ex.Message, "Журнал", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
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
            MinimumSize = new Size(1000, 740);
            ClientSize = new Size(1120, 780);
            Opacity = 1;

            var shell = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = BackColor };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 198));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(shell);
            var sidebar = new FluentLayout {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(14, 28, 12, 16), BackColor = Color.FromArgb(239, 240, 244)
            };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 222));
            shell.Controls.Add(sidebar, 0, 0);
            var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            var brandIcon = new PictureBox { Location = new Point(0, 3), Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom, Image = Brand.LoadImage() };
            var brandHeader = new Panel { Width = 168, Height = 48, Margin = Padding.Empty };
            var brandName = FluentLabel("Dagami", 15, true); brandName.Location = new Point(42, 0);
            var brandSubtitle = FluentLabel("Agent Proxy", 9, false); brandSubtitle.Location = new Point(43, 25);
            brandHeader.Controls.Add(brandIcon); brandHeader.Controls.Add(brandName); brandHeader.Controls.Add(brandSubtitle);
            brand.Controls.Add(brandHeader);
            sidebar.Controls.Add(brand, 0, 0);
            var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            sidebar.Controls.Add(nav, 0, 1);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            _updateLink = new LinkLabel { Text = "Обновления\n"+Brand.ReleaseTag, Size = new Size(168,38), AutoSize=false, Font=new Font("Segoe UI",9), LinkColor=FluentTheme.Muted, LinkBehavior=LinkBehavior.HoverUnderline, AccessibleName="Обновления GitHub; установлено "+Brand.ReleaseTag, Margin=new Padding(0,0,0,8) };
            _updateLink.LinkClicked+=(sender,e)=>ExecuteNativeAction(OpenUpdates);
            footer.Controls.Add(_updateLink);
            var donate = new LinkLabel { Text = "Поддержать проект", AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = FluentTheme.Accent, Margin = new Padding(0, 0, 0, 12), AccessibleName = "Поддержать проект — CloudTips" };
            donate.LinkClicked += (sender, e) => ExecuteNativeAction(() => OpenCommunityLink("https://pay.cloudtips.ru/p/671fda86"));
            footer.Controls.Add(donate);
            foreach (var entry in new string[][] { new [] { "t.me/dagamvi", "https://t.me/dagamvi", "Паблик Dagamvi" }, new [] { "@bright26bot", "https://t.me/bright26bot", "Открыть бота bright26bot" } }) {
                string address = entry[1];
                var link = new LinkLabel { Text = entry[0], Tag = address, AccessibleName = entry[2], AutoSize = true,
                    LinkColor = FluentTheme.Muted, LinkBehavior = LinkBehavior.HoverUnderline, ActiveLinkColor = FluentTheme.Text, Margin = new Padding(0, 0, 0, 10) };
                link.LinkClicked += (sender, e) => ExecuteNativeAction(() => OpenCommunityLink(address));
                footer.Controls.Add(link);
            }
            var proxyOffer = FluentLabel("Получить прокси\nКодовое слово «DAGAMI»", 9, false);
            proxyOffer.AutoSize = false; proxyOffer.Size = new Size(168, 54);
            proxyOffer.AccessibleName = "Получить прокси. Кодовое слово DAGAMI";
            footer.Controls.Add(proxyOffer);
            sidebar.Controls.Add(footer, 0, 2);

            var main = new FluentLayout {
                Dock = DockStyle.Fill, Padding = new Padding(24, 24, 24, 12), ColumnCount = 1, RowCount = 3,
                BackColor = BackColor
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.Controls.Add(main, 1, 0);
            var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _fluentHeading = FluentLabel("Приложения", 24, true);
            _fluentSubtitle = FluentLabel("Запускайте рабочие инструменты через выбранное подключение.", 10, false);
            heading.Controls.Add(_fluentHeading);
            _fluentSubtitle.Visible = true; heading.Controls.Add(_fluentSubtitle);
            main.Controls.Add(heading, 0, 0);
            _fluentPages = new FluentPageHost { Dock = DockStyle.Fill, BackColor = BackColor };
            main.Controls.Add(_fluentPages, 0, 1);
            _nativeStatus = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9) };
            main.Controls.Add(_nativeStatus, 0, 2);

            var apps = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = BackColor };
            apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            apps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            apps.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
            var connection = new FluentHero { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 16), Margin = new Padding(0, 0, 0, 14) };
            var connectionRows = new FluentLayout { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            connectionRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            connectionRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            connectionRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            connectionRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var connectionCaption = new Label { Text = "ВАШЕ ПОДКЛЮЧЕНИЕ", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(222, 233, 255), Font = new Font("Segoe UI", 9, FontStyle.Bold), BackColor = Color.Transparent, Margin = Padding.Empty };
            connectionRows.Controls.Add(connectionCaption, 0, 0);
            _connectionSummary = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 10), BackColor = Color.Transparent, Margin = Padding.Empty, Text = "Параметры маршрута", AutoEllipsis = true };
            connectionRows.Controls.Add(_connectionSummary, 0, 1);
            var connectionAction = new FluentButton { Text = "Изменить", Dock = DockStyle.Fill, Margin = new Padding(12, 4, 0, 12), ForeColor = FluentTheme.Accent, BackColor = Color.White, AccessibleName = "Открыть настройки подключения" };
            connectionAction.Click += (sender, e) => ShowFluentPage(1);
            connectionRows.Controls.Add(connectionAction, 1, 1);
            connection.Controls.Add(connectionRows); apps.Controls.Add(connection, 0, 0);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var addMenu=new ContextMenuStrip();
            addMenu.Items.Add(AppMenuItem("Выбрать файл приложения…",AddNativeTarget));
            addMenu.Items.Add(AppMenuItem("Обновить список установленных",RefreshNativeTargets));
            var add=new FluentButton{Text="Добавить ▾",Width=130,Height=34};
            add.Click+=(sender,e)=>addMenu.Show(add,new Point(0,add.Height));tools.Controls.Add(add);_nativeButtons.Add(add);



            _blockAntigravityUpdates = new CheckBox { Name = "BlockUpdatesCheckBox", Text = "Блокировать обновления через мост", AutoSize = true, Checked = ConfigManager.Instance.BlockAntigravityUpdates, Margin = new Padding(12, 7, 0, 0), AccessibleName = "Блокировать известные запросы обновлений Antigravity через мост" };
            _blockAntigravityUpdates.CheckedChanged += (s, e) => {
                if (_updatingBlockUpdates || _preview) return;
                try { ConfigManager.Instance.ChangeConnection(cfg => cfg.BlockAntigravityUpdates = _blockAntigravityUpdates.Checked); }
                catch (Exception error) {
                    _updatingBlockUpdates = true; _blockAntigravityUpdates.Checked = ConfigManager.Instance.BlockAntigravityUpdates; _updatingBlockUpdates = false;
                    MessageBox.Show(this, error.Message, "Параметры", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                SyncSelectedAntigravityUpdateBlock();
            };
            // _blockAntigravityUpdates is added to optionsRow inside actionRows below
            Disposed+=(sender,e)=>addMenu.Dispose();
            apps.Controls.Add(tools, 0, 1);

            var listCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
            _targetList = new FluentAppList {
                Dock = DockStyle.Fill, View = View.Details, CheckBoxes = false, BorderStyle = BorderStyle.None,
                FullRowSelect = true, HideSelection = false, MultiSelect = false, BackColor = FluentTheme.Surface,
                ForeColor = FluentTheme.Text, ShowItemToolTips = true,
                AccessibleName = "Выберите приложение. Все действия ниже относятся к выбранной строке."
            };
            _targetList.Columns.Add("Приложение", 205);
            _targetList.Columns.Add("Статус", 130);
            _targetList.Columns.Add("Расположение", 600);
            _targetList.SmallImageList = new ImageList { ImageSize = new Size(1, 42) };
            StyleMacAppList(_targetList);
            _targetList.SizeChanged += (sender, e) => FitNativeListColumns();
            _targetList.HandleCreated += (sender, e) => FitNativeListColumns();
            _targetList.SelectedIndexChanged += (sender, e) => {
                if (_appFeedback != null) _appFeedback.Text = _proxyCheckMessage ?? "Настройте один раз, затем запускайте приложение как обычно.";
                UpdateNativeDetails();
            };
            listCard.Controls.Add(_targetList);
            apps.Controls.Add(listCard, 0, 2);
            var actionCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), Margin = Padding.Empty };
            var actionRows = new FluentLayout { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = FluentTheme.Surface };
            actionRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 26, 38, 24, 22, 22, 22 }) actionRows.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
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
            _primaryAppAction.Primary = true; _primaryAppAction.AutoSize = false; _primaryAppAction.Width = 205;
            // Patching and restoration live in one menu, beside Launch.
            BuildAppActionsMenu();
            AddNativeButton(launch, "Действия", () => _appActions.Show(_moreAppActions, new Point(0, _moreAppActions.Height)));
            _moreAppActions = (FluentButton)_nativeButtons[_nativeButtons.Count - 1];
            _moreAppActions.Primary = false; _moreAppActions.AutoSize = false; _moreAppActions.Width = 108;
            actionRows.Controls.Add(launch, 0, 1);
            var optionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };



            _blockAntigravityUpdates.Margin = new Padding(0, 3, 0, 0);
            optionsRow.Controls.Add(_blockAntigravityUpdates);
            actionRows.Controls.Add(optionsRow, 0, 2);
            _shortcutState = new Label { Name = "ShortcutStateLabel", Dock = DockStyle.Fill, ForeColor = FluentTheme.Text,
                AccessibleName = "Состояние обычного запуска и бинарного патча", AutoEllipsis=true, Margin = new Padding(0, 2, 0, 0) };
            actionRows.Controls.Add(_shortcutState, 0, 3);
            _clientPreparationStatus = new Label { Name="ClientPatchStatusLabel", Dock=DockStyle.Fill, ForeColor=FluentTheme.Text, AutoEllipsis=true, Margin=Padding.Empty, AccessibleName="Состояние бинарного патча Antigravity" };
            actionRows.Controls.Add(_clientPreparationStatus, 0, 4);
            _shortcutExplanation = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Font = new Font("Segoe UI", 9), Margin = Padding.Empty };
            actionRows.Controls.Add(_shortcutExplanation, 0, 5);
            _appFeedback = new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Font = new Font("Segoe UI", 9), Margin = Padding.Empty,
                AutoEllipsis=true, Text = "Настройте один раз, затем запускайте приложение как обычно." };
            var feedbackTip=new ToolTip{AutoPopDelay=30000,InitialDelay=400,ReshowDelay=100};
            _appFeedback.TextChanged+=(sender,e)=>feedbackTip.SetToolTip(_appFeedback,_appFeedback.Text);
            Disposed+=(sender,e)=>feedbackTip.Dispose();
            actionRows.Controls.Add(_appFeedback, 0, 6);
            actionCard.Controls.Add(actionRows); apps.Controls.Add(actionCard, 0, 3);

            Panel antigravityPage = BuildAntigravityPage();
            Panel connectionSettings = BuildConnectionSettings();
            Panel advancedSettings = BuildAdvancedSettings();

            var logCard = new FluentCard { Dock = DockStyle.Fill };
            _nativeLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, WordWrap = true, BackColor = FluentTheme.Surface, ForeColor = FluentTheme.Text,
                Font = new Font("Consolas", 10), AccessibleName = "Журнал Dagami Agent Proxy" };
            logCard.Controls.Add(_nativeLog);
            var reportActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 6, 0, 0) };
            var saveLogButton = new FluentButton { Name = "SaveJournalButton", Text = "Сохранить в файл…", AccessibleName = "Сохранить журнал в выбранный файл", Width = 190, Height = 36 };
            saveLogButton.Click += (sender, e) => SaveJournalToFile();
            reportActions.Controls.Add(saveLogButton); logCard.Controls.Add(reportActions);
            Panel settingsPage = BuildFlatConnectionPage(connectionSettings,advancedSettings);
            Panel dnsPage = BuildDnsPage();
            Panel[] pages = new Panel[] { apps, settingsPage, dnsPage, logCard, antigravityPage };
            string[] names = new string[] { "Приложения", "Подключение", "DNS и сброс", "Журнал", "Диагностика" };
            string[] descriptions = new string[] {
                "Выберите приложение и запустите через Dagami.",
                "Профиль, автозапуск и параметры маршрута.",
                "Ручной DNS, восстановление и сброс правил Windows.",
                "События приложения и результаты проверок.",
                "Проверка маршрута и backend Antigravity."
            };
            for (int i = 0; i < pages.Length; i++) {
                int pageIndex = i; pages[i].Visible = i == 0; _fluentPages.Controls.Add(pages[i]);
                var button = new FluentButton { Text = names[i], NavigationGlyph = i, Width = 168, Height = 44, Margin = new Padding(0, 0, 0, 6), NavigationSelected = i == 0, BackColor = sidebar.BackColor };
                button.Click += (s, e) => ShowFluentPage(pageIndex);
                _navigation.Add(button);
            }
            foreach(var button in _navigation)nav.Controls.Add(button);
            var globalReport = new FluentButton { Name = "BugReportNavigationButton", Text = "Сообщить о сбое", NavigationGlyph = 3, BackColor = sidebar.BackColor, Font = new Font("Segoe UI", 9), AccessibleName = "Сообщить об ошибке — журнал и картинка", Width = 168, Height = 36, Margin = new Padding(0, 10, 0, 6) };
            globalReport.Click += (sender, e) => OpenBugReport();
            nav.Controls.Add(globalReport);
            _pageControls = pages; _pageNames = names; _pageDescriptions = descriptions;
            apps.BringToFront();
            _proxyBox.TextChanged += (sender, e) => { _proxyCheckFailed = false; _proxyCheckMessage = null; UpdateNativeStatus(); };
            _nativeLogTimer = new System.Windows.Forms.Timer { Interval = 650 };
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
            if (!_preview) {
                _nativeLogTimer.Start();DnsProviders.Initialize();
                DateTime nextDiscovery=DateTime.UtcNow;
                Activated+=(sender,e)=>{if(!_nativeBusy&&DateTime.UtcNow>=nextDiscovery){nextDiscovery=DateTime.UtcNow.AddSeconds(15);RefreshNativeTargets();QueueClientStatus(true);UpdateAppActionState();ReadStartupState();}};
            }
            RefreshNativeTargets();
        }

        private void CheckFluentProxy()
        {
            if (!SaveNativeSettings()) return; int port = _bridgePort;
            _nativeBusy = true; SetNativeButtonsEnabled(false); _nativeStatus.Text = "Проверка TLS и доступности маршрута через Dagami…";
            if (_primaryAppAction != null) _primaryAppAction.Text = "Проверка…";
            System.Threading.ThreadPool.QueueUserWorkItem(o => {
                var probe = ConnectionDiagnostics.Check(port);
                try { BeginInvoke(new Action(() => {
                    _nativeBusy = false; SetNativeButtonsEnabled(true);
                    _proxyCheckFailed = !probe.Available;
                    _proxyCheckMessage = probe.Message;
                    if (_appFeedback != null) _appFeedback.Text = _proxyCheckMessage;
                    Logger.Log((probe.Available ? "[✓] " : "[-] ") + probe.Message); UpdateNativeStatus();
                })); } catch (InvalidOperationException) { }
            });
        }

        private void OpenCommunityLink(string address)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
        }
    }
}
