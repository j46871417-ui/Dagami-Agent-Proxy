using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DevProxyApp;
class ReviewRegressionChecks {
 static int checks,failures;static object shell;static string root;
 class Paths:ICliPathEnvironment {public string UserPath{get;set;}public string ProcessPath{get;set;}public string MachinePath{get;set;}public void Notify(){}}
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static void Case(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
 static string Hash(string path){return AntigravityIntegration.Hash(File.ReadAllBytes(path));}
 static string Folder(string name){string path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
 static AppTarget App(string folder,string name,string id){string path=Path.Combine(folder,name);File.Copy(typeof(MainForm).Assembly.Location,path);return new AppTarget{Id=id,DisplayName=id,ExePath=path};}
 static void Link(string path,string exe){object link=shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});try{link.GetType().InvokeMember("TargetPath",BindingFlags.SetProperty,null,link,new object[]{exe});link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);}finally{Marshal.FinalReleaseComObject(link);}}
 [STAThread]static int Main(){
  root=Path.Combine(Path.GetTempPath(),"Dagami-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
  try {
   Case("invalid JetBrains metadata rejects stale config",()=>{
    string folder=Folder("JB"),bin=Path.Combine(folder,"bin");Directory.CreateDirectory(bin);var app=App(bin,"idea64.exe","jb_fixture");
    File.WriteAllText(Path.Combine(folder,"product-info.json"),"{\"dataDirectoryName\":\"../escape\"}");app.ConfigPath=Path.Combine(folder,"stale.xml");File.WriteAllText(app.ConfigPath,"<application />");string before=Hash(app.ConfigPath),message;
    Assert(!ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),"invalid metadata accepted");Assert(Hash(app.ConfigPath)==before,"stale configuration changed");
   });
   Case("removed portable data does not retain stale path",()=>{
    string folder=Folder("Portable");var app=App(folder,"Code.exe","vscode-fixture");string data=Path.Combine(folder,"data");Directory.CreateDirectory(data);ApplicationSupport.ResolveSettings(app);string before=app.ConfigPath;
    Directory.Delete(data);ApplicationSupport.ResolveSettings(app);Assert(app.ConfigPath!=before,"portable path remained cached");
   });
   Case("unsupported desktop configuration remains untouched",()=>{
    string folder=Folder("Codex");var app=App(folder,"Codex.exe","codex-gui");app.ConfigPath=Path.Combine(folder,"config.json");File.WriteAllText(app.ConfigPath,"{\"custom\":true}");string before=Hash(app.ConfigPath),message;
    Assert(!ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),"unsupported JSON config accepted");Assert(Hash(app.ConfigPath)==before,"unsupported configuration modified");
   });
   Case("multi shortcut restore validates every link before writing",()=>{
    string folder=Folder("Links");var app=App(folder,"Editor.exe","review-links");string a=Path.Combine(folder,"A.lnk"),b=Path.Combine(folder,"B.lnk");Link(a,app.ExePath);Link(b,app.ExePath);
    var store=new NativeShortcuts(Path.Combine(folder,"state"));store.EnablePrepared(app,typeof(MainForm).Assembly.Location,new[]{folder},()=>{},()=>{});string patched=Hash(a);File.AppendAllText(b,"foreign");bool rejected=false;
    try{store.Restore(app.Id);}catch(InvalidOperationException){rejected=true;}Assert(rejected,"foreign modification accepted");Assert(Hash(a)==patched&&store.Count(app.Id)==2,"partial restore changed first link/journal");
   });
   Case("missing settings can be restored from backup",()=>{
    string folder=Folder("MissingSettings");var app=App(folder,"Code.exe","settings-fixture");app.ConfigPath=Path.Combine(folder,"settings.json");File.WriteAllText(app.ConfigPath,"{\"http.proxy\":\"http://original.invalid:80\"}");string message;
    Assert(ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),message);File.Delete(app.ConfigPath);Assert(ProxyEngine.RestoreTarget(app,out message),message);Assert(File.ReadAllText(app.ConfigPath).Contains("original.invalid"),"original proxy missing");
   });
   Case("restore write failure rolls earlier links back",()=>{
    string folder=Folder("LockedLinks");var app=App(folder,"Editor.exe","locked-links");string a=Path.Combine(folder,"A.lnk"),b=Path.Combine(folder,"B.lnk");Link(a,app.ExePath);Link(b,app.ExePath);
    var store=new NativeShortcuts(Path.Combine(folder,"state"));store.EnablePrepared(app,typeof(MainForm).Assembly.Location,new[]{folder},()=>{},()=>{});string ah=Hash(a),bh=Hash(b);bool failed=false;
    using(var held=new FileStream(b,FileMode.Open,FileAccess.Read,FileShare.Read))try{store.Restore(app.Id);}catch(IOException){failed=true;}
    Assert(failed,"write lock ignored");Assert(Hash(a)==ah&&Hash(b)==bh&&store.Count(app.Id)==2,"failed restore left partial state");store.Restore(app.Id);
   });
   Case("launcher failure preserves current settings and backup",()=>{
    string folder=Folder("ConfigRollback");var app=App(folder,"Code.exe","rollback-settings");app.ConfigPath=Path.Combine(folder,"settings.json");File.WriteAllText(app.ConfigPath,"{\"http.proxy\":\"http://original.invalid\",\"other\":true}");string message;
    Assert(ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),message);File.AppendAllText(app.ConfigPath,"\n");string config=Hash(app.ConfigPath),backup=Hash(app.ConfigPath+".devproxy.bak");bool failed=false;
    try{NativeAppPatcher.RestoreWithLauncher(app,()=>{throw new IOException("fixture launcher failure");});}catch(IOException){failed=true;}
    Assert(failed&&Hash(app.ConfigPath)==config&&Hash(app.ConfigPath+".devproxy.bak")==backup,"config/backup lost after launcher failure");
    NativeAppPatcher.RestoreWithLauncher(app,()=>new System.Collections.Generic.List<string>());Assert(File.ReadAllText(app.ConfigPath).Contains("original.invalid"),"retry failed");
   });
   Case("read-only ordinary shortcut gets a writable copy",()=>{
    string folder=Folder("ReadOnlyDesktop");var app=App(folder,"Editor.exe","readonly-app");string source=Path.Combine(folder,"Original.lnk");Link(source,app.ExePath);File.SetAttributes(source,File.GetAttributes(source)|FileAttributes.ReadOnly);
    try {
     string before=Hash(source);var store=new NativeShortcuts(Path.Combine(folder,"state"));var paths=store.EnablePrepared(app,typeof(MainForm).Assembly.Location,new[]{folder},()=>{},()=>{},true);
     Assert(paths.Count==1&&!string.Equals(source,paths[0],StringComparison.OrdinalIgnoreCase),"no writable fallback");Assert(Hash(source)==before&&store.SkippedReadOnlyLinks==1,"read-only original changed or skip not reported");
     string issue;Assert(store.VerifiedCount(app.Id,out issue)==1&&issue==null,"fallback not verified");store.Restore(app.Id);
    }finally{File.SetAttributes(source,File.GetAttributes(source)&~FileAttributes.ReadOnly);}
   });
   Case("deleted CLI launcher repairs and removed EXE can be unpatched",()=>{
    string folder=Folder("Cli");string original=Path.Combine(folder,"fixturecli.exe");File.Copy(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,original);
    var app=new AppTarget{Id="review-cli",ExePath=original,DisplayName="Fixture CLI"};var env=new Paths{UserPath="original",ProcessPath="original",MachinePath=""};var store=new NativeCliApps(Path.Combine(folder,"state"),env);
    string shim=store.Enable(app,typeof(MainForm).Assembly.Location);File.Delete(shim);Assert(File.Exists(store.Enable(app,typeof(MainForm).Assembly.Location)),"missing CLI shim not repaired");
    File.Delete(original);Assert(store.Contains(app.Id)&&!NativeCliApps.IsCli(app),"removed CLI registration lost");var targets=new System.Collections.Generic.List<AppTarget>();ApplicationDiscovery.IncludeRegistered(targets,store.RegisteredTargets());
    Assert(targets.Count==1&&!targets[0].IsInstalled,"missing registered target not listed");store.Restore(app.Id);Assert(env.UserPath=="original"&&!File.Exists(shim),"removed CLI cleanup failed");
   });
   Case("CLI lookup skips a Desktop with the same name",()=>{
    string desktop=Folder("DesktopPath"),cli=Folder("CliPath");File.Copy(typeof(MainForm).Assembly.Location,Path.Combine(desktop,"codex.exe"));string expected=Path.Combine(cli,"codex.exe");File.Copy(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,expected);
    Assert(NativeCliApps.FindCommand("codex",desktop+";"+cli,null,true)==expected,"Desktop shadows CLI discovery");
   });
   Case("missing JetBrains XML restores unrelated components",()=>{
    string folder=Folder("MissingXml");var app=App(folder,"idea64.exe","jb_missingxml");app.ConfigPath=Path.Combine(folder,"proxy.settings.xml");File.WriteAllText(app.ConfigPath,"<application><component name=\"Other\"><option name=\"a\" value=\"keep\"/></component></application>");string message;
    Assert(ProxyEngine.ApplyProxyToTarget(app,"http://127.0.0.1:12345",out message),message);File.Delete(app.ConfigPath);Assert(ProxyEngine.RestoreTarget(app,out message),message);Assert(File.ReadAllText(app.ConfigPath).Contains("keep"),"unrelated XML components lost");
   });
   Case("long valid shortcut path can be restored",()=>{
    string folder=Folder(new string('d',Math.Max(1,190-root.Length-1)));var app=App(folder,"Editor.exe","long-path");string link=Path.Combine(folder,"Shortcut-"+new string('n',45)+".lnk");Assert(link.Length<259,"fixture exceeds Windows path limit");Link(link,app.ExePath);string original=Hash(link);
    var store=new NativeShortcuts(Path.Combine(folder,"state"));store.EnablePrepared(app,typeof(MainForm).Assembly.Location,new[]{folder},()=>{},()=>{});store.Restore(app.Id);Assert(Hash(link)==original,"long path restore was not exact");
   });
   Case("extensionless npm entrypoint skips quoted node executable",()=>{
    string folder=Folder("Npm");string wrapper=Path.Combine(folder,"tool.cmd"),entry=Path.Combine(folder,"entry");File.WriteAllText(entry,"fixture");File.Copy(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,Path.Combine(folder,"node.exe"));File.WriteAllText(wrapper,"\"%dp0%\\node.exe\" \"%dp0%\\entry\" %*\r\n");
    var start=NativeCliApps.CreateStart(wrapper,new[]{"space value"},"http://127.0.0.1:12345",folder);Assert(start.FileName.EndsWith("node.exe")&&start.Arguments.Contains("entry")&&start.Arguments.Contains("space value"),"extensionless npm entry not resolved");
   });
   Case("corrupt CLI journal does not crash GUI target classification",()=>{
    string folder=Folder("CorruptCli");var app=App(folder,"Editor.exe","gui-fixture");string state=Path.Combine(folder,"state");Directory.CreateDirectory(state);string journal=Path.Combine(state,"state.dat");File.WriteAllText(journal,"broken protected state");string before=Hash(journal);
    var store=new NativeCliApps(state,new Paths());Assert(!NativeCliApps.IsCommandTarget(app,store),"GUI mistaken for managed CLI");Assert(Hash(journal)==before,"corrupt journal overwritten");
   });
   Case("reserved Windows application name produces valid shortcut",()=>{
    string folder=Folder("DeviceName");var app=App(folder,"Editor.exe","device-name");app.DisplayName="NUL";var store=new NativeShortcuts(Path.Combine(folder,"state"));
    var paths=store.EnablePrepared(app,typeof(MainForm).Assembly.Location,new[]{folder},()=>{},()=>{},true);Assert(paths.Count==1&&Path.GetFileName(paths[0])=="_NUL.lnk","reserved DOS name not sanitized");store.Restore(app.Id);
   });
   Console.WriteLine("CHECKS "+checks+", FAILURES "+failures);return failures==0?0:1;
  }finally{Marshal.FinalReleaseComObject(shell);string full=Path.GetFullPath(root),parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!full.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("Dagami-review-"))throw new IOException("Unsafe cleanup");Directory.Delete(full,true);}
 }
}
