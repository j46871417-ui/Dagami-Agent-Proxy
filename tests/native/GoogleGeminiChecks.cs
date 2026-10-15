using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DevProxyApp;
class GoogleGeminiChecks {
 static int checks;
 static void Assert(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
 static int Main(string[] args){
  // The same executable supplies a launcher/child fixture. Its child is deliberately
  // created only after Job assignment, so the test proves descendant accounting.
  if(args.Length>0 && args[0]=="child"){Thread.Sleep(30000);return 0;}
  if(args.Length>0 && args[0]=="launcher"){
   while(!File.Exists(args[1]))Thread.Sleep(20);
   var child=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"child"){UseShellExecute=false,CreateNoWindow=true});
   File.WriteAllText(args[2],child.Id.ToString());return 0;
  }
  string temp=Path.Combine(Path.GetTempPath(),"dagami-gemini-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
  Process parent=null;
  try {
   foreach(string host in new[]{"oauth2.googleapis.com","lh3.googleusercontent.com","accounts.google.com","gemini.google.com","CLOUDCODE-PA.GOOGLEAPIS.COM"})Assert(ScopedDnsResolver.IsScoped(host),"Missing route "+host);
   foreach(string host in new[]{"apis.google.com","alkalimakersuite-pa.clients6.google.com","alkalicore-pa.clients6.google.com","waa-pa.clients6.google.com","generativelanguage.googleapis.com","proactivebackend-pa.googleapis.com","robinfrontend-pa.googleapis.com","aisandbox-pa.googleapis.com"})Assert(ScopedDnsResolver.IsScoped(host),"Missing Gemini API route "+host);
   foreach(string host in new[]{"oauth2.googleapis.com.attacker.invalid","x.oauth2.googleapis.com","example.com","google.com",null})Assert(!ScopedDnsResolver.IsScoped(host),"Overbroad DNS route "+host);
   string main=Path.Combine(temp,"Gemini.exe");File.WriteAllText(main,"fixture");
   var target=new AppTarget{Id="fixture",ExePath=Path.Combine(temp,"GeminiAppLauncher.exe")};
   Assert(AppProxyPolicy.IsGemini(target),"Launcher not recognized");Assert(AppProxyPolicy.Executable(target)==main,"Actual main executable not selected");
   var start=new ProcessStartInfo(main);start.Arguments="--existing-argument";
   start.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"]="--existing-webview-flag";
   AppProxyPolicy.ConfigureGemini(start,"http://127.0.0.1:43210");
   Assert(start.Arguments.Contains("--proxy-server=\"http://127.0.0.1:43210\""),"Chromium proxy missing");
   Assert(!start.Arguments.Contains("--user-data-dir"),"Account profile was changed");
   Assert(start.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"].Contains("--existing-webview-flag"),"Existing WebView arguments lost");
   Assert(start.EnvironmentVariables["QTWEBENGINE_CHROMIUM_FLAGS"].Contains("--disable-quic"),"Qt proxy flags missing");
   bool rejected=false;try{AppProxyPolicy.ChromiumArguments("http://user:password@remote.invalid:80");}catch(ArgumentException){rejected=true;}Assert(rejected,"Remote/authenticated bridge accepted");
   string signal=Path.Combine(temp,"go"),pidFile=Path.Combine(temp,"pid");
   using(var session=new NativeSession()){
    parent=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"launcher \""+signal+"\" \""+pidFile+"\""){UseShellExecute=false,CreateNoWindow=true});
    session.Processes.Add(parent);Assert(session.Job.AssignProcess(parent.Handle),"Fixture job assignment failed");File.WriteAllText(signal,"");
    Assert(parent.WaitForExit(8000),"Launcher did not exit");Assert(File.Exists(pidFile),"Child fixture not created");
    int childId=int.Parse(File.ReadAllText(pidFile));var running=session.RunningProcess;
    Assert(running!=null && running.Id==childId,"Session lost child after launcher exit");
   }
   Console.WriteLine("PASS "+checks+" exact Google routes, Gemini launch policy, account profile, and real child-process lifetime checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally {if(parent!=null){try{if(!parent.HasExited)parent.Kill();}catch{}parent.Dispose();}
   string resolved=Path.GetFullPath(temp),baseTemp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!resolved.StartsWith(baseTemp,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("dagami-gemini-check-",StringComparison.Ordinal))throw new IOException("Unsafe fixture cleanup path");
   Directory.Delete(resolved,true);
  }
 }
}
