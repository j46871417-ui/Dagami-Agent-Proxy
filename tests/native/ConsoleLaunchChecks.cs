using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DevProxyApp;
class ConsoleLaunchChecks {
 static int Main(string[] args){
  if(args.Length>0&&args[0]=="--job-child"){Thread.Sleep(30000);return 0;}
  if(args.Length==2&&(args[0]=="--job-parent"||args[0]=="--job-parent-normal")){
   using(var session=new NativeSession())using(var child=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--job-child"){UseShellExecute=false,CreateNoWindow=true})){
    if(!session.Job.AssignProcess(child.Handle))return 3;
    File.WriteAllText(args[1],child.Id.ToString());
    if(args[0]=="--job-parent-normal"){session.Dispose();if(!child.WaitForExit(5000))return 4;return 0;}
    Thread.Sleep(30000);
   }return 0;
  }
  Console.InputEncoding = new System.Text.UTF8Encoding(false);
  string root=Path.Combine(Path.GetTempPath(),"Dagami-console-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try {
   string source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ConsoleFixture.exe"),command=Path.Combine(root,"agy.exe"),result=Path.Combine(root,"input.txt");
   if(NativeCliApps.IsCli(new AppTarget{ExePath=source}))throw new Exception("Fixture must start as a GUI-subsystem EXE");
   File.WriteAllBytes(command,NativeCliApps.ConsoleRunnerBytes(source));
   if(!NativeCliApps.IsConsoleTarget(new AppTarget{ExePath=command}))throw new Exception("Converted command is not a console application");
   var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),"/d /q"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
   using(var process=Process.Start(psi)){
    // The redirected cmd pipe uses an OEM code page. Keep its text ASCII;
    // Unicode fixture paths are already passed through CreateProcessW as WorkingDirectory.
    process.StandardInput.WriteLine("\"agy.exe\" \"input.txt\"");process.StandardInput.Flush();
    System.Threading.Thread.Sleep(1200);process.StandardInput.WriteLine("interactive-input");process.StandardInput.WriteLine("exit");process.StandardInput.Close();
    if(!process.WaitForExit(8000)){process.Kill();throw new Exception("Interactive terminal did not finish");}
    string output=process.StandardOutput.ReadToEnd(),error=process.StandardError.ReadToEnd();
    if(!File.Exists(result)||File.ReadAllText(result)!="interactive-input"||!output.Contains("FIXTURE-COMPLETE")||error.Contains("interactive-input"))throw new Exception("cmd stole the child's input: "+output+error);
   }
   string normalPid=Path.Combine(root,"normal-child.pid");
   using(var normal=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--job-parent-normal \""+normalPid+"\""){UseShellExecute=false,CreateNoWindow=true})){
    if(!normal.WaitForExit(8000)){normal.Kill();throw new Exception("Normal job shutdown timed out");}
    if(normal.ExitCode!=0||!File.Exists(normalPid))throw new Exception("Normal job shutdown did not terminate the child");
   }
   string pidFile=Path.Combine(root,"job-child.pid");int childId=0;
   using(var parent=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--job-parent \""+pidFile+"\""){UseShellExecute=false,CreateNoWindow=true})){
    try{
     var wait=Stopwatch.StartNew();
     string pidContent = null;
     while (wait.ElapsedMilliseconds < 8000) {
      if (File.Exists(pidFile)) {
       try { pidContent = File.ReadAllText(pidFile); if (!string.IsNullOrEmpty(pidContent)) break; }
       catch (IOException) {}
      }
      if (parent.HasExited) break;
      Thread.Sleep(25);
     }
     if (string.IsNullOrEmpty(pidContent)) throw new Exception("Job lifetime fixture did not start");
     childId = int.Parse(pidContent.Trim());parent.Kill();if(!parent.WaitForExit(5000))throw new Exception("Fixture parent did not exit");
     Thread.Sleep(250);using(var child=Process.GetProcessById(childId)){if(child.HasExited)throw new Exception("GUI session job killed child when parent crashed");child.Kill();child.WaitForExit(5000);}childId=0;
    }finally{if(!parent.HasExited){parent.Kill();parent.WaitForExit(5000);}if(childId!=0)try{using(var child=Process.GetProcessById(childId)){if(!child.HasExited)child.Kill();}}catch(ArgumentException){}}
   }
   Console.WriteLine("PASS interactive CLI console and GUI job lifetime on normal shutdown and parent crash");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-console-"))throw new IOException("Unsafe cleanup");Directory.Delete(root,true);}
 }
}
