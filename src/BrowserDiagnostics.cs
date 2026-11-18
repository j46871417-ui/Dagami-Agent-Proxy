using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DevProxyApp
{
    public enum BrowserFamily { Chromium, Firefox, Other }
    public enum BrowserDohStatus { Follows, Bypasses, Unknown }

    public sealed class BrowserItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ExePath { get; set; }
        public BrowserFamily Family { get; set; }
        public string SettingsUrl { get; set; }
        public BrowserDohStatus Status { get; set; }
        public string Explanation { get; set; }
        public bool ByPolicy { get; set; }
    }

    /// <summary>
    /// Detects installed web browsers and checks whether they have Secure DNS (DoH)
    /// enabled internally, which bypasses Windows system DNS and Dagami DNS proxy.
    /// Read-only: inspects Windows Registry and browser configuration files.
    /// </summary>
    public static class BrowserDiagnostics
    {
        sealed class BrowserSpec
        {
            public string Id;
            public string Name;
            public string Exe;
            public BrowserFamily Family;
            public string[] LocalStateRelPaths;
            public string PolicyKey;
            public string SettingsUrl;
            public string FirefoxRoot;
            public string PathHint;
            public bool Roaming;
            public bool Full;
        }

        static readonly BrowserSpec[] KnownBrowsers = new[]
        {
            new BrowserSpec {
                Id = "chrome", Name = "Google Chrome", Exe = "chrome.exe",
                Family = BrowserFamily.Chromium,
                LocalStateRelPaths = new[] { @"Google\Chrome\User Data\Local State" },
                PolicyKey = @"SOFTWARE\Policies\Google\Chrome",
                SettingsUrl = "chrome://settings/security", Full = true
            },
            new BrowserSpec {
                Id = "edge", Name = "Microsoft Edge", Exe = "msedge.exe",
                Family = BrowserFamily.Chromium,
                LocalStateRelPaths = new[] { @"Microsoft\Edge\User Data\Local State" },
                PolicyKey = @"SOFTWARE\Policies\Microsoft\Edge",
                SettingsUrl = "edge://settings/privacy", Full = true
            },
            new BrowserSpec {
                Id = "brave", Name = "Brave", Exe = "brave.exe",
                Family = BrowserFamily.Chromium,
                LocalStateRelPaths = new[] { @"BraveSoftware\Brave-Browser\User Data\Local State" },
                PolicyKey = @"SOFTWARE\Policies\BraveSoftware\Brave",
                SettingsUrl = "brave://settings/security", Full = true
            },
            new BrowserSpec {
                Id = "yandex", Name = "Yandex Browser", Exe = "browser.exe", PathHint = @"\Yandex\",
                Family = BrowserFamily.Chromium,
                LocalStateRelPaths = new[] { @"Yandex\YandexBrowser\User Data\Local State" },
                PolicyKey = @"SOFTWARE\Policies\YandexBrowser"
            },
            new BrowserSpec {
                Id = "opera", Name = "Opera", Exe = "opera.exe",
                Family = BrowserFamily.Chromium,
                LocalStateRelPaths = new[] { @"Opera Software\Opera Stable\Local State", @"Opera Software\Opera GX Stable\Local State" },
                Roaming = true
            },
            new BrowserSpec {
                Id = "vivaldi", Name = "Vivaldi", Exe = "vivaldi.exe",
                Family = BrowserFamily.Chromium,
                LocalStateRelPaths = new[] { @"Vivaldi\User Data\Local State" },
                PolicyKey = @"SOFTWARE\Policies\Vivaldi"
            },
            new BrowserSpec {
                Id = "firefox", Name = "Mozilla Firefox", Exe = "firefox.exe",
                Family = BrowserFamily.Firefox,
                PolicyKey = @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS",
                SettingsUrl = "about:preferences#privacy", FirefoxRoot = @"Mozilla\Firefox", Full = true
            },
            new BrowserSpec {
                Id = "waterfox", Name = "Waterfox", Exe = "waterfox.exe",
                Family = BrowserFamily.Firefox,
                SettingsUrl = "about:preferences#privacy", FirefoxRoot = "Waterfox"
            }
        };

        public static List<BrowserItem> DetectAndInspect()
        {
            var detected = DetectBrowsers();
            foreach (var b in detected)
            {
                InspectBrowser(b);
            }
            return detected;
        }

        public static List<BrowserItem> GetBypassingBrowsers()
        {
            var list = new List<BrowserItem>();
            foreach (var b in DetectAndInspect())
            {
                if (b.Status == BrowserDohStatus.Bypasses) list.Add(b);
            }
            return list;
        }

        private static List<BrowserItem> DetectBrowsers()
        {
            var found = new List<BrowserItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string, string> addExe = (exe, displayName) =>
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                string fullExe = Path.GetFullPath(exe);
                if (seenPaths.Contains(fullExe)) return;
                seenPaths.Add(fullExe);

                string fileName = Path.GetFileName(fullExe);
                if (string.Equals(fileName, "iexplore.exe", StringComparison.OrdinalIgnoreCase)) return;

                BrowserSpec spec = FindSpecByExe(fullExe);
                if (spec != null)
                {
                    found.Add(new BrowserItem
                    {
                        Id = spec.Id,
                        Name = spec.Name,
                        ExePath = fullExe,
                        Family = spec.Family,
                        SettingsUrl = spec.SettingsUrl
                    });
                }
                else
                {
                    found.Add(new BrowserItem
                    {
                        Id = "exe:" + fileName.ToLowerInvariant(),
                        Name = !string.IsNullOrEmpty(displayName) ? displayName : Path.GetFileNameWithoutExtension(fileName),
                        ExePath = fullExe,
                        Family = BrowserFamily.Other
                    });
                }
            };

            // 1. StartMenuInternet registry keys
            foreach (bool machine in new[] { true, false })
            {
                string[] subKeys = GetRegistrySubKeys(machine, @"SOFTWARE\Clients\StartMenuInternet");
                foreach (var k in subKeys)
                {
                    string cmd = GetRegistryString(machine, @"SOFTWARE\Clients\StartMenuInternet\" + k + @"\shell\open\command", null);
                    string name = GetRegistryString(machine, @"SOFTWARE\Clients\StartMenuInternet\" + k, null);
                    addExe(ExtractExeFromCommand(cmd), name);
                }
            }

            // 2. App Paths
            foreach (var s in KnownBrowsers)
            {
                foreach (bool machine in new[] { true, false })
                {
                    string path = GetRegistryString(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + s.Exe, null);
                    if (!string.IsNullOrEmpty(path))
                    {
                        addExe(path.Trim('"'), s.Name);
                    }
                }
            }

            return found;
        }

        private static BrowserSpec FindSpecByExe(string exePath)
        {
            string fileName = Path.GetFileName(exePath);
            foreach (var s in KnownBrowsers)
            {
                if (string.Equals(s.Exe, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (s.PathHint == null || exePath.IndexOf(s.PathHint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return s;
                }
            }
            return null;
        }

        private static void InspectBrowser(BrowserItem item)
        {
            if (item.Family == BrowserFamily.Other)
            {
                item.Status = BrowserDohStatus.Unknown;
                item.Explanation = "Сторонний браузер";
                return;
            }

            BrowserSpec spec = null;
            foreach (var s in KnownBrowsers)
            {
                if (s.Id == item.Id) { spec = s; break; }
            }
            if (spec == null)
            {
                item.Status = BrowserDohStatus.Unknown;
                return;
            }

            if (item.Family == BrowserFamily.Firefox)
            {
                InspectFirefox(item, spec);
            }
            else
            {
                InspectChromium(item, spec);
            }
        }

        private static void InspectChromium(BrowserItem item, BrowserSpec spec)
        {
            // 1. Enterprise Policies
            if (!string.IsNullOrEmpty(spec.PolicyKey))
            {
                foreach (bool machine in new[] { true, false })
                {
                    string mode = GetRegistryString(machine, spec.PolicyKey, "DnsOverHttpsMode");
                    if (!string.IsNullOrEmpty(mode))
                    {
                        string templates = GetRegistryString(machine, spec.PolicyKey, "DnsOverHttpsTemplates");
                        ClassifyChromium(item, mode, templates, true);
                        return;
                    }
                }
            }

            // 2. Local State file
            if (spec.LocalStateRelPaths != null)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                foreach (var rel in spec.LocalStateRelPaths)
                {
                    string basePath = spec.Roaming ? roamingAppData : localAppData;
                    string filePath = Path.Combine(basePath, rel);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            if(new FileInfo(filePath).Length>4*1024*1024)continue;
                            string json = File.ReadAllText(filePath, Encoding.UTF8);
                            string mode = null;
                            string templates = null;

                            // Fast JSON check
                            var mMode = Regex.Match(json, @"""dns_over_https""\s*:\s*\{[^}]*""mode""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                            if (mMode.Success) mode = mMode.Groups[1].Value;

                            var mTpl = Regex.Match(json, @"""dns_over_https""\s*:\s*\{[^}]*""templates""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                            if (mTpl.Success) templates = mTpl.Groups[1].Value;

                            if (mode != null || !string.IsNullOrEmpty(templates))
                            {
                                ClassifyChromium(item, mode, templates, false);
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }

            item.Status = BrowserDohStatus.Unknown;
            item.Explanation = "Настройки не удалось определить. Это не подтверждает использование системного DNS.";
        }

        private static void ClassifyChromium(BrowserItem item, string mode, string templates, bool byPolicy)
        {
            item.ByPolicy = byPolicy;
            mode = (mode ?? "").Trim().ToLowerInvariant();
            bool hasTemplates = !string.IsNullOrWhiteSpace(templates);

            if (mode == "secure" || mode == "custom")
            {
                if (hasTemplates)
                {
                    item.Status = BrowserDohStatus.Bypasses;
                    item.Explanation = "Явно настроен собственный DoH; адрес и параметры скрыты.";
                }
                else
                {
                    item.Status = BrowserDohStatus.Unknown;
                    item.Explanation = "Задан режим Secure DoH без указания сервера";
                }
                return;
            }

            if (mode == "automatic")
            {
                if (hasTemplates)
                {
                    item.Status = BrowserDohStatus.Bypasses;
                    item.Explanation = "Задан автоматический DoH с собственным шаблоном; фактический маршрут зависит от браузера.";
                }
                else
                {
                    item.Status = BrowserDohStatus.Unknown;
                    item.Explanation = "Автоматический DoH: фактический резолвер не подтверждён.";
                }
                return;
            }

            item.Status = mode=="off" ? BrowserDohStatus.Follows : BrowserDohStatus.Unknown;
            item.Explanation = mode=="off" ? "DoH явно отключён в прочитанных настройках." : "Режим DoH не удалось определить.";
        }

        private static void InspectFirefox(BrowserItem item, BrowserSpec spec)
        {
            // 1. Windows Registry Policy
            if (!string.IsNullOrEmpty(spec.PolicyKey))
            {
                foreach (bool machine in new[] { true, false })
                {
                    int? en = GetRegistryDword(machine, spec.PolicyKey, "Enabled");
                    if (en.HasValue)
                    {
                        item.ByPolicy = true;
                        if (en.Value != 0)
                        {
                            item.Status = BrowserDohStatus.Bypasses;
                            item.Explanation = "Политикой включён DNS-over-HTTPS (обходит систему)";
                        }
                        else
                        {
                            item.Status = BrowserDohStatus.Follows;
                            item.Explanation = "Политикой DoH отключён (использует систему)";
                        }
                        return;
                    }
                }
            }

            // 2. prefs.js in default profile
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string root = Path.Combine(appData, spec.FirefoxRoot ?? @"Mozilla\Firefox");
            string iniPath = Path.Combine(root, "profiles.ini");
            if (File.Exists(iniPath))
            {
                string profileDir = ResolveFirefoxProfile(File.ReadAllText(iniPath), root);
                if (profileDir != null && Directory.Exists(profileDir))
                {
                    string prefsPath = Path.Combine(profileDir, "prefs.js");
                    if (File.Exists(prefsPath))
                    {
                        try
                        {
                            if(new FileInfo(prefsPath).Length>4*1024*1024)throw new IOException("Настройки слишком велики");
                            string prefs = File.ReadAllText(prefsPath);
                            var m = Regex.Matches(prefs, @"user_pref\(\s*""network\.trr\.mode""\s*,\s*(\d+)\s*\)\s*;");
                            if (m.Count > 0)
                            {
                                int val;
                                if (int.TryParse(m[m.Count - 1].Groups[1].Value, out val))
                                {
                                    if (val == 2 || val == 3)
                                    {
                                        item.Status = BrowserDohStatus.Bypasses;
                                        item.Explanation = "Включён собственный DoH (network.trr.mode=" + val + ") — обходит системный DNS";
                                        return;
                                    }
                                    if (val == 5)
                                    {
                                        item.Status = BrowserDohStatus.Follows;
                                        item.Explanation = "DoH отключён (использует системный DNS)";
                                        return;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            item.Status = BrowserDohStatus.Unknown;
            item.Explanation = "Не удалось подтвердить фактический DNS Firefox. Режим по умолчанию зависит от политик и версии.";
        }

        private static string ResolveFirefoxProfile(string ini, string root)
        {
            if (string.IsNullOrEmpty(ini)) return null;
            string defaultPath = null;
            string firstPath = null;

            using (var reader = new StringReader(ini))
            {
                string line;
                string currentPath = null;
                bool isRelative = true;
                bool isDefault = false;

                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        if (currentPath != null)
                        {
                            string full = isRelative ? Path.Combine(root, currentPath) : currentPath;
                            if (isDefault) return full;
                            if (firstPath == null) firstPath = full;
                        }
                        currentPath = null;
                        isRelative = true;
                        isDefault = false;
                    }
                    else
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string k = line.Substring(0, eq).Trim();
                            string v = line.Substring(eq + 1).Trim();
                            if (k.Equals("Path", StringComparison.OrdinalIgnoreCase)) currentPath = v.Replace('/', '\\');
                            if (k.Equals("IsRelative", StringComparison.OrdinalIgnoreCase)) isRelative = v != "0";
                            if (k.Equals("Default", StringComparison.OrdinalIgnoreCase)) isDefault = v == "1";
                        }
                    }
                }

                if (currentPath != null)
                {
                    string full = isRelative ? Path.Combine(root, currentPath) : currentPath;
                    if (isDefault) return full;
                    if (firstPath == null) firstPath = full;
                }
            }

            return defaultPath ?? firstPath;
        }

        private static string ExtractExeFromCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int q = cmd.IndexOf('"', 1);
                return q > 1 ? cmd.Substring(1, q - 1) : null;
            }
            int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return e > 0 ? cmd.Substring(0, e + 4) : null;
        }

        private static string GetRegistryString(bool machine, string subKey, string valueName)
        {
            try
            {
                var hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(subKey, false))
                    {
                        if (key != null)
                        {
                            object val = key.GetValue(valueName);
                            if (val is string) return (string)val;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static int? GetRegistryDword(bool machine, string subKey, string valueName)
        {
            try
            {
                var hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(subKey, false))
                    {
                        if (key != null)
                        {
                            object val = key.GetValue(valueName);
                            if (val is int) return (int)val;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string[] GetRegistrySubKeys(bool machine, string subKey)
        {
            var result = new List<string>();
            try
            {
                var hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(subKey, false))
                    {
                        if (key != null)
                        {
                            foreach (var name in key.GetSubKeyNames())
                            {
                                if (!result.Contains(name)) result.Add(name);
                            }
                        }
                    }
                }
            }
            catch { }
            return result.ToArray();
        }
    }
}
