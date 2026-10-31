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
        public static void StopExactExecutables(System.Collections.Generic.IEnumerable<string> executables, int timeoutMilliseconds=5000) {
            var deadline=Stopwatch.StartNew();var errors=new System.Collections.Generic.List<string>();
            var paths=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string value in executables) {
                if(string.IsNullOrEmpty(value)||!Path.IsPathRooted(value))throw new ArgumentException("Для остановки нужен полный путь приложения.");
                paths.Add(Path.GetFullPath(value));
            }
            foreach(string path in paths)foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))using(process) {
                try {
                    if(process.HasExited)continue;
                    // Never infer ownership from a process name or kill after an
                    // access-denied path lookup. Another install may share a name.
                    if(!string.Equals(Path.GetFullPath(process.MainModule.FileName),path,StringComparison.OrdinalIgnoreCase))continue;
                    process.Kill();
                    if(!process.WaitForExit(Math.Max(0,timeoutMilliseconds-(int)deadline.ElapsedMilliseconds)))errors.Add(Path.GetFileName(path)+": процесс не завершился");
                } catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { errors.Add(Path.GetFileName(path)+": не удалось проверить путь или завершить PID "+process.Id); }
            }
            foreach(string path in paths)try{RequireStopped(path);}catch(InvalidOperationException error){errors.Add(error.Message);}
            if(errors.Count>0)throw new IOException("Не все процессы закрыты. "+string.Join("; ",errors.ToArray()));
        }

        public static void RequireStopped(string executable)

        {

            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))

                using (process) {

                    if (!process.HasExited)

                    {

                        // Codex Desktop and Codex CLI can share a basename;

                        // another installed copy must not block this launcher.

                        if(Path.IsPathRooted(executable))try {

                            if(!string.Equals(Path.GetFullPath(process.MainModule.FileName),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))continue;

                        }catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){continue;}

                        throw new InvalidOperationException(Path.GetFileNameWithoutExtension(executable) + " уже запущен (PID " + process.Id + "). Полностью закройте его окна и значок в трее, включая экземпляры, запущенные от администратора, затем запустите через Dagami. Работающий процесс не получает новые настройки прокси.");

                    }

                }

        }



        public static string ElectronArguments(AppTarget target, string proxy, string profileRoot)

        {

            Uri bridge;

            if (!Uri.TryCreate(proxy, UriKind.Absolute, out bridge) || bridge.Scheme != "http" ||

                bridge.Host != "127.0.0.1" || !string.IsNullOrEmpty(bridge.UserInfo) || bridge.Port <= 0)

                throw new ArgumentException("Expected the local Dagami Agent Proxy bridge.");

            string args = "--proxy-server=\"" + proxy + "\" --proxy-bypass-list=\"localhost;127.0.0.1;[::1]\" --disable-quic";

            if (target != null && AntigravityIntegration.IsIde(target.ExePath) && ConfigManager.Instance.BlockAntigravityUpdates)

                args += " --disable-updates";

            // Use the real settings/account profile. RequireStopped prevents an

            // existing direct process from silently discarding these flags.

            return args;

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

            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(10))

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



        public static string FindFreshLoginUrl()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] logDirs = new string[] {
                Path.Combine(appData, "Antigravity", "logs"),
                Path.Combine(appData, "Antigravity IDE", "logs"),
                Path.Combine(appData, "antigravity", "logs")
            };

            foreach (string dir in logDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var files = new DirectoryInfo(dir).GetFiles("*.log", SearchOption.AllDirectories);
                    Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    foreach (var file in files)
                    {
                        if (DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) break;
                        try { return ReadFreshLoginUrl(file.FullName); } catch { }
                    }
                }
                catch { }
            }

            foreach (string dirName in new string[] { "antigravity", "Antigravity", "Antigravity IDE" })
            {
                string cliLog = Path.Combine(appData, dirName, "logs", "language_server.log");
                if (File.Exists(cliLog)) {
                    try { return ReadFreshLoginUrl(cliLog); } catch { }
                }
            }

            throw new InvalidOperationException("Свежая ссылка входа не найдена. Нажмите «Sign In» / «Вход» в Antigravity или Antigravity IDE.");
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

            if (_bridgePort <= 0)

                throw new InvalidOperationException("Сначала запустите Antigravity через Dagami Agent Proxy.");

            string url = NativeLogin.FindFreshLoginUrl();
            if (!url.Contains("prompt=") && url.Contains("accounts.google.com")) url += (url.Contains("?") ? "&" : "?") + "prompt=select_account";

            string browser = NativeLogin.FindBrowser();

            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "devproxy", "oauth-browser");

            Directory.CreateDirectory(profile);

            var start = new ProcessStartInfo(browser, NativeLogin.BrowserArguments(url, _bridgePort, profile));

            start.UseShellExecute = false;

            Process.Start(start);

            try { Clipboard.SetText(url); } catch { }

            Logger.Log("[OAuth] Открыт браузер через выбранный маршрут Dagami. Ссылка входа скопирована в буфер обмена.");

            if (_antigravityReport != null)

            {

                _antigravityReport.Text = "Открыт браузер через локальный мост Dagami.\r\n" +

                    "Завершите вход в браузере. Регион выхода и доступ к моделям этой проверкой не подтверждаются.\r\n" +

                    "Ссылка входа также скопирована в буфер обмена (можно вставить в браузер с VPN).";

            }

        }

    }

}
