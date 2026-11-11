using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DevProxyApp {
 // PAC applies only to software that honours the operating-system proxy settings.
 // It is generated locally; this class neither hosts it nor changes system settings.
 internal static class WindowsSelectiveProxy {
  const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
  const string State = @"Software\Dagami\SelectiveProxy";
  const string AutoConfig = "AutoConfigURL";
  const string Owner = "OwnedPac";

  [DllImport("wininet.dll", EntryPoint="InternetSetOptionW", SetLastError=true)]
  static extern bool InternetSetOption(IntPtr handle,int option,IntPtr data,int length);

  static void Refresh() {
   InternetSetOption(IntPtr.Zero,39,IntPtr.Zero,0); // INTERNET_OPTION_SETTINGS_CHANGED
   InternetSetOption(IntPtr.Zero,37,IntPtr.Zero,0); // INTERNET_OPTION_REFRESH
  }

  public static string ActiveUrl() {
   using(var state=Registry.CurrentUser.OpenSubKey(State))
   using(var settings=Registry.CurrentUser.OpenSubKey(InternetSettings)) {
    string owned=state==null?null:state.GetValue(Owner) as string;
    return !string.IsNullOrEmpty(owned)&&settings!=null&&
        string.Equals(settings.GetValue(AutoConfig) as string,owned,StringComparison.Ordinal)?owned:null;
   }
  }

  public static bool Disable() {
   using(var state=Registry.CurrentUser.OpenSubKey(State,true))
   using(var settings=Registry.CurrentUser.OpenSubKey(InternetSettings,true)) {
    string owned=state==null?null:state.GetValue(Owner) as string;
    if(string.IsNullOrEmpty(owned)||settings==null)return false;
    bool removed=string.Equals(settings.GetValue(AutoConfig) as string,owned,StringComparison.Ordinal);
    if(removed) {
     settings.DeleteValue(AutoConfig,false);
     Refresh();
    }
    state.DeleteValue(Owner,false);
    return removed;
   }
  }

  public static void Enable(int port) {
   if(port<1||port>65535)throw new ArgumentOutOfRangeException("port");
   string url="http://127.0.0.1:"+port+SelectiveProxyRules.Path;
   using(var settings=Registry.CurrentUser.CreateSubKey(InternetSettings))
   using(var state=Registry.CurrentUser.CreateSubKey(State)) {
    var current=settings.GetValue(AutoConfig);
    if((current!=null&&!object.Equals(current,"")&&!object.Equals(current,url)) ||
       object.Equals(settings.GetValue("ProxyEnable"),1))
     throw new InvalidOperationException("В Windows уже настроен другой прокси. Отключите его перед включением Dagami.");
    state.SetValue(Owner,url,RegistryValueKind.String);
    try {settings.SetValue(AutoConfig,url,RegistryValueKind.String);}
    catch {state.DeleteValue(Owner,false);throw;}
   }
   Refresh();
  }
 }

 public static class SelectiveProxyRules {
  public const string Path = "/dagami-selective.pac";

  internal static void ExportBrowserExtension(string directory,int port) {
   string config = "globalThis.dagamiConfig = " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
       new { port=port, pac=GeneratePac(ReadBuiltInDomains(),port,true) }) + ";\n";
   Directory.CreateDirectory(directory);
   foreach(string name in new[]{"popup.html","popup.js","route.js","background.js","config.js","manifest.json"}) {
    string destination = System.IO.Path.Combine(directory,name);
    string temporary = destination+"."+Guid.NewGuid().ToString("N")+".tmp";
    try {
     if(name=="config.js")File.WriteAllText(temporary,config,new UTF8Encoding(false));
     else using(var input = typeof(SelectiveProxyRules).Assembly.GetManifestResourceStream("DevProxy.BrowserExtension."+name)) {
      if(input == null)throw new InvalidOperationException("В сборке отсутствует файл расширения.");
      using(var output = new FileStream(temporary,FileMode.CreateNew))input.CopyTo(output);
     }
     if(File.Exists(destination))File.Replace(temporary,destination,null);
     else File.Move(temporary,destination);
    } finally { if(File.Exists(temporary))File.Delete(temporary); }
   }
  }

  public static string BuiltInPac(int port) {
   return GeneratePac(ReadBuiltInDomains(),port);
  }

  private static List<string> ReadBuiltInDomains() {
   var lines = new List<string>();
   using(var stream = typeof(SelectiveProxyRules).Assembly.GetManifestResourceStream("DevProxy.SelectiveDomains")) {
    if(stream == null) throw new InvalidOperationException("Dagami routing rules are missing.");
    using(var reader = new StreamReader(stream)) {
     string line;
     while((line = reader.ReadLine()) != null) lines.Add(line);
    }
   }
   return lines;
  }

  public static string GeneratePac(IEnumerable<string> lines,int port) {
   return GeneratePac(lines,port,false);
  }

  private static string GeneratePac(IEnumerable<string> lines,int port,bool browserFallback) {
   if(lines==null)throw new ArgumentNullException("lines");
   if(port<1||port>65535)throw new ArgumentOutOfRangeException("port");
   var exact=new SortedSet<string>(StringComparer.Ordinal);var suffix=new SortedSet<string>(StringComparer.Ordinal);
   foreach(string raw in lines){
    string value=(raw??"").Trim().ToLowerInvariant();if(value.Length==0||value.StartsWith("#"))continue;
    bool sub=value.StartsWith(".");string host=sub?value.Substring(1):value;
    if(host.Length>253||Uri.CheckHostName(host)!=UriHostNameType.Dns||host.IndexOfAny(new[]{'\"','\'','\\','\r','\n'})>=0)throw new InvalidDataException("Invalid routing domain");
    (sub?suffix:exact).Add(host);
   }
   if(exact.Count+suffix.Count==0)throw new InvalidDataException("Empty routing list");
   var s=new StringBuilder("function FindProxyForURL(url, host) {\n  host = host.toLowerCase();\n  if (host.charAt(host.length - 1) === '.') host = host.slice(0, -1);\n");
   string route="PROXY 127.0.0.1:"+port+(browserFallback?"; DIRECT":"");
   foreach(string host in exact)s.Append("  if (host === \"").Append(host).Append("\") return \"").Append(route).Append("\";\n");
   foreach(string host in suffix)s.Append("  if (host === \"").Append(host).Append("\" || dnsDomainIs(host, \".").Append(host).Append("\")) return \"").Append(route).Append("\";\n");
   return s.Append("  return \"DIRECT\";\n}\n").ToString();
  }
 }
}
