using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DevProxyApp;
class SessionChecks {
 static int checks;
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static void Wait(string path){for(int n=0;n<400&&!File.Exists(path);n++){Thread.Sleep(30);Application.DoEvents();}Assert(File.Exists(path),"Fixture output missing: "+Path.GetFileName(path));}
 [STAThread]static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"DevProxy-sessions-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  MainForm form=null;
  try {
   File.Copy("FixtureApp.exe",Path.Combine(root,"FixtureOne.exe"));File.Copy("FixtureApp.exe",Path.Combine(root,"FixtureTwo.exe"));
   File.Copy(typeof(MainForm).Assembly.Location,Path.Combine(root,Path.GetFileName(typeof(MainForm).Assembly.Location)));
   Application.EnableVisualStyles();form=new MainForm();form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
   var list=(ListView)typeof(MainForm).GetField("_targetList",Private).GetValue(form);
   if(list.Items.Count>0)list.Items[0].Selected=true;
   Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save("native-shortcuts.png");}
   int port=(int)typeof(MainForm).GetField("_bridgePort",Private).GetValue(form);Assert(port>0,"Bridge missing");
   var launch=typeof(MainForm).GetMethod("LaunchTarget",Private,null,new[]{typeof(AppTarget),typeof(string),typeof(string),typeof(string).MakeByRefType()},null);
   var targets=new List<AppTarget>{new AppTarget{Id="fixture-one",DisplayName="Fixture 1",ExePath=Path.Combine(root,"FixtureOne.exe")},new AppTarget{Id="vscode-fixture-two",DisplayName="Fixture 2",ExePath=Path.Combine(root,"FixtureTwo.exe")}};
   typeof(MainForm).GetField("_cachedTargets",Private).SetValue(form,targets);
   var dictionary=(Dictionary<string,NativeSession>)typeof(MainForm).GetField("_nativeSessions",Private).GetValue(form);
   foreach(var target in targets){
    string output=Path.Combine(root,target.Id+".txt");object[] call={target,NativeShortcuts.Quote(output)+" "+NativeShortcuts.Quote("nonce with space"),root,null};
    Assert((bool)launch.Invoke(form,call),"Launch failed: "+call[3]);Wait(output);Wait(output+".child");
    foreach(string path in new[]{output,output+".child"}) {
     string text=File.ReadAllText(path);Assert(text.Contains("nonce=nonce with space"),"Original quoted args lost");Assert(text.Contains("cwd="+root),"Working directory lost");
     foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy","AG_LS_PROXY","AGY_PROXY_URL"})Assert(text.Contains(key+"=http://127.0.0.1:"+port),"Proxy not inherited by parent/child: "+key);
    }
   }
   Assert(dictionary.Count==2&&dictionary[targets[0].Id].RunningProcess!=null&&dictionary[targets[1].Id].RunningProcess!=null,"Concurrent apps killed one another");
   int originalPid=dictionary[targets[0].Id].RunningProcess.Id;
   Assert(form.HandleShortcutRequest(new NativeLaunchRequest{TargetId=targets[0].Id})==null,"Repeat shortcut failed");
   Assert(dictionary[targets[0].Id].Processes.Count==1&&dictionary[targets[0].Id].RunningProcess.Id==originalPid,"Repeat click duplicated process");
   form.Close();Application.DoEvents();Assert(!form.IsDisposed&&!form.Visible,"Window close did not hide to tray");
   Assert(dictionary[targets[0].Id].RunningProcess!=null&&dictionary[targets[1].Id].RunningProcess!=null,"Window close killed app sessions");
   Console.WriteLine("PASS "+checks+" real parent/child environment, arguments, concurrent launch, repeat click, tray and UI checks");return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally {if(form!=null){typeof(MainForm).GetMethod("ExitApplication",Private).Invoke(form,null);form.Dispose();}if(Directory.Exists(root))Directory.Delete(root,true);}
 }
}
