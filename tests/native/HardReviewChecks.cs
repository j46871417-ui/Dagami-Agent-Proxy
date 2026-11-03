using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using DevProxyApp;

class HardReviewChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static string Hash(string path){return AntigravityIntegration.Hash(File.ReadAllBytes(path));}
 static Process Start(string exe){return Process.Start(new ProcessStartInfo(exe,"--wait"){UseShellExecute=false,CreateNoWindow=true});}
 static void Close(Process process){if(process==null)return;try{if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}finally{process.Dispose();}}
 static string Header(Stream stream){var text=new StringBuilder();while(text.Length<8192){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();text.Append((char)b);if(text.ToString().EndsWith("\r\n\r\n"))return text.ToString();}throw new IOException();}
 static int Main(string[] args){
  if(args.Length>0&&args[0]=="--wait"){Thread.Sleep(60000);return 0;}
  string root=Path.Combine(Path.GetTempPath(),"Dagami-hard-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  Process a=null,b=null;
  try{
   var scan=typeof(AntigravityIntegration).GetMethod("FindAll",BindingFlags.NonPublic|BindingFlags.Static);
   var random=new Random(31);
   for(int sample=0;sample<80;sample++){
    byte[] data=new byte[sample*13],needle=new byte[1+sample%19];
    for(int i=0;i<data.Length;i++)data[i]=(byte)random.Next(4);
    for(int i=0;i<needle.Length;i++)needle[i]=(byte)random.Next(4);
    if(data.Length>=needle.Length)Array.Copy(needle,0,data,data.Length-needle.Length,needle.Length);
    var expected=new List<int>();
    for(int offset=0;offset<=data.Length-needle.Length;offset++){int j=0;while(j<needle.Length&&data[offset+j]==needle[j])j++;if(j==needle.Length){expected.Add(offset);offset+=needle.Length-1;}}
    var actual=(List<int>)scan.Invoke(null,new object[]{data,needle});
    Assert(string.Join(",",expected)==string.Join(",",actual),"Signature optimization changed match positions/counts for fixture "+sample);
   }
   string gui=Path.Combine(root,"UnknownEditor.exe");File.Copy(typeof(MainForm).Assembly.Location,gui);
   var unknown=new AppTarget{Id="custom-editor",ExePath=gui,ConfigPath=Path.Combine(root,"settings.json")};File.WriteAllText(unknown.ConfigPath,"{\"proxy\":\"user-value\",\"http.proxy\":\"foreign-schema\"}");
   string before=Hash(unknown.ConfigPath),message;
   Assert(!ApplicationSupport.SupportsSettings(unknown),"Unknown app falsely claims JSON adapter support");
   Assert(!ProxyEngine.ApplyProxyToTarget(unknown,"http://127.0.0.1:12345",out message)&&Hash(unknown.ConfigPath)==before&&!File.Exists(unknown.ConfigPath+".devproxy.bak"),"Unknown JSON schema was changed");
   Assert(ApplicationSupport.Describe(unknown).Contains("не перенаправит"),"Unknown app implies guaranteed traffic interception");
   Assert(ApplicationSupport.Kind(new AppTarget{Id="codex-gui",ExePath=Process.GetCurrentProcess().MainModule.FileName})=="cli","GUI ID overrode actual console executable");
   Assert(!ApplicationSupport.IsJetBrains("idea64.exe")&&ApplicationSupport.SettingsPath(new AppTarget{ExePath="idea64.exe"})==null,"Relative JetBrains filename crashes path discovery");
   Assert(!ApplicationSupport.IsElectronDesktop(new AppTarget{ExePath="Tool.exe"}),"Relative path probed arbitrary working-directory resources");
   Assert(ApplicationDiscovery.FindByExecutable(new[]{unknown},gui.ToUpperInvariant())==unknown,"Existing executable not recognized before duplicate add");
   var misleading=new AppTarget{Id="vscode-unrelated",ExePath=gui};Assert(ApplicationSupport.Kind(misleading)=="unknown","ID prefix selected a VS Code adapter");
   string npm=Path.Combine(root,"npm");Directory.CreateDirectory(npm);File.Copy(Process.GetCurrentProcess().MainModule.FileName,Path.Combine(npm,"node.exe"));
   File.WriteAllText(Path.Combine(npm,"wrong.js"),"fixture");File.WriteAllText(Path.Combine(npm,"right.js"),"fixture");
   string wrapper=Path.Combine(npm,"tool.cmd");File.WriteAllText(wrapper,"@echo off\r\necho \"%dp0%\\wrong.js\"\r\n\"%dp0%\\node.exe\" \"%dp0%\\right.js\" %*\r\n");
   Assert(NativeCliApps.CreateStart(wrapper,new string[0],"http://127.0.0.1:12345",root).Arguments.Contains("right.js"),"Quoted non-executable path hijacked npm entrypoint discovery");
   File.WriteAllText(wrapper,"echo \"%dp0%\\wrong.js\"\r\n");bool unsupported=false;try{NativeCliApps.CreateStart(wrapper,new string[0],"http://127.0.0.1:12345",root);}catch(InvalidOperationException){unsupported=true;}
   Assert(unsupported,"Arbitrary CMD text was accepted as an npm launch");
   File.WriteAllText(wrapper,"REM & \"node\" \"%dp0%\\wrong.js\" %*\r\nendLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\" \"%dp0%\\right.js\" %*\r\n");
   Assert(NativeCliApps.CreateStart(wrapper,new string[0],"http://127.0.0.1:12345",root).Arguments.Contains("right.js"),"Standard npm command tail or comment handling failed");
   string ps=Path.Combine(npm,"tool.ps1");File.WriteAllText(ps,"# & \"node$exe\" \"$basedir/wrong.js\" $args\r\n& \"$basedir/node$exe\" \"$basedir/right.js\" $args\r\n");
   Assert(NativeCliApps.CreateStart(ps,new string[0],"http://127.0.0.1:12345",root).Arguments.Contains("right.js"),"Standard npm PowerShell wrapper failed");

   // A real round trip through the shared GUI/CLI unpatch coordinator.
   string backend=Path.Combine(root,"language_server.exe");
   var patterns=(string[])typeof(AntigravityIntegration).GetField("Patterns",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
   byte[] original=Encoding.ASCII.GetBytes("fixture\0"+string.Join("\0",patterns)+"\0https_proxy\0https_proxy\0tail");File.WriteAllBytes(backend,original);
   AntigravityIntegration.Prepare(backend);string patched=Hash(backend);bool launcherRestored=false;
   Assert(AntigravityIntegration.HasActivePatchRecord(backend),"Prepared backend has no active ownership record");
   File.WriteAllText(backend+".devproxy-region.bak","broken");bool rejected=false;
   try{NativeAppPatcher.RestoreWithLauncher(new AppTarget{ExePath=backend},()=>{launcherRestored=true;return new List<string>();});}catch(IOException error){rejected=error.Message.Contains("бинарный патч");}
   Assert(rejected&&launcherRestored&&Hash(backend)==patched,"Binary rollback failure was reported as success or bytes lost");
   File.WriteAllBytes(backend+".devproxy-region.bak",original);NativeAppPatcher.RestoreWithLauncher(new AppTarget{ExePath=backend},()=>new List<string>());
   Assert(Hash(backend)==AntigravityIntegration.Hash(original),"CLI/GUI common unpatch left binary patched");
   Assert(!AntigravityIntegration.HasActivePatchRecord(backend),"Completed rollback still reports an active patch");
   AntigravityIntegration.Prepare(backend);Assert(AntigravityIntegration.HasActivePatchRecord(backend),"Repeat preparation retained restored status");AntigravityIntegration.Restore(backend);
   string untouched=Path.Combine(root,"unmanaged","language_server.exe");Directory.CreateDirectory(Path.GetDirectoryName(untouched));File.WriteAllBytes(untouched,original);
   NativeAppPatcher.RestoreWithLauncher(new AppTarget{ExePath=untouched},()=>new List<string>());
   Assert(!File.Exists(untouched+".devproxy.lock"),"Unpatch wrote a lock to an unmanaged backend");

   // Same image name, different installations: only the exact requested file closes.
   string self=Process.GetCurrentProcess().MainModule.FileName;var copies=new List<string>();
   foreach(string dir in new[]{"A","B"}){string folder=Path.Combine(root,dir);Directory.CreateDirectory(folder);File.Copy(typeof(MainForm).Assembly.Location,Path.Combine(folder,"DagamiAgentProxy.exe"));string exe=Path.Combine(folder,"review-worker.exe");File.Copy(self,exe);copies.Add(exe);}
   a=Start(copies[0]);b=Start(copies[1]);Thread.Sleep(400);
   NativeLogin.StopExactExecutables(new[]{copies[0]});
   Assert(a.WaitForExit(5000)&&!b.HasExited,"Name-only conflict shutdown killed another installation");

   // Saturate the connection guard without opening external sockets or threads.
   using(var bridge=new LocalProxyBridge()){
    Assert(bridge.Start(0,"direct"),"Capacity fixture bridge failed");
    var slots=(SemaphoreSlim)typeof(LocalProxyBridge).GetField("_connectionSlots",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bridge);
    int acquired=0;try{while(slots.Wait(0))acquired++;Assert(acquired==LocalProxyBridge.MaximumConcurrentConnections,"Connection limit mismatch");
     using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=3000;Assert(Header(client.GetStream()).Contains(" 503 "),"Saturated bridge kept accepting unbounded work");}
    }finally{slots.Release(acquired);}
    using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=3000;byte[] request=Encoding.ASCII.GetBytes("GET http://metadata.google.internal/ HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");client.GetStream().Write(request,0,request.Length);Assert(Header(client.GetStream()).Contains(" 404 "),"Bridge did not recover after capacity was released");}
   }
   Console.WriteLine("PASS "+checks+" unknown-app isolation, classification, duplicate detection, verified full rollback, exact-path shutdown and bounded bridge capacity checks");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally{Close(a);Close(b);string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-hard-review-"))throw new IOException("Unsafe cleanup");Directory.Delete(root,true);}
 }
}
