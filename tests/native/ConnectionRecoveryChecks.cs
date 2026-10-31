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
   var adopt=typeof(ConfigManager).GetMethod("CanAdoptDetectedProxy",BindingFlags.Static|BindingFlags.NonPublic);
   Assert(!(bool)adopt.Invoke(null,new object[]{"http://127.0.0.1:53200",false}),"Own bridge adopted as upstream");
   Assert(!(bool)adopt.Invoke(null,new object[]{"http://localhost:53209",false}),"Reserve bridge adopted as upstream");
   Assert(!(bool)adopt.Invoke(null,new object[]{"http://127.0.0.1:61000",true}),"Managed dynamic bridge adopted as upstream");
   Assert((bool)adopt.Invoke(null,new object[]{"http://127.0.0.1:7890",false}),"Independent local VPN proxy rejected");
   Assert((bool)adopt.Invoke(null,new object[]{"http://proxy.example:8080",true}),"Explicit external proxy in IDE rejected");
   int probeCalls=0;
   var route=ConnectionDiagnostics.Check(12345,port=>{probeCalls++;Assert(port==12345,"Probe did not use selected bridge");return "HTTP 401 expected without token";});
   Assert(route.Available&&route.Status==401&&probeCalls==1,"Effective Smart DNS/VPN route rejected because no upstream was tested");
   Assert(!ConnectionDiagnostics.Check(12345,port=>{throw new IOException("fixture");}).Available,"Broken route reported available");
   Assert(!ConnectionDiagnostics.Check(0,port=>{throw new Exception("Must not call");}).Available,"Unstarted bridge probed");
   var legacy=Config(Path.Combine(root,"legacy"));
   var normalize=typeof(MainForm).GetMethod("NormalizeSavedProxy",BindingFlags.NonPublic|BindingFlags.Static);
   string savedProxy="http://fixture-user:fixture-password@proxy.invalid:8080";
   Assert((string)normalize.Invoke(null,new object[]{savedProxy,false,true})==savedProxy,"Saving DNS-only settings erased disabled proxy credentials");
   foreach(bool proxyEnabled in new[]{false,true})foreach(bool dnsEnabled in new[]{false,true}){
    legacy.LastProxy=savedProxy;legacy.UseProxy=proxyEnabled;legacy.UseDagamiDns=dnsEnabled;
    Assert(legacy.EffectiveProxy==(proxyEnabled?savedProxy:"direct"),"Route flags did not control effective upstream");
    Assert(legacy.Save(),legacy.LastSaveError);
    var loaded=Config(Path.Combine(root,"legacy"));
    Parse(loaded,File.ReadAllText(Path.Combine(root,"legacy","config.json")));
    Assert(loaded.LastProxy==savedProxy&&loaded.UseProxy==proxyEnabled&&loaded.UseDagamiDns==dnsEnabled,"Saved flags lost the original proxy");
   }
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
    bool failed=false;string oldDns=legacy.DnsHost;bool oldFlag=legacy.UseDagamiDns;
    try{legacy.ChangeConnection(cfg=>{cfg.DnsHost="fixture.invalid";cfg.UseDagamiDns=false;cfg.LastProxy="http://fixture.invalid:8080";});}catch(IOException){failed=true;}
    Assert(failed&&legacy.DnsHost==oldDns&&legacy.UseDagamiDns==oldFlag&&legacy.LastProxy=="direct","Failed save changed effective connection settings");
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
