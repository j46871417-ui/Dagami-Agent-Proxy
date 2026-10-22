using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using DevProxyApp;

class ApplicationSupportChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 class Paths:ICliPathEnvironment {
  public string UserPath{get;set;}public string ProcessPath{get;set;}public string MachinePath{get;set;}public void Notify(){}
 }
 static string Self{get{return Process.GetCurrentProcess().MainModule.FileName;}}
 [STAThread]static int Main(string[] args) {
  if(args.Length>0&&args[0]=="child") {Console.WriteLine(new JavaScriptSerializer().Serialize(new {proxy=Environment.GetEnvironmentVariable("HTTPS_PROXY"),node=Environment.GetEnvironmentVariable("NODE_USE_ENV_PROXY"),git=Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"),args=args}));return 0;}
  string root=Path.Combine(Path.GetTempPath(),"Dagami-apps-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try {
   File.Copy(typeof(MainForm).Assembly.Location,Path.Combine(root,"DagamiAgentProxy.exe"));
   string otherCopy=Path.Combine(root,Path.GetFileName(Self));File.Copy(Self,otherCopy);NativeLogin.RequireStopped(otherCopy);
   bool sameRunning=false;try{NativeLogin.RequireStopped(Self);}catch(InvalidOperationException){sameRunning=true;}Assert(sameRunning,"Running exact executable was not detected");
   const string local="http://127.0.0.1:12345";string message;
   foreach(string name in new[]{"Code","Cursor","Windsurf","VSCodium","Antigravity"}) {
    string folder=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(folder,"data"));
    string exe=Path.Combine(folder,name+".exe");File.Copy(typeof(MainForm).Assembly.Location,exe);
    var target=new AppTarget{Id="manual-"+name,ExePath=exe};ApplicationSupport.ResolveSettings(target);
    Assert(target.ConfigPath==Path.Combine(folder,@"data\user-data\User\settings.json"),"Portable profile not found: "+name);
    Assert(ProxyEngine.ApplyProxyToTarget(target,local,out message),message);
    var settings=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(target.ConfigPath));
    Assert((string)settings["http.proxy"]==local&&(bool)settings["http.proxyStrictSSL"],"Wrong proxy/TLS: "+name);
    Assert(settings["http.noProxy"] is System.Collections.ArrayList,"VS Code noProxy must be an array");
    string flags=NativeLogin.ElectronArguments(target,local,Path.Combine(root,"unused"));
    Assert(flags.Contains("--disable-quic")&&!flags.Contains("user-data-dir"),"Wrong profile: "+name);
    Assert(File.Exists(target.ConfigPath+".devproxy.bak"),"New configuration has no rollback");
    Assert(ProxyEngine.RestoreTarget(target,out message),message);
    Assert(!File.ReadAllText(target.ConfigPath).Contains("http.proxy"),"Restore retained proxy");
   }
   var retryTarget=new AppTarget{Id="vscode-retry",ExePath=Path.Combine(root,@"Code\Code.exe"),ConfigPath=Path.Combine(root,@"Code\data\user-data\User\settings.json")};
   Assert(ProxyEngine.ApplyProxyToTarget(retryTarget,local,out message),message);
   File.AppendAllText(retryTarget.ConfigPath,"\n");
   byte[] currentBytes=File.ReadAllBytes(retryTarget.ConfigPath),backupBytes=File.ReadAllBytes(retryTarget.ConfigPath+".devproxy.bak");
   bool failed=false;try{string summary;NativeAppPatcher.Apply(retryTarget,Path.Combine(root,"missing.exe"),new[]{root},new NativeShortcuts(Path.Combine(root,"links-state")),"http://user:secret@upstream.invalid:8080",out summary,true);}catch(Exception){failed=true;}
   Assert(failed,"Invalid manager accepted");
   Assert(Convert.ToBase64String(currentBytes)==Convert.ToBase64String(File.ReadAllBytes(retryTarget.ConfigPath)),"Failed repeated patch changed current settings");
   Assert(Convert.ToBase64String(backupBytes)==Convert.ToBase64String(File.ReadAllBytes(retryTarget.ConfigPath+".devproxy.bak")),"Failed repeated patch changed original backup");
   Assert(!File.ReadAllText(retryTarget.ConfigPath).Contains("secret"),"Upstream credentials were written into IDE settings");
   string jb=Path.Combine(root,"JB"),bin=Path.Combine(jb,"bin");Directory.CreateDirectory(bin);
   string idea=Path.Combine(bin,"idea64.exe");File.Copy(typeof(MainForm).Assembly.Location,idea);File.Copy(typeof(MainForm).Assembly.Location,Path.Combine(bin,"unrelated64.exe"));
   string product=Path.Combine(jb,"product-info.json");File.WriteAllText(product,"{\"name\":\"IDEA fixture\",\"dataDirectoryName\":\"IdeaFixture2099.1\"}");
   var found=ApplicationDiscovery.JetBrains(new[]{root});Assert(found.Count==1&&found[0].ExePath==idea,"Wrong JetBrains launcher selected");
   Assert(found[0].ConfigPath.EndsWith(@"JetBrains\IdeaFixture2099.1\options\proxy.settings.xml"),"JetBrains versioned settings path wrong");
   File.WriteAllText(product,"{\"dataDirectoryName\":\"../escape\"}");Assert(ApplicationSupport.SettingsPath(found[0])==null,"Product directory escapes settings root");
   File.Delete(product);
   string xml=Path.Combine(root,"proxy.settings.xml");File.WriteAllText(xml,"<application><component name=\"HttpConfigurable\"><option name=\"PROXY_HOST\" value=\"original\"/><option name=\"KEEP_PROXY_PASSWORD\" value=\"true\"/></component><component name=\"Other\"><option name=\"value\" value=\"untouched\"/></component></application>");
   var jetbrains=new AppTarget{Id="jb_fixture",ExePath=idea,ConfigPath=xml};
   Assert(ProxyEngine.ApplyProxyToTarget(jetbrains,local,out message),message);
   string originalBackup=File.ReadAllText(xml+".devproxy.bak");
   Assert(ProxyEngine.ApplyProxyToTarget(jetbrains,"http://127.0.0.1:12346",out message),message);
   Assert(File.ReadAllText(xml+".devproxy.bak")==originalBackup,"Repeated patch replaced original settings backup");
   File.WriteAllText(xml,File.ReadAllText(xml).Replace("127.0.0.1","manual-new-proxy"));
   Assert(ProxyEngine.RestoreTarget(jetbrains,out message),message);
   string restored=File.ReadAllText(xml);Assert(restored.Contains("original")&&!restored.Contains("manual-new-proxy")&&restored.Contains("untouched")&&restored.Contains("KEEP_PROXY_PASSWORD"),"XML rollback lost unrelated settings or retained manual proxy");
   File.WriteAllText(xml,"<!DOCTYPE application [<!ENTITY x SYSTEM 'file:///not-read'>]><application>&x;</application>");
   Assert(!ProxyEngine.ApplyProxyToTarget(jetbrains,local,out message)&&!File.Exists(xml+".devproxy.bak"),"Unsafe XML was accepted or backed up");
   string git=Path.Combine(root,"git.exe");File.Copy(Self,git);var gt=new AppTarget{Id="git",ExePath=git};
   var environment=new Paths{UserPath="",ProcessPath="",MachinePath=root};var store=new NativeCliApps(Path.Combine(root,"cli"),environment);
   string shim=store.Enable(gt,typeof(MainForm).Assembly.Location);
   Assert(Path.GetFileName(shim)=="dagami-git.exe"&&!File.Exists(Path.Combine(store.BinDirectory,"git.exe")),"Git original command was shadowed");
   string issue;Assert(store.VerifiedCount(gt.Id,out issue)==1&&issue==null,"Git alias not verified");store.Restore(gt.Id);
   var start=NativeCliApps.CreateStart(git,new[]{"child","space value","","literal\"quote"},local,root);
   Assert(start.EnvironmentVariables["GIT_CONFIG_KEY_0"]=="http.proxy"&&start.EnvironmentVariables["GIT_CONFIG_VALUE_0"]==local&&start.EnvironmentVariables["GIT_CONFIG_VALUE_1"]=="true","Git process config/TLS missing");
   start.RedirectStandardOutput=true;start.CreateNoWindow=true;using(var child=Process.Start(start)){string output=child.StandardOutput.ReadToEnd();child.WaitForExit();Assert(child.ExitCode==0&&output.Contains("12345")&&output.Contains("space value"),"Real child did not inherit launch arguments/proxy");}
   string terminal=Path.Combine(root,"powershell.exe");File.Copy(Self,terminal);
   Assert(NativeCliApps.IsCli(new AppTarget{ExePath=terminal})&&!NativeCliApps.UsesCommandShim(new AppTarget{ExePath=terminal}),"Terminal system command would be shadowed");
   string gemini=Path.Combine(root,"gemini.exe");File.Copy(Self,gemini);Assert(!AppProxyPolicy.IsGemini(new AppTarget{ExePath=gemini}),"Gemini CLI receives Desktop Chromium flags");
   foreach(string id in new[]{"codex-gui","opencode-gui"}) {
    var target=new AppTarget{Id=id,ExePath=typeof(MainForm).Assembly.Location};var launch=new ProcessStartInfo(Self){UseShellExecute=false};ApplicationSupport.ConfigureLaunch(target,launch,local);
    Assert(launch.EnvironmentVariables["HTTPS_PROXY"]==local&&launch.EnvironmentVariables["NO_PROXY"].Contains("127.0.0.1"),"Desktop child env missing");
    Assert(ApplicationSupport.SettingsPath(target)==null,"Desktop would edit unsupported JSON/TOML config");
   }
   string desktop=Path.Combine(root,"desktop");Directory.CreateDirectory(Path.Combine(desktop,"resources"));File.WriteAllText(Path.Combine(desktop,@"resources\app.asar"),"fixture");
   var oc=new AppTarget{Id="opencode-gui",ExePath=Path.Combine(desktop,"OpenCode.exe")};var ocStart=new ProcessStartInfo(Self){UseShellExecute=false};ApplicationSupport.ConfigureLaunch(oc,ocStart,local);
   Assert(ocStart.Arguments.Contains("--proxy-server")&&ocStart.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"].Contains("--disable-quic"),"OpenCode generation-specific runtime flags missing");
   string[] values={"space value","","literal\"quote","trailing\\","&|<>%USERPROFILE%!","русский"};
   string joined=string.Join(" ",Array.ConvertAll(values,NativeShortcuts.Quote));var split=NativeCliApps.SplitArguments(joined);
   Assert(split.Length==values.Length,"Arguments count changed");for(int i=0;i<values.Length;i++)Assert(split[i]==values[i],"Argument changed at "+i);
   var targets=ProxyEngine.ScanAllTargets();foreach(string id in new[]{"codex-gui","opencode-gui","git","gemini_cli","claude_cli"})Assert(targets.Exists(t=>t.Id==id),"Advertised target missing: "+id);
   foreach(string id in new[]{"powershell","pwsh","cmd"})Assert(!targets.Exists(t=>t.Id==id)||ConfigManager.Instance.CustomTargets.Exists(t=>t.Id==id)||NativeCliApps.Current.RegisteredTargets().Exists(t=>t.Id==id)||NativeShortcuts.Current.RegisteredTargets().Exists(t=>t.Id==id),"Unrequested terminal added automatically: "+id);
   Console.WriteLine("PASS "+checks+" application structures, settings/rollback, discovery, Git alias, Desktop policy and real child launch checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally {
   string full=Path.GetFullPath(root),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("Dagami-apps-"))throw new IOException("Unsafe cleanup");
   Directory.Delete(full,true);
  }
 }
}
