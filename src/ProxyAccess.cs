using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DevProxyApp {
    // Application access is unrestricted. Only the remote Dagami proxy authenticates a device.
    public static class ProxyAccess {
        public const string BotUrl = ManagedAccess.BotKeyLink;
        public static bool Connect(string key, out string error) {
            if (!ManagedAccess.IsAccessCode((key ?? "").Trim())) {
                error = "Нужен ключ прокси DAGAMI1- из /key. Приложение работает без ключа.";
                return false;
            }
            try { ConnectDevice(ManagedAccess.Redeem(key.Trim())); error = null; return true; }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        internal static void ConnectDevice(ManagedAccess device) {
            if (device == null) throw new ArgumentNullException("device");
            device.Commit(ManagedAccess.SelectProfile);
        }
    }

    public sealed class ProxyKeyForm : Form {
        readonly TextBox _keyBox;
        readonly Label _errorLabel;
        readonly Button _activateBtn;
        readonly Button _botBtn;
        readonly Button _exitBtn, _saveOfflineBtn;
        readonly Func<bool> _canChangeRoute;
        readonly bool _activateRoute;

        public ProxyKeyForm() : this(null) { }

        internal ProxyKeyForm(Func<bool> canChangeRoute, bool activateRoute = true) {
            _canChangeRoute = canChangeRoute;
            _activateRoute = activateRoute;
            Text = Brand.Name + " — Подключить прокси";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 386);
            BackColor = FluentTheme.Background;
            ForeColor = FluentTheme.Text;
            Font = new Font("Segoe UI", 9.5f);
            ShowInTaskbar = true;
            try { Icon = Brand.LoadIcon(); } catch { }

            var avatar = new PictureBox {
                Location = new Point(32, 24),
                Size = new Size(48, 48),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = Brand.LoadImage()
            };
            Controls.Add(avatar);

            var titleLabel = new Label {
                Location = new Point(92, 22),
                AutoSize = true,
                Font = new Font("Segoe UI", 15, FontStyle.Bold),
                ForeColor = FluentTheme.Text,
                Text = Brand.Name
            };
            Controls.Add(titleLabel);

            var subLabel = new Label {
                Location = new Point(94, 50),
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Regular),
                ForeColor = FluentTheme.Accent,
                Text = "Подключение к прокси Dagami"
            };
            Controls.Add(subLabel);

            var descLabel = new Label {
                Location = new Point(32, 90),
                Size = new Size(456, 42),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = FluentTheme.Muted,
                Text = "Ключ из /key нужен только для прокси Dagami.\nПриложение и свой прокси работают без ключа."
            };
            Controls.Add(descLabel);

            var card = new Panel {
                Location = new Point(32, 142),
                Size = new Size(456, 126),
                BackColor = FluentTheme.Surface, BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(card);

            var fieldLabel = new Label {
                Location = new Point(16, 14),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = FluentTheme.Muted,
                Text = "КЛЮЧ ПРОКСИ DAGAMI"
            };
            card.Controls.Add(fieldLabel);

            _keyBox = new TextBox {
                Location = new Point(16, 36),
                Size = new Size(424, 28),
                Font = new Font("Consolas", 10),
                ForeColor = FluentTheme.Text,
                BackColor = FluentTheme.Background,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = 4096,
                UseSystemPasswordChar = true,
                Text = ManagedAccess.ReadDeferredKey() ?? ""
            };
            _keyBox.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) {
                    e.SuppressKeyPress = true;
                    DoActivate();
                }
            };
            card.Controls.Add(_keyBox);

            var hintLabel = new Label {
                Location = new Point(16, 70),
                AutoSize = true,
                Font = new Font("Segoe UI", 8),
                ForeColor = FluentTheme.Muted,
                Text = "Ключ для подключения начинается с DAGAMI1-"
            };
            card.Controls.Add(hintLabel);

            _errorLabel = new Label {
                Location = new Point(16, 92),
                Size = new Size(424, 32),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 107, 107),
                Text = ""
            };
            card.Controls.Add(_errorLabel);

            _botBtn = new Button {
                Location = new Point(32, 288),
                Size = new Size(244, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(235, 243, 255),
                ForeColor = FluentTheme.Accent,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Text = "Получить ключ в @bright26bot",
                Cursor = Cursors.Hand
            };
            _botBtn.FlatAppearance.BorderColor = Color.FromArgb(191, 219, 254);
            _botBtn.Click += (s, e) => {
                try {
                    Process.Start(new ProcessStartInfo(ProxyAccess.BotUrl) { UseShellExecute = true });
                } catch (Exception ex) {
                    MessageBox.Show(this, "Не удалось открыть браузер: " + ex.Message, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(_botBtn);

            _activateBtn = new Button {
                Location = new Point(286, 288),
                Size = new Size(202, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(22, 152, 136),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Text = "Подключить прокси",
                Cursor = Cursors.Hand
            };
            _activateBtn.FlatAppearance.BorderSize = 0;
            _activateBtn.Click += (s, e) => DoActivate();
            Controls.Add(_activateBtn);

            _saveOfflineBtn = new Button {
                Location = new Point(32,342), Size = new Size(240,26),
                Text = "Сохранить ключ без сети"
            };
            _saveOfflineBtn.Click += (s,e) => SaveOffline();
            Controls.Add(_saveOfflineBtn);
            _exitBtn = new Button {
                Location = new Point(280, 342),
                Size = new Size(208, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = FluentTheme.Muted,
                Font = new Font("Segoe UI", 8.5f),
                Text = "Отмена",
                Cursor = Cursors.Hand
            };
            _exitBtn.FlatAppearance.BorderSize = 0;
            _exitBtn.Click += (s, e) => {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(_exitBtn);

            AcceptButton = _activateBtn;
            CancelButton = _exitBtn;
        }

        void SaveOffline() {
            try {
                ManagedAccess.SaveDeferredKey(_keyBox.Text.Trim());
                DialogResult = DialogResult.Retry;
                Close();
            } catch (Exception error) {
                _errorLabel.ForeColor = Color.FromArgb(255,107,107);
                _errorLabel.Text = error.Message;
            }
        }

        async void DoActivate() {
            if (!_activateBtn.Enabled) return;
            string raw = _keyBox.Text.Trim();
            if (string.IsNullOrEmpty(raw)) {
                _errorLabel.ForeColor = Color.FromArgb(255, 107, 107);
                _errorLabel.Text = "Введите ключ доступа из Telegram-бота.";
                _keyBox.Focus();
                return;
            }
            _activateBtn.Enabled = _keyBox.Enabled = _saveOfflineBtn.Enabled = false;
            try {
                if (ManagedAccess.IsAccessCode(raw)) {
                    _errorLabel.ForeColor = FluentTheme.Muted;
                    _errorLabel.Text = "Подключаем устройство к шлюзу Dagami…";
                    var device = await Task.Run(() => ManagedAccess.Redeem(raw));
                    if (IsDisposed || Disposing || !Visible) return;
                    if (_canChangeRoute != null && !_canChangeRoute()) {
                        _errorLabel.Text = "Остановите активные сессии и повторите активацию.";
                        return;
                    }
                    if(_activateRoute)ProxyAccess.ConnectDevice(device);
                    else device.Commit(()=>{
                        var config=ConfigManager.Instance;
                        config.SaveProxySelection("key",config.SelectedProxyAddress,config.SavedProxyRequireGemini);
                    });
                    ManagedAccess.ClearDeferredKey();
                } else {
                    throw new ArgumentException("Для прокси получите ключ DAGAMI1- через /key. Приложение работает без активации.");
                }
                DialogResult = DialogResult.OK;
                Close();
            } catch (Exception error) {
                if (IsDisposed || Disposing || !Visible) return;
                _errorLabel.ForeColor = Color.FromArgb(255, 107, 107);
                _errorLabel.Text = error is System.Net.Sockets.SocketException || error is TimeoutException
                    ? "Шлюз Dagami недоступен. Проверьте подключение и повторите."
                    : error.Message;
            } finally {
                if (!IsDisposed && !Disposing && Visible) {
                    _activateBtn.Enabled = _keyBox.Enabled = _saveOfflineBtn.Enabled = true;
                    _keyBox.Focus();
                    _keyBox.SelectAll();
                }
            }
        }
    }
}
