using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;

namespace DevProxyApp {
 public static class ApplicationSupport {
  static bool Name(AppTarget target, params string[] names) { string file=Path.GetFileName(target.ExePath??"");return Array.Exists(names,x=>file.Equals(x,StringComparison.OrdinalIgnoreCase)); }
  public static string Kind(AppTarget target) {
   if(target==null)return "unknown"; string id=(target.Id??"").ToLowerInvariant();
   if(id=="codex"||id.EndsWith("_cli"))return "cli";
   if(id=="codex-gui"||Name(target,"Codex.exe")&&!NativeCliApps.IsCli(target))return "codex-desktop";
   if(id=="opencode-gui"||Name(target,"OpenCode.exe")&&!NativeCliApps.IsCli(target))return "opencode-desktop";
   if(id.StartsWith("jb_")||IsJetBrains(target.ExePath))return "jetbrains";
   if(id=="git"||Name(target,"git.exe"))return "git";
   if(Name(target,"cmd.exe","powershell.exe","pwsh.exe"))return "terminal";
   if(Name(target,"Antigravity.exe","Code.exe","Code - Insiders.exe","Cursor.exe","Windsurf.exe","VSCodium.exe")||
      id=="antigravity"||id.StartsWith("vscode")||id=="cursor"||id=="windsurf"||id=="vscodium")return "vscode";
   if(AppProxyPolicy.IsGemini(target))return "webview";
   return NativeCliApps.IsCli(target)?"cli":"unknown";
  }
  public static bool IsJetBrains(string executable) {
   if(string.IsNullOrEmpty(executable))return false;
   string name=Path.GetFileName(executable).ToLowerInvariant();
   return Array.Exists(new[]{"idea64.exe","pycharm64.exe","webstorm64.exe","phpstorm64.exe","clion64.exe","goland64.exe","rider64.exe","datagrip64.exe","rubymine64.exe","rustrover64.exe","dataspell64.exe"},x=>x==name)&&File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable)),"product-info.json"));
  }
  public static string SettingsPath(AppTarget target) {
   string kind=Kind(target),root=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
   if(kind=="jetbrains") {
    string product=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target.ExePath)),"product-info.json");
    if(!File.Exists(product))return target.ConfigPath;
    var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(product));object value;
    if(data==null)return null;
    string directory=data.TryGetValue("dataDirectoryName",out value)?Convert.ToString(value):"";
    if(string.IsNullOrWhiteSpace(directory)||directory.IndexOfAny(Path.GetInvalidFileNameChars())>=0||directory=="."||directory=="..")return null;
    return Path.Combine(root,"JetBrains",directory,"options","proxy.settings.xml");
   }
   if(kind!="vscode")return null;
   string file=Path.GetFileName(target.ExePath??"").ToLowerInvariant(),id=(target.Id??"").ToLowerInvariant();
   string folder=file=="cursor.exe"||id=="cursor"?"Cursor":file=="windsurf.exe"||id=="windsurf"?"Windsurf":file=="vscodium.exe"||id=="vscodium"?"VSCodium":file=="antigravity.exe"||id=="antigravity"?"Antigravity":file=="code - insiders.exe"?"Code - Insiders":"Code";
   if(!string.IsNullOrEmpty(target.ExePath)) {
    string portable=Path.Combine(Path.GetDirectoryName(target.ExePath),"data");
    string portableSettings=Path.Combine(portable,"user-data","User","settings.json");
    if(Directory.Exists(portable))return portableSettings;
    if(string.Equals(target.ConfigPath,portableSettings,StringComparison.OrdinalIgnoreCase))return Path.Combine(root,folder,"User","settings.json");
   }
   return !string.IsNullOrEmpty(target.ConfigPath)?target.ConfigPath:Path.Combine(root,folder,"User","settings.json");
  }
  public static bool SupportsSettings(AppTarget target) {if(target==null)return false;string kind=Kind(target);return kind=="vscode"||kind=="jetbrains"||kind=="unknown";}
  public static void ResolveSettings(AppTarget target) {
   string path=SettingsPath(target);
   if(path==null&&Kind(target)=="jetbrains")throw new InvalidDataException("Каталог настроек JetBrains не определён: проверьте product-info.json. Прежний путь не используется.");
   if(path!=null)target.ConfigPath=path;
  }
  public static string LocalSettingsProxy(string value) {
   Uri uri;if(Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.Scheme=="http"&&uri.Host=="127.0.0.1"&&string.IsNullOrEmpty(uri.UserInfo))return uri.AbsoluteUri.TrimEnd('/');
   return "http://127.0.0.1:53200";
  }
  public static void ConfigureProcess(ProcessStartInfo start,string localProxy) {
   AppProxyPolicy.ChromiumArguments(localProxy); // Validate the shared bridge contract.
   foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy"})start.EnvironmentVariables[key]=localProxy;
   start.EnvironmentVariables["NO_PROXY"]=start.EnvironmentVariables["no_proxy"]="localhost,127.0.0.1,::1";
   start.EnvironmentVariables["NODE_USE_ENV_PROXY"]="1";
  }
  public static void ConfigureLaunch(AppTarget target,ProcessStartInfo start,string localProxy) {
   ConfigureProcess(start,localProxy);string kind=Kind(target);
   if(kind=="opencode-desktop") {
    string current=start.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"];
    start.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"]=(current??"")+" "+AppProxyPolicy.ChromiumArguments(localProxy);
    if(IsElectronDesktop(target))start.Arguments+=" "+AppProxyPolicy.ChromiumArguments(localProxy);
   }
   if(kind=="jetbrains") {
    var uri=new Uri(localProxy);string existing=start.EnvironmentVariables["JAVA_TOOL_OPTIONS"];
    start.EnvironmentVariables["JAVA_TOOL_OPTIONS"]=(existing??"")+" -Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort="+uri.Port+" -Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort="+uri.Port+" -Dhttp.nonProxyHosts=localhost|127.*|[::1]";
   }
   if(kind=="git") {
    int count;string raw=start.EnvironmentVariables["GIT_CONFIG_COUNT"];
    if(!string.IsNullOrEmpty(raw)&&(!int.TryParse(raw,out count)||count<0||count>64))throw new InvalidOperationException("Некорректный GIT_CONFIG_COUNT.");
    count=string.IsNullOrEmpty(raw)?0:int.Parse(raw);
    foreach(var pair in new[]{new[]{"http.proxy",localProxy},new[]{"http.sslVerify","true"}}) {start.EnvironmentVariables["GIT_CONFIG_KEY_"+count]=pair[0];start.EnvironmentVariables["GIT_CONFIG_VALUE_"+count]=pair[1];count++;}
    start.EnvironmentVariables["GIT_CONFIG_COUNT"]=count.ToString();
   }
  }
  public static bool IsElectronDesktop(AppTarget target) {
   if(target==null||string.IsNullOrEmpty(target.ExePath))return false;
   string root=Path.GetDirectoryName(target.ExePath);
   return File.Exists(Path.Combine(root,"resources","app.asar"))||File.Exists(Path.Combine(root,"resources","app","package.json"));
  }
  public static string Describe(AppTarget target) {
   switch(Kind(target)) {
    case "vscode":return "Патч: настройки IDE, обычные ярлыки, Chromium и окружение дочерних процессов. Профиль аккаунта сохраняется.";
    case "codex-desktop":return "Патч: ярлыки, Chromium и окружение backend. config.toml и настройки sandbox не меняются.";
    case "opencode-desktop":return "Патч: ярлыки, Electron/WebView2 по структуре установки и окружение backend. Старый фоновый сервер нужно закрыть. JSON-ключ proxy не добавляется.";
    case "jetbrains":return "Патч: proxy.settings.xml, JVM и ярлыки. Gradle/Maven/SSH могут иметь отдельные настройки.";
    case "git":return "Патч: отдельная команда dagami-git и HTTP(S)-окружение. Обычный git и глобальный .gitconfig сохраняются; SSH требует своей настройки.";
    case "terminal":return "Патч: ярлык терминала с окружением прокси для дочерних команд. Системные команды в PATH не заменяются.";
    case "cli":return "Патч: команда CLI в пользовательском PATH и окружение прокси; параметры моделей и авторизации сохраняются.";
    case "webview":return "Патч: ярлыки и параметры WebView2/Chromium/Qt при запуске.";
    default:return "Патч: управляемый ярлык и окружение HTTP(S). Собственная поддержка прокси приложением требует проверки.";
   }
  }
 }
}
