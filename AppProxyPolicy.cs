using System;
using System.Diagnostics;
using System.IO;

namespace DevProxyApp {
    public static class AppProxyPolicy {
        public static bool IsGemini(AppTarget target) {
            string name=Path.GetFileName(target.ExePath ?? "");
            return name.Equals("Gemini.exe",StringComparison.OrdinalIgnoreCase) || name.Equals("GeminiAppLauncher.exe",StringComparison.OrdinalIgnoreCase);
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
            string flags=ChromiumArguments(proxy);
            start.Arguments=(start.Arguments ?? "")+" "+flags;
            // Embedded runtimes ignore HTTP_PROXY. Keep the normal account profile.
            foreach(string variable in new[]{"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS","QTWEBENGINE_CHROMIUM_FLAGS"}) {
                string existing=start.EnvironmentVariables[variable];
                start.EnvironmentVariables[variable]=(string.IsNullOrWhiteSpace(existing)?"":existing+" ")+flags;
            }
        }
    }
}
