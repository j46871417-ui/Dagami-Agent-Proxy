using System;
using System.Diagnostics;
using System.IO;
using DevProxyApp;
class ConsoleLaunchChecks {
 static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"Dagami-console-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try {
   string source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ConsoleFixture.exe"),command=Path.Combine(root,"agy.exe"),result=Path.Combine(root,"input.txt");
   if(NativeCliApps.IsCli(new AppTarget{ExePath=source}))throw new Exception("Fixture must start as a GUI-subsystem EXE");
   File.WriteAllBytes(command,NativeCliApps.ConsoleRunnerBytes(source));
   if(!NativeCliApps.IsConsoleTarget(new AppTarget{ExePath=command}))throw new Exception("Converted command is not a console application");
   var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),"/d /q"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
   using(var process=Process.Start(psi)){
    process.StandardInput.WriteLine("\""+command+"\" \""+result+"\"");process.StandardInput.Flush();
    System.Threading.Thread.Sleep(500);process.StandardInput.WriteLine("interactive-input");process.StandardInput.WriteLine("exit");process.StandardInput.Close();
    if(!process.WaitForExit(8000)){process.Kill();throw new Exception("Interactive terminal did not finish");}
    string output=process.StandardOutput.ReadToEnd(),error=process.StandardError.ReadToEnd();
    if(!File.Exists(result)||File.ReadAllText(result)!="interactive-input"||!output.Contains("FIXTURE-COMPLETE")||error.Contains("interactive-input"))throw new Exception("cmd stole the child's input: "+output+error);
   }
   Console.WriteLine("PASS real cmd waits for converted command; interactive stdin and console output retained; GUI fixture unchanged");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-console-"))throw new IOException("Unsafe cleanup");Directory.Delete(root,true);}
 }
}
