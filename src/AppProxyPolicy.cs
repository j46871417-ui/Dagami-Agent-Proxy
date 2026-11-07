using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;

namespace DevProxyApp {
    public static class AppProxyPolicy {
        public static LocalProxyBridge CreateGeminiBridge(AppTarget target,bool useProxy,string upstream) {
            if(!IsGemini(target)||!useProxy||MainForm.IsDirectProxyValue(upstream))return null;
            var bridge=new LocalProxyBridge();
            if(!bridge.Start(0,upstream,null)){bridge.Dispose();throw new IOException("Не удалось открыть отдельный мост Gemini через внешний прокси.");}
            return bridge;
        }
        public static string ManagedChromiumArguments(string existing, string proxy) {
            var keep = new List<string>();
            var routing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "--no-proxy-server", "--proxy-server", "--proxy-bypass-list",
                "--proxy-pac-url", "--proxy-auto-detect", "--disable-quic"
            };
            string[] args = NativeCliApps.SplitArguments(existing ?? "");
            for (int i = 0; i < args.Length; i++) {
                int equals = args[i].IndexOf('=');
                string name = equals < 0 ? args[i] : args[i].Substring(0, equals);
                if (!routing.Contains(name)) { keep.Add(NativeShortcuts.Quote(args[i])); continue; }
                if (equals < 0 && (name.Equals("--proxy-server", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("--proxy-bypass-list", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("--proxy-pac-url", StringComparison.OrdinalIgnoreCase)) &&
                    i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
            }
            keep.Add(ChromiumArguments(proxy));
            return string.Join(" ", keep.ToArray());
        }
        public static bool IsGemini(AppTarget target) {
            string name=Path.GetFileName(target.ExePath ?? "");
            return (name.Equals("Gemini.exe",StringComparison.OrdinalIgnoreCase) || name.Equals("GeminiAppLauncher.exe",StringComparison.OrdinalIgnoreCase)) && !NativeCliApps.IsCli(target);
        }
        public static string Executable(AppTarget target) {
            if(IsGemini(target) && Path.GetFileName(target.ExePath).Equals("GeminiAppLauncher.exe",StringComparison.OrdinalIgnoreCase)) {
                string main=Path.Combine(Path.GetDirectoryName(target.ExePath),"Gemini.exe");
                if(File.Exists(main))return main;
            }
            return target.ExePath;
        }
        public static string ChromiumArguments(string localProxy) {
            Uri uri;
            if(!Uri.TryCreate(localProxy,UriKind.Absolute,out uri) || uri.Scheme!="http" || uri.Host!="127.0.0.1" || uri.Port<=0 || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Expected a local proxy bridge.");
            return "--proxy-server=\""+localProxy+"\" --proxy-bypass-list=\"localhost;127.0.0.1;[::1]\" --disable-quic";
        }
        public static void ConfigureGemini(ProcessStartInfo start,string proxy) {
            start.Arguments=ManagedChromiumArguments(start.Arguments,proxy);
            // Embedded runtimes ignore HTTP_PROXY. Keep the normal account profile.
            foreach(string variable in new[]{"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS","QTWEBENGINE_CHROMIUM_FLAGS"}) {
                string existing=start.EnvironmentVariables[variable];
                start.EnvironmentVariables[variable]=ManagedChromiumArguments(existing,proxy);
            }
        }
    }
}
