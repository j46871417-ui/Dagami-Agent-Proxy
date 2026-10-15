using System;
using System.IO;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DevProxyApp;

class ShortcutChecks {
 static int checks;
 static void Assert(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
 static void Reject(Action action, string text) { bool caught = false; try { action(); } catch(InvalidOperationException) {caught=true;} Assert(caught,text); }
 static object Link(object shell,string path) { return shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path}); }
 static object Get(object link,string key) { return link.GetType().InvokeMember(key,BindingFlags.GetProperty,null,link,null); }
 static void Set(object link,string key,object value) {link.GetType().InvokeMember(key,BindingFlags.SetProperty,null,link,new object[]{value});}
 static void Save(object link) {link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);}
 static void Release(object value) {if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
 static bool Bytes(byte[] a,byte[] b) { return Convert.ToBase64String(a)==Convert.ToBase64String(b); }
 [STAThread] public static int Main() {
  string root=Path.Combine(Path.GetTempPath(),"DevProxy-shortcuts-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
  object shell=null;
  try {
   string exe=Path.Combine(root,"My IDE.exe"), manager=Path.Combine(root,"DevProxy.fixture.exe"), folder=Path.Combine(root,"User links");
   Directory.CreateDirectory(folder); File.Copy(typeof(NativeShortcuts).Assembly.Location,exe); File.Copy(exe,manager);
   string args="--project \"C:\\Work folder\\файл.txt\" --flag=42";
   string linkPath=Path.Combine(folder,"My existing IDE.lnk");
   shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); object link=Link(shell,linkPath);
   Set(link,"TargetPath",exe);Set(link,"Arguments",args);Set(link,"WorkingDirectory",root);Set(link,"IconLocation",exe+",0");Set(link,"Description","User description"); Save(link);Release(link);
   byte[] original=File.ReadAllBytes(linkPath);var target=new AppTarget{Id="fixture",ExePath=exe,DisplayName="My IDE"};
   var store=new NativeShortcuts(Path.Combine(root,"state"));
   Assert(store.Enable(target,manager,new[]{folder}).Count==1,"Existing shortcut not found");
   Assert(store.Count("fixture")==1,"Journal missing");
   string issue; Assert(store.VerifiedCount("fixture",out issue)==1&&issue==null,"Valid shortcut is not shown enabled");
   File.Move(linkPath,linkPath+".moved");
   Assert(store.VerifiedCount("fixture",out issue)==1&&issue!=null,"Missing shortcut incorrectly shown enabled");
   File.Move(linkPath+".moved",linkPath);
   byte[] patched=File.ReadAllBytes(linkPath);
   Assert(!Bytes(original,patched),"Shortcut unchanged");
   Assert(store.Enable(target,manager,new[]{folder}).Count==1 && Bytes(patched,File.ReadAllBytes(linkPath)),"Repeated enable changed shortcut");
   link=Link(shell,linkPath);
   Assert((string)Get(link,"TargetPath")==manager,"Launcher not installed");
   string launch=(string)Get(link,"Arguments");
   Assert(launch.StartsWith("--shortcut-launch \"fixture\" --shortcut-key "),"Opaque launch arguments missing");
   string key=launch.Substring(launch.LastIndexOf(' ')+1);
   var record=store.Resolve(key,"fixture");
   Assert(record.Arguments==args && record.WorkingDirectory==root && record.Executable==exe,"Original launch context lost");
   Assert((string)Get(link,"IconLocation")==exe+",0" && (string)Get(link,"Description")=="User description","Icon/description lost");
   Assert((string)Get(link,"WorkingDirectory")==root,"Working directory lost");
   Release(link);
   Reject(()=>store.Resolve(key,"other"),"Key accepted for another app");
   Reject(()=>store.Resolve("../escape","fixture"),"Traversal key accepted");
   byte[] journal=File.ReadAllBytes(Path.Combine(root,"state","journal.dat"));
   Assert(!Encoding.UTF8.GetString(journal).Contains("--project"),"Original arguments persisted in plaintext journal");
   // Legacy Apply/Restore must not silently unwrap a managed shortcut.
   typeof(ProxyEngine).GetMethod("UpdateShortcutFile",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{linkPath,exe,"http://127.0.0.1:9",false});
   Assert(Bytes(patched,File.ReadAllBytes(linkPath)),"Legacy Apply destroyed managed shortcut");
   Assert(store.Restore("fixture").Count==1 && Bytes(original,File.ReadAllBytes(linkPath)),"Rollback is not byte exact");
   Assert(store.Count("fixture")==0,"Rollback kept active record");
   Assert(store.VerifiedCount("fixture",out issue)==0&&issue==null,"Restored shortcut is not shown disabled");
   store.Enable(target,manager,new[]{folder}); byte[] installed=File.ReadAllBytes(linkPath);
   File.AppendAllText(linkPath,"changed-by-user");
   Assert(store.VerifiedCount("fixture",out issue)==1&&issue!=null,"Edited shortcut incorrectly shown enabled");
   Reject(()=>store.Restore("fixture"),"User-edited shortcut overwritten");
   Assert(!Bytes(installed,File.ReadAllBytes(linkPath)),"Failed rollback changed edited shortcut");
   File.WriteAllBytes(linkPath,installed);
   record=store.Resolve(((string)Get(link=Link(shell,linkPath),"Arguments")).Split(' ')[3],"fixture");Release(link);
   byte[] backup=File.ReadAllBytes(record.BackupPath);File.WriteAllText(record.BackupPath,"corrupt");
   Reject(()=>store.Restore("fixture"),"Corrupt backup accepted");
   Assert(Bytes(installed,File.ReadAllBytes(linkPath)),"Corrupt backup damaged shortcut");
   File.WriteAllBytes(record.BackupPath,backup);store.Restore("fixture");
   Assert(NativeShortcuts.Quote("C:\\space path\\")=="\"C:\\space path\\\\\"","Trailing backslash quoting broken");
   string pipe="DevProxy.fixture."+Guid.NewGuid().ToString("N");int received=0;
   using(var broker=new NativeLaunchBroker(pipe,r=>{Interlocked.Increment(ref received);return r.TargetId=="reject"?"rejected":null;})) {
    broker.Start();
    Assert(NativeLaunchBroker.Send(pipe,new NativeLaunchRequest{TargetId="fixture",ShortcutKey=key},4000)=="","IPC launch failed");
    Assert(NativeLaunchBroker.Send(pipe,new NativeLaunchRequest(),4000)=="","IPC show failed");
    Assert(NativeLaunchBroker.Send(pipe,new NativeLaunchRequest{TargetId="reject"},4000)=="rejected","IPC error lost");
    using(var bad=new NamedPipeClientStream(".",pipe,PipeDirection.InOut)){bad.Connect(2000);byte[] invalid=BitConverter.GetBytes(9000);bad.Write(invalid,0,4);}
    Assert(NativeLaunchBroker.Send(pipe,new NativeLaunchRequest{TargetId="after-malformed"},4000)=="","Malformed client killed broker");
    Assert(received==4,"IPC duplicate/incorrect dispatch");
   }
   Console.WriteLine("PASS "+checks+" real shortcut, exact rollback, edit protection, DPAPI and IPC checks"); return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally {Release(shell);if(Directory.Exists(root))Directory.Delete(root,true);}
 }
}
