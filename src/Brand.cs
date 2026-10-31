using System;
using System.Drawing;
using System.Reflection;
[assembly: AssemblyTitle("Dagami Agent Proxy")]
[assembly: AssemblyProduct("Dagami Agent Proxy")]
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]
[assembly: AssemblyInformationalVersion("0.4.0-beta")]
namespace DevProxyApp {
    internal static class Brand {
        internal const string Name = "Dagami Agent Proxy";
        internal const string Version = "0.4.0 бета";
        internal const string ReleaseTag = "v0.4.0-beta";
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
