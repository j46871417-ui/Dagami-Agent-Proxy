using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using DevProxyApp;

class ConnectionRecoveryChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
 static void Parse(ConfigManager config,string json){typeof(ConfigManager).GetMethod("ParseJsonConfig",Private).Invoke(config,new object[]{json});}
 static ConfigManager Config(string folder){
  var config=new ConfigManager();Directory.CreateDirectory(folder);
  foreach(var item in new[]{new[]{"configDir",folder},new[]{"configFile",Path.Combine(folder,"config.json")},new[]{"localConfigDir",Path.Combine(folder,"fallback")},new[]{"localConfigFile",Path.Combine(folder,"fallback","config.json")}})
   typeof(ConfigManager).GetField(item[0],Private).SetValue(config,item[1]);
  return config;
 }
 static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"Dagami-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try {
   var defaults=new ConfigManager();
   Assert(defaults.EffectiveProxy==ManagedAccess.Profile&&!defaults.UseDagamiDns,"A new install must use the Dagami gateway without a Smart DNS bypass");
   int probeCalls=0;
   var route=ConnectionDiagnostics.Check(12345,port=>{probeCalls++;Assert(port==12345,"Probe did not use selected bridge");return "HTTP 401 expected without token";});
   Assert(route.Available&&route.Status==401&&probeCalls==1,"Effective Smart DNS/VPN route rejected because no upstream was tested");
   Assert(!ConnectionDiagnostics.Check(12345,port=>{throw new IOException("fixture");}).Available,"Broken route reported available");
   Assert(!ConnectionDiagnostics.Check(0,port=>{throw new Exception("Must not call");}).Available,"Unstarted bridge probed");
   var legacy=Config(Path.Combine(root,"legacy"));
   var normalize=typeof(MainForm).GetMethod("NormalizeSavedProxy",BindingFlags.NonPublic|BindingFlags.Static);
   string savedProxy="http://fixture-user:fixture-password@proxy.invalid:8080";
   var custom=typeof(MainForm).GetMethod("NormalizeCustomProxy",BindingFlags.NonPublic|BindingFlags.Static);
   Assert((string)custom.Invoke(null,new object[]{"127.0.0.1:7897",53200})=="http://127.0.0.1:7897","Bare host and port did not select HTTP");
   Assert((string)custom.Invoke(null,new object[]{"https://user:p%40ss@[::1]:8443",53200})=="https://user:p%40ss@[::1]:8443","Escaped credentials or IPv6 were changed");
   Assert((string)custom.Invoke(null,new object[]{"direct",53200})=="direct","System route was rejected");
   foreach(string invalid in new[]{"",ManagedAccess.Profile,"http://127.0.0.1:53200","https://dns.dagami.ru/dns-query","http://proxy.invalid/path"}){
    bool rejected=false;
    try {custom.Invoke(null,new object[]{invalid,53200});}
    catch(TargetInvocationException error){rejected=error.InnerException is ArgumentException;}
    Assert(rejected,"Invalid custom proxy was accepted: "+invalid);
   }
   Assert((string)normalize.Invoke(null,new object[]{savedProxy,false,true})==savedProxy,"Saving DNS-only settings erased disabled proxy credentials");
   foreach(bool proxyEnabled in new[]{false,true})foreach(bool dnsEnabled in new[]{false,true}){
    legacy.LastProxy=savedProxy;legacy.UseProxy=proxyEnabled;legacy.UseDagamiDns=dnsEnabled;
    Assert(legacy.EffectiveProxy==(proxyEnabled?savedProxy:"direct"),"Route flags did not control effective upstream");
    Assert(legacy.Save(),legacy.LastSaveError);
    var loaded=Config(Path.Combine(root,"legacy"));
    Parse(loaded,File.ReadAllText(Path.Combine(root,"legacy","config.json")));
    Assert(loaded.LastProxy==savedProxy&&loaded.UseProxy==proxyEnabled&&loaded.UseDagamiDns==dnsEnabled,"Saved flags lost the original proxy");
   }
   var select=typeof(ManagedAccess).GetMethod("ApplyProfileSettings",BindingFlags.NonPublic|BindingFlags.Static);
   legacy.ChangeConnection(cfg=>select.Invoke(null,new object[]{cfg}));
   Assert(legacy.EffectiveProxy==ManagedAccess.Profile&&legacy.SavedCustomProxy==savedProxy,"Selecting Dagami erased the manual proxy");
   var reloaded=Config(Path.Combine(root,"legacy"));
   string serialized=File.ReadAllText(Path.Combine(root,"legacy","config.json"));
   Assert(!serialized.Contains("fixture-password"),"Manual proxy credentials were written in plaintext");
   Parse(reloaded,serialized);
   Assert(reloaded.EffectiveProxy==ManagedAccess.Profile&&reloaded.SavedCustomProxy==savedProxy,"Manual proxy was lost after restarting on Dagami");
   reloaded.UseProxy=false;reloaded.UseDagamiDns=true;
   var useCustom=typeof(ConfigManager).GetMethod("SelectCustomRoute",Private);
   useCustom.Invoke(reloaded,new object[]{reloaded.SavedCustomProxy});
   Assert(reloaded.EffectiveProxy==savedProxy&&reloaded.UseProxy&&!reloaded.UseDagamiDns,"Selecting custom kept a disabled proxy or a Smart DNS bypass");
   Parse(legacy,"{\"use_dagami_dns\":true,\"dns_host\":\"dns.dagami.ru\",\"dns_obfuscation\":true,\"block_antigravity_updates\":true}");
   Assert(!legacy.DnsObfuscation&&!legacy.BlockAntigravityUpdates&&legacy.UseDagamiDns,"Unsafe legacy flags survived migration or DNS was disabled");
   Parse(legacy,"{\"connection_policy_version\":1,\"dns_obfuscation\":true,\"block_antigravity_updates\":true}");
   Assert(legacy.DnsObfuscation&&legacy.BlockAntigravityUpdates,"Explicit opt-in was discarded");
   Assert(legacy.Save(),legacy.LastSaveError);
   string file=Path.Combine(root,"legacy","config.json"),before=File.ReadAllText(file);
   Assert(Convert.ToInt32(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(before)["connection_policy_version"])==1,"Policy choice not persisted");
   legacy.LastProxy="direct";
   using(var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
    Assert(!legacy.Save()&&!string.IsNullOrEmpty(legacy.LastSaveError),"Atomic replacement failure was reported as saved");
    Assert(File.ReadAllText(file)==before,"Failed save truncated/overwrote the active configuration");
    Assert(!File.Exists(Path.Combine(root,"legacy","fallback","config.json")),"Failed save created a stale fallback");
    bool failed=false;string oldDns=legacy.DnsHost,oldCustom=legacy.CustomProxy;bool oldFlag=legacy.UseDagamiDns;
    try{legacy.ChangeConnection(cfg=>{cfg.DnsHost="fixture.invalid";cfg.UseDagamiDns=false;cfg.LastProxy=cfg.CustomProxy="http://fixture.invalid:8080";});}catch(IOException){failed=true;}
    Assert(failed&&legacy.DnsHost==oldDns&&legacy.UseDagamiDns==oldFlag&&legacy.LastProxy=="direct"&&legacy.CustomProxy==oldCustom,"Failed save changed effective connection settings");
   }
   Assert(Directory.GetFiles(Path.GetDirectoryName(file),"*.tmp").Length==0,"Failed config save leaked temp files");
   string folder=Path.Combine(root,"editor");Directory.CreateDirectory(folder);
   string exe=Path.Combine(folder,"Code.exe");File.Copy(typeof(MainForm).Assembly.Location,exe);
   var app=new AppTarget{Id="recovery-editor",ExePath=exe,ConfigPath=Path.Combine(folder,"settings.json")};
   File.WriteAllText(app.ConfigPath,"{\"http.proxy\":\"http://original.invalid:80\",\"update.mode\":\"manual\",\"extensions.autoUpdate\":false}");
   string message;
   Assert(ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),message);
   string identicalSettings=File.ReadAllText(app.ConfigPath);
   File.WriteAllText(app.ConfigPath,identicalSettings,new System.Text.UTF8Encoding(true));
   Assert(ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),message);
   byte[] normalized=File.ReadAllBytes(app.ConfigPath);
   Assert(!(normalized.Length>=3&&normalized[0]==239&&normalized[1]==187&&normalized[2]==191)&&File.ReadAllText(app.ConfigPath)==identicalSettings,"Identical legacy settings retained BOM rejected by IDE JSON.parse");
   var serializer=new JavaScriptSerializer();var settings=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(app.ConfigPath));
   settings["update.mode"]="default";settings["extensions.autoUpdate"]=true;settings["http.proxy"]="http://manual.invalid:80";
   File.WriteAllText(app.ConfigPath,serializer.Serialize(settings));
   Assert(ProxyEngine.RestoreTarget(app,out message),message);
   settings=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(app.ConfigPath));
   Assert((string)settings["http.proxy"]=="http://original.invalid:80"&&(string)settings["update.mode"]=="default"&&(bool)settings["extensions.autoUpdate"],"Rollback changed unrelated update preferences or kept manual proxy");
   Assert(ProxyEngine.RestoreTarget(app,out message),message);
   settings=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(app.ConfigPath));
   Assert(!settings.ContainsKey("http.proxy")&&settings.ContainsKey("update.mode")&&(bool)settings["extensions.autoUpdate"],"Backup-free restore removed unrelated preferences");
   string original=File.ReadAllText(app.ConfigPath);
   using(var locked=new FileStream(app.ConfigPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
    Assert(!ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12346",out message)&&File.ReadAllText(app.ConfigPath)==original,"Settings replacement failure fell back to destructive in-place write");
   Assert(Directory.GetFiles(folder,"*.tmp").Length==0,"Settings temp file not cleaned");
   Console.WriteLine("PASS "+checks+" migration, explicit opt-in, transactional config save, locked-file preservation and rollback ownership checks");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally {
   string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-recovery-"))throw new IOException("Unsafe cleanup");
   Directory.Delete(root,true);
  }
 }
}
