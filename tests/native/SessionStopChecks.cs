using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DevProxyApp;

class SessionStopChecks {
 static int checks;
 static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static Process Start(string arguments){return Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,arguments){UseShellExecute=false,CreateNoWindow=true});}
 static bool Ensure(MainForm form,Func<string,bool> confirm,Action<NativeSession> stop=null){
  try{return (bool)typeof(MainForm).GetMethod("EnsureSessionsStoppedForRouteChange",Private).Invoke(form,new object[]{confirm,stop});}
  catch(TargetInvocationException error){throw error.InnerException;}
 }
 static void Cleanup(Process process){if(process==null)return;try{if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}finally{process.Dispose();}}
 [STAThread] static int Main(string[] args){
  if(args.Length>0&&args[0]=="--wait"){Thread.Sleep(60000);return 0;}
  if(args.Length==2&&args[0]=="--parent"){
   var wait=Stopwatch.StartNew();while(!File.Exists(args[1]+".go")&&wait.ElapsedMilliseconds<10000)Thread.Sleep(10);
   using(var leaf=Start("--wait")){File.WriteAllText(args[1],leaf.Id.ToString());Thread.Sleep(60000);}return 0;
  }
  string root=Path.Combine(Path.GetTempPath(),"Dagami-stop-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  Process parent=null,child=null,second=null,unrelated=null;NativeSession first=null,other=null;
  try{
   Application.EnableVisualStyles();
   string pidFile=Path.Combine(root,"child.pid");
   first=new NativeSession();parent=Start("--parent "+NativeShortcuts.Quote(pidFile));
   Assert(first.Job.AssignProcess(parent.Handle),"Fixture parent could not join Job");first.Processes.Add(Process.GetProcessById(parent.Id));
   File.WriteAllText(pidFile+".go","");var wait=Stopwatch.StartNew();
   int childId=0;while(wait.ElapsedMilliseconds<5000){try{if(File.Exists(pidFile)&&int.TryParse(File.ReadAllText(pidFile),out childId))break;}catch(IOException){}Thread.Sleep(10);}
   Assert(childId>0,"Fixture descendant did not start");child=Process.GetProcessById(childId);
   other=new NativeSession();second=Start("--wait");other.Processes.Add(Process.GetProcessById(second.Id)); // Tracked launcher whose Job assignment failed.
   unrelated=Start("--wait");
   using(var form=new MainForm(null,false,true)){
    var sessions=(Dictionary<string,NativeSession>)typeof(MainForm).GetField("_nativeSessions",Private).GetValue(form);
    typeof(MainForm).GetField("_cachedTargets",Private).SetValue(form,new List<AppTarget>{new AppTarget{Id="one",DisplayName="Fixture editor"},new AppTarget{Id="two",DisplayName="Fixture CLI"}});
    sessions.Add("one",first);sessions.Add("two",other);
    typeof(MainForm).GetField("_activeJobMgr",Private).SetValue(form,other.Job);
    typeof(MainForm).GetField("_activeSessionProc",Private).SetValue(form,second);
    var config=ConfigManager.Instance;bool proxy=config.UseProxy,dns=config.UseDagamiDns;int stopCalls=0;
    Assert(!Ensure(form,message=>{
     Assert(message.Contains("Fixture editor")&&message.Contains("Fixture CLI"),"Confirmation does not list every affected application");
     Assert(message.Contains("Несохранённые")&&message.Contains("Принудительно"),"Forced shutdown has no data-loss notice");
     Assert(form.HandleShortcutRequest(new NativeLaunchRequest())!=null,"IPC launch was accepted during route confirmation");return false;
    },session=>{stopCalls++;}),"Cancel accepted the route change");
    Assert(stopCalls==0&&!parent.HasExited&&!second.HasExited&&!child.HasExited,"Cancel stopped a session");
    Assert(sessions.Count==2&&config.UseProxy==proxy&&config.UseDagamiDns==dns,"Cancel changed session ownership or settings");
    bool failed=false;try{Ensure(form,message=>true,session=>{throw new IOException("fixture stop denied");});}catch(InvalidOperationException error){failed=error.Message.Contains("Настройки подключения не изменены");}
    Assert(failed&&sessions.Count==2&&!parent.HasExited,"Stop failure was ignored or session ownership lost");
    failed=false;try{Ensure(form,message=>true,session=>{});}catch(InvalidOperationException){failed=true;}
    Assert(failed&&sessions.Count==2,"Still-running sessions were reported stopped");
    Assert(Ensure(form,message=>true),"Confirmed stop did not permit route change");
    Assert(parent.WaitForExit(3000)&&child.WaitForExit(3000)&&second.WaitForExit(3000),"Managed launcher or descendant survived forced shutdown");
    Assert(!unrelated.HasExited,"Unrelated application was killed");
    Assert(sessions.Count==0,"Stopped sessions remain registered");
    Assert(typeof(MainForm).GetField("_activeSessionProc",Private).GetValue(form)==null&&typeof(MainForm).GetField("_activeJobMgr",Private).GetValue(form)==null,"Stopped active session references remain");
    Assert(config.UseProxy==proxy&&config.UseDagamiDns==dns,"Stopping alone changed connection settings");
    Assert(Ensure(form,message=>{throw new Exception("Empty sessions should not prompt");}),"Empty route change rejected");
    first=null;other=null;
   }
   Console.WriteLine("PASS "+checks+" route-switch confirmation/cancel, failed-stop guard, managed Job descendants, unassigned launcher and unrelated-process isolation checks");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally{
   if(first!=null)first.Dispose();if(other!=null)other.Dispose();
   Cleanup(parent);Cleanup(child);Cleanup(second);Cleanup(unrelated);
   string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-stop-"))throw new IOException("Unsafe fixture cleanup");
   Directory.Delete(root,true);
  }
 }
}
