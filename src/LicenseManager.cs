using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;

namespace DevProxyApp {
    public static class LicenseManager {
        public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        public const string Salt = "DagamiAgentProxySecretSalt2026";
        public const string BotUrl = "https://t.me/bright26bot?start=dagami";

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dagami.License.v1");

        static string LicenseFilePath {
            get {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devproxy");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "license.dat");
            }
        }

        public static string ComputeChecksum(string payload) {
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload + Salt));
                char[] chars = new char[4];
                for (int i = 0; i < 4; i++) {
                    chars[i] = Alphabet[hash[i] % 32];
                }
                return new string(chars);
            }
        }

        public static string NormalizeKey(string raw) {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder();
            foreach (char c in raw) {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            }
            string cleaned = sb.ToString();
            if (!cleaned.StartsWith("DAGAMI", StringComparison.OrdinalIgnoreCase)) return "";
            string body = cleaned.Substring(6);
            if (body.Length != 12) return "";
            return "DAGAMI-" + body.Substring(0, 4) + "-" + body.Substring(4, 4) + "-" + body.Substring(8, 4);
        }

        public static bool ValidateKey(string key) {
            string normalized = NormalizeKey(key);
            if (string.IsNullOrEmpty(normalized)) return false;
            string body = normalized.Replace("-", "").Substring(6);
            foreach (char c in body) {
                if (Alphabet.IndexOf(c) < 0) return false;
            }
            string payload = body.Substring(0, 8);
            string chk = body.Substring(8, 4);
            return string.Equals(ComputeChecksum(payload), chk, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsActivated() {
            string stored = GetStoredKey();
            return !string.IsNullOrEmpty(stored) && ValidateKey(stored);
        }

        public static string GetStoredKey() {
            try {
                string path = LicenseFilePath;
                if (!File.Exists(path)) return null;
                byte[] encrypted = File.ReadAllBytes(path);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            } catch {
                return null;
            }
        }

        public static string GetMaskedKey() {
            string stored = GetStoredKey();
            if (string.IsNullOrEmpty(stored)) return null;
            string normalized = NormalizeKey(stored);
            if (normalized.Length != 21) return "DAGAMI-****-****-****";
            return "DAGAMI-****-****-" + normalized.Substring(normalized.Length - 4);
        }

        public static bool Activate(string key, out string error) {
            string normalized = NormalizeKey(key);
            if (!ValidateKey(normalized)) {
                error = "Неверный ключ доступа. Проверьте правильность ввода.";
                return false;
            }
            try {
                byte[] plain = Encoding.UTF8.GetBytes(normalized);
                byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                string path = LicenseFilePath;
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, encrypted);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                error = null;
                return true;
            } catch (Exception ex) {
                error = "Ошибка сохранения лицензии: " + ex.Message;
                return false;
            }
        }

        public static void Deactivate() {
            try {
                string path = LicenseFilePath;
                if (File.Exists(path)) File.Delete(path);
            } catch { }
        }
    }

    public sealed class ActivationForm : Form {
        readonly TextBox _keyBox;
        readonly Label _errorLabel;
        readonly Button _activateBtn;
        readonly Button _botBtn;
        readonly Button _exitBtn;

        public ActivationForm() {
            Text = Brand.Name + " — Активация";
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
                Text = "Первый запуск · Активация"
            };
            Controls.Add(subLabel);

            var descLabel = new Label {
                Location = new Point(32, 90),
                Size = new Size(456, 42),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = FluentTheme.Muted,
                Text = "Для продолжения работы введите ключ доступа.\nКлючи бесплатно выдаются в официальном Telegram-боте выдачи."
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
                Text = "КЛЮЧ ДОСТУПА"
            };
            card.Controls.Add(fieldLabel);

            _keyBox = new TextBox {
                Location = new Point(16, 36),
                Size = new Size(424, 28),
                Font = new Font("Consolas", 12.5f, FontStyle.Bold),
                ForeColor = FluentTheme.Text,
                BackColor = Color.FromArgb(248, 250, 254),
                BorderStyle = BorderStyle.FixedSingle,
                CharacterCasing = CharacterCasing.Upper
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
                Text = "Формат: DAGAMI-XXXX-XXXX-XXXX"
            };
            card.Controls.Add(hintLabel);

            _errorLabel = new Label {
                Location = new Point(16, 92),
                AutoSize = true,
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
                    Process.Start(new ProcessStartInfo(LicenseManager.BotUrl) { UseShellExecute = true });
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
                Text = "Активировать",
                Cursor = Cursors.Hand
            };
            _activateBtn.FlatAppearance.BorderSize = 0;
            _activateBtn.Click += (s, e) => DoActivate();
            Controls.Add(_activateBtn);

            _exitBtn = new Button {
                Location = new Point(32, 342),
                Size = new Size(456, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = FluentTheme.Muted,
                Font = new Font("Segoe UI", 8.5f),
                Text = "Выйти из приложения",
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

        void DoActivate() {
            string raw = _keyBox.Text.Trim();
            if (string.IsNullOrEmpty(raw)) {
                _errorLabel.ForeColor = Color.FromArgb(255, 107, 107);
                _errorLabel.Text = "Введите ключ доступа из Telegram-бота.";
                _keyBox.Focus();
                return;
            }
            string err;
            if (LicenseManager.Activate(raw, out err)) {
                _errorLabel.ForeColor = Color.FromArgb(47, 222, 196);
                _errorLabel.Text = "✓ Ключ успешно активирован! Запуск приложения…";
                _activateBtn.Enabled = false;
                _botBtn.Enabled = false;
                _keyBox.Enabled = false;
                _exitBtn.Enabled = false;
                var timer = new System.Windows.Forms.Timer { Interval = 600 };
                timer.Tick += (s, ev) => {
                    timer.Stop();
                    timer.Dispose();
                    DialogResult = DialogResult.OK;
                    Close();
                };
                timer.Start();
            } else {
                _errorLabel.ForeColor = Color.FromArgb(255, 107, 107);
                _errorLabel.Text = err;
                _keyBox.Focus();
                _keyBox.SelectAll();
            }
        }
    }
}
