using System;
using System.Drawing;
using System.Reflection;
[assembly: AssemblyTitle("Dagami Agent Proxy")]
[assembly: AssemblyProduct("Dagami Agent Proxy")]
[assembly: AssemblyVersion("0.3.6.0")]
[assembly: AssemblyFileVersion("0.3.6.0")]
[assembly: AssemblyInformationalVersion("0.3.6-alpha")]
namespace DevProxyApp {
    internal static class Brand {
        internal const string Name = "Dagami Agent Proxy";
        internal const string Version = "0.3.6 альфа";
        internal const string ReleaseTag = "v0.3.6-alpha";
        internal static Image LoadImage() {
            using(var stream=typeof(Brand).Assembly.GetManifestResourceStream("Dagami.AgentProxy.png"))
            using(var image=Image.FromStream(stream)) return new Bitmap(image);
        }
        internal static Icon LoadIcon() {
            using (var stream = typeof(Brand).Assembly.GetManifestResourceStream("Dagami.AgentProxy.ico")) {
                if (stream == null) throw new InvalidOperationException("Application icon is missing.");
                using (var icon = new Icon(stream)) return (Icon)icon.Clone();
            }
        }
    }
}
