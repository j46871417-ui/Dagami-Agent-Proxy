using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using DevProxyApp;
class PatchChecks {
 static int checks;
 static object shell;
 static void Assert(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 static string Hash(string path){return AntigravityIntegration.Hash(File.ReadAllBytes(path));}
 static string Reject(Action action){try{action();}catch(InvalidOperationException ex){checks++;return ex.Message;}throw new Exception("Expected patch failure");}
 static void Link(string path,string exe){
  var link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
  try{
   link.GetType().InvokeMember("TargetPath",BindingFlags.SetProperty,null,link,new object[]{exe});
   link.GetType().InvokeMember("Arguments",BindingFlags.SetProperty,null,link,new object[]{"--user-argument"});
   link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
  }finally{Marshal.FinalReleaseComObject(link);}
 }
 [STAThread]static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"DevProxy-patch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   Assert(ProxyEngine.ScanAllTargets().Exists(x=>!x.IsCustom&&x.Id=="git"&&x.ConfigPath==null),"Git needs a separate alias without a global config patch");
   shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
   string links=Path.Combine(root,"Links");Directory.CreateDirectory(links);
   string exe=Path.Combine(root,"Antigravity.exe");File.Copy(typeof(MainForm).Assembly.Location,exe);
   var target=new AppTarget{Id="patch-fixture",DisplayName="Fixture",ExePath=exe};
   var store=new NativeShortcuts(Path.Combine(root,"state"));string manager=typeof(MainForm).Assembly.Location;
   Assert(Reject(()=>NativeAppPatcher.Apply(target,manager,new[]{links},store)).Contains("ярлыки"),"Missing shortcut has no useful instruction");
   Assert(!Directory.Exists(Path.Combine(root,"resources")),"Missing links changed client");
   string one=Path.Combine(links,"One.lnk"),two=Path.Combine(links,"Two.lnk");Link(one,exe);Link(two,exe);
   string originalOne=Hash(one),originalTwo=Hash(two);
   string unsupportedSummary;
   Assert(NativeAppPatcher.Apply(target,manager,new[]{links},store,null,out unsupportedSummary,false).Count==2,"Unsupported layout blocked proxy shortcuts");
   Assert(unsupportedSummary.Contains("пропущена")&&!unsupportedSummary.Contains("клиент подготовлен"),"Skipped preparation reported as successful binary patch");
   Assert(!Directory.Exists(Path.Combine(root,"resources")),"Unsupported layout created a fake backend");
   store.Restore(target.Id);
   Assert(Hash(one)==originalOne&&Hash(two)==originalTwo&&store.Count(target.Id)==0,"Unsupported-layout shortcut rollback was not exact");
   string backend=Path.Combine(root,"resources","bin","language_server.exe");Directory.CreateDirectory(Path.GetDirectoryName(backend));
   string[] patterns=(string[])typeof(AntigravityIntegration).GetField("Patterns",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
   File.WriteAllBytes(backend,Encoding.ASCII.GetBytes("unknown-new-version-with-zero-supported-signatures"));string unknownHash=Hash(backend);
   using(var heldBinaryLock=new FileStream(backend+".devproxy.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
    var unsupported=AntigravityIntegration.PrepareForProxy(exe);
    Assert(!unsupported.Prepared&&unsupported.Message.Contains("пропущена"),"Zero signatures required a writable/unlocked backend");
   }
   Assert(NativeAppPatcher.Apply(target,manager,new[]{links},store,null,out unsupportedSummary,false).Count==2,"Zero signatures blocked ordinary shortcuts");
   Assert(Hash(backend)==unknownHash&&!File.Exists(backend+".devproxy-region.bak")&&!File.Exists(backend+".devproxy-region.json"),"Unsupported backend or recovery data was changed");
   store.Restore(target.Id);
   byte[] known=Encoding.ASCII.GetBytes(string.Join("\0fixture\0",patterns));int count;
   File.WriteAllBytes(backend,AntigravityIntegration.PatchData(known,out count));string foreignPrepared=Hash(backend);
   Assert(NativeAppPatcher.Apply(target,manager,new[]{links},store,null,out unsupportedSummary,false).Count==2,"Pre-modified backend without manifest blocked proxy setup");
   Assert(Hash(backend)==foreignPrepared&&!File.Exists(backend+".devproxy-region.json"),"Foreign preparation was overwritten or assigned a false manifest");
   store.Restore(target.Id);
   File.WriteAllBytes(backend,Encoding.ASCII.GetBytes(string.Join("\0fixture\0",patterns)));string originalBackend=Hash(backend);
   // The second shell link can be read but not replaced. The first link must roll back.
   using(var held=new FileStream(two,FileMode.Open,FileAccess.Read,FileShare.Read)){
    string failure=Reject(()=>NativeAppPatcher.Apply(target,manager,new[]{links},store));
    Assert(failure.Contains("отменены"),"Failed installation has no rollback outcome");
    Assert(Hash(one)==originalOne&&Hash(two)==originalTwo,"Partial shortcut installation was not rolled back");
    Assert(store.Count(target.Id)==0,"Rollback left new active records");
    Assert(Hash(backend)==originalBackend,"New binary preparation not rolled back");
   }
   Assert(NativeAppPatcher.Apply(target,manager,new[]{links},store).Count==2,"One-click patch missed ordinary shortcuts");
   string patchedBackend=Hash(backend),patchedOne=Hash(one),patchedTwo=Hash(two),issue;
   Assert(patchedBackend!=originalBackend,"Antigravity was not prepared");
   Assert(store.VerifiedCount(target.Id,out issue)==2&&issue==null,"Ready shortcuts did not pass verification");
   Assert(NativeAppPatcher.Apply(target,manager,new[]{links},store).Count==2&&Hash(one)==patchedOne&&Hash(two)==patchedTwo&&Hash(backend)==patchedBackend,"Repeated patch is not idempotent");
   string three=Path.Combine(links,"Three.lnk");Link(three,exe);string originalThree=Hash(three);
   using(var held=new FileStream(three,FileMode.Open,FileAccess.Read,FileShare.Read)){
    Reject(()=>NativeAppPatcher.Apply(target,manager,new[]{links},store));
    Assert(Hash(one)==patchedOne&&Hash(two)==patchedTwo&&store.Count(target.Id)==2,"Rollback removed previously configured shortcuts");
    Assert(Hash(backend)==patchedBackend&&Hash(three)==originalThree,"Rollback undid prior client preparation or new original link");
   }
   File.AppendAllText(one,"external-change");string edited=Hash(one);
   Reject(()=>NativeAppPatcher.Apply(target,manager,new[]{links},store));
   Assert(Hash(one)==edited&&Hash(backend)==patchedBackend,"Foreign shortcut modification overwritten");
   // The remaining fixture files are deliberately left inside the temporary tree for cleanup.
   string autoLinks=Path.Combine(root,"AutoLinks");Directory.CreateDirectory(autoLinks);
   string autoConfig=Path.Combine(root,"settings.json");
   var autoTarget=new AppTarget{Id="auto-fixture",DisplayName="AutoFixture",ExePath=exe,ConfigPath=autoConfig};
   string autoSummary;
   var autoPaths=NativeAppPatcher.Apply(autoTarget,manager,new[]{autoLinks},store,"http://127.0.0.1:53200",out autoSummary,true);
   Assert(autoPaths.Count==1,"Auto-create shortcut failed");
   Assert(File.Exists(Path.Combine(autoLinks,"AutoFixture.lnk")),"Auto-created link missing on disk");
   Assert(autoSummary.Contains("ярлыков: 1")&&autoSummary.Contains("settings.json"),"Summary did not report auto-created shortcut and config");
   Assert(File.Exists(autoConfig)&&File.ReadAllText(autoConfig).Contains("127.0.0.1:53200"),"Proxy settings not written to config file");
   Console.WriteLine("PASS "+checks+" patch preparation, real links, idempotency, partial failure rollback, existing-state protection and Git discovery checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{if(shell!=null)Marshal.FinalReleaseComObject(shell);if(Directory.Exists(root)){
   string resolved=Path.GetFullPath(root),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!resolved.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("DevProxy-patch-",StringComparison.Ordinal))throw new IOException("Unsafe fixture cleanup");
   Directory.Delete(resolved,true);
  }}
 }
}
