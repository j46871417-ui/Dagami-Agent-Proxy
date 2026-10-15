using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DevProxyApp
{
    public static class NativeLogin
    {
        public static void RequireStopped(string executable)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process) {
                    if (!process.HasExited)
                        throw new InvalidOperationException(Path.GetFileNameWithoutExtension(executable) + " уже запущен (PID " + process.Id + "). Полностью закройте его окна и значок в трее, затем запустите через Dagami Agent Proxy. Уже работающий процесс не получает новые настройки прокси.");
                }
        }

        public static string ElectronArguments(AppTarget target, string proxy, string profileRoot)
        {
            Uri bridge;
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out bridge) || bridge.Scheme != "http" ||
                bridge.Host != "127.0.0.1" || !string.IsNullOrEmpty(bridge.UserInfo) || bridge.Port <= 0)
                throw new ArgumentException("Expected the local Dagami Agent Proxy bridge.");
            string args = "--proxy-server=\"" + proxy + "\" --proxy-bypass-list=\"localhost;127.0.0.1;[::1]\" --disable-quic";
            if (Path.GetFileName(target.ExePath).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase))
                return args; // Normal profile shares the native OAuth single-instance lock.
            Directory.CreateDirectory(profileRoot);
            return args + " --user-data-dir=\"" + profileRoot + "\" --new-window";
        }

        public static string ValidateLoginUrl(string value)
        {
            Uri uri;
            if (value == null || value.Length > 16384 || value.IndexOfAny(new char[] {'\r','\n','\0','"'}) >= 0 ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Port != 443 ||
                !string.IsNullOrEmpty(uri.UserInfo) || (uri.Host != "accounts.google.com" && uri.Host != "antigravity.google"))
                throw new InvalidOperationException("Нужна свежая HTTPS-ссылка входа Google из Antigravity.");
            return value;
        }

        public static string ReadFreshLoginUrl(string path)
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("Нажмите вход в Antigravity, затем снова «Вход через прокси».");
            string text;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                file.Seek(Math.Max(0, file.Length - 131072), SeekOrigin.Begin);
                byte[] buffer = new byte[Math.Min(131072, file.Length - file.Position)];
                int length = file.Read(buffer, 0, buffer.Length);
                text = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
            }
            MatchCollection matches = Regex.Matches(text, "https://accounts\\.google\\.com/(?:o/oauth2/|v3/signin/|signin/)[^\\s<>\"\\x1b]+");
            if (matches.Count == 0) throw new InvalidOperationException("Свежая ссылка входа не найдена. Нажмите вход в самом Antigravity.");
            return ValidateLoginUrl(matches[matches.Count-1].Value);
        }

        public static string BrowserArguments(string url, int port, string profile)
        {
            ValidateLoginUrl(url);
            if (port < 1 || port > 65535 || profile.IndexOf('"') >= 0) throw new ArgumentException("Invalid browser configuration.");
            return "--proxy-server=http://127.0.0.1:" + port + " --proxy-bypass-list=\"localhost;127.0.0.1;[::1]\" --disable-quic" +
                " --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --new-window \"" + url + "\"";
        }

        public static string FindBrowser()
        {
            foreach (string folder in new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                foreach (string relative in new string[] { @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe",
                        @"BraveSoftware\Brave-Browser\Application\brave.exe", @"Yandex\YandexBrowser\Application\browser.exe" }) {
                    string path = Path.Combine(folder, relative);
                    if (File.Exists(path)) return path;
                }
            throw new InvalidOperationException("Установленный Chrome, Edge, Brave или Яндекс Браузер не найден.");
        }
    }

    public partial class MainForm
    {
        private void OpenAntigravityLogin()
        {
            var antigravity = AntigravitySession();
            if (antigravity == null || _bridgePort <= 0)
                throw new InvalidOperationException("Сначала запустите Antigravity через Dagami Agent Proxy или настроенный ярлык и нажмите вход в приложении.");
            string url = NativeLogin.ReadFreshLoginUrl(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "antigravity", "logs", "language_server.log"));
            string browser = NativeLogin.FindBrowser();
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "devproxy", "oauth-browser");
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo(browser, NativeLogin.BrowserArguments(url, _bridgePort, profile));
            start.UseShellExecute = false;
            // Proxy credentials remain in LocalProxyBridge, not the browser arguments.
            Process.Start(start);
            Logger.Log("[OAuth] Открыт отдельный браузер через тот же локальный мост. URL входа не записывается.");
            _antigravityReport.Text = "Завершите вход в открытом браузере. Не закрывайте Antigravity и Dagami Agent Proxy до завершения входа.\r\n" +
                "Возврат использует обычный профиль Antigravity. При смене режима запуска полностью закройте старые экземпляры перед первым запуском.";
        }
    }
}
