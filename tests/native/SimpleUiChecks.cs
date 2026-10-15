using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DevProxyApp;
class SimpleUiChecks {
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static int checks;
 static MainForm form;
 static void Assert(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 static object Get(string name){return typeof(MainForm).GetField(name,Private).GetValue(form);}
 static void Set(string name,object value){typeof(MainForm).GetField(name,Private).SetValue(form,value);}
 static void Call(string name,params object[] args){typeof(MainForm).GetMethod(name,Private).Invoke(form,args);Application.DoEvents();}
 static void Snapshot(string path){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(path);}}
 static Control FindLink(Control c,string address){if((c.Tag as string)==address)return c;foreach(Control child in c.Controls){var result=FindLink(child,address);if(result!=null)return result;}return null;}
 static void Layout(Control c){foreach(Control child in c.Controls){if(child.Visible){Assert(child.Left>=0&&child.Top>=0&&child.Right<=c.ClientSize.Width+2&&child.Bottom<=c.ClientSize.Height+2,"Clipped control: "+child.GetType().Name+" "+child.Text+" bounds="+child.Bounds+" parent="+c.GetType().Name+" size="+c.ClientSize);Layout(child);}}}
 [STAThread]static int Main(){
  string proxy=ConfigManager.Instance.LastProxy;
  Process fixture=null;
  try {
   Application.EnableVisualStyles();form=new MainForm();form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
   var list=(ListView)Get("_targetList"); var primary=(Button)Get("_primaryAppAction"); var toggle=(CheckBox)Get("_shortcutToggle");
   Assert(!list.CheckBoxes&&!list.MultiSelect,"Ambiguous bulk selection remains");
   Assert(((Button)Get("_patchAppAction")).Visible&&((Button)Get("_patchAppAction")).Text=="Пропатчить","Separate patch action is missing");
   Assert(FindLink(form,"https://pay.cloudtips.ru/p/671fda86")!=null,"Donation link missing or incorrect");
   Assert(FindLink(form,"https://t.me/dagamvi").Text=="t.me/dagamvi","Public link text is incorrect");
   Assert(FindLink(form,"https://t.me/bright26bot").Text=="@bright26bot","Bot link is incorrect");
   Assert(list.SelectedItems.Count==1,"No default application selected");
   var target=new AppTarget{Id="ui-fixture",DisplayName="Тестовое приложение",ExePath=Path.Combine(Environment.CurrentDirectory,"FixtureApp.exe"),IsCustom=true};
   list.Items.Clear();var item=new ListViewItem(target.DisplayName){Tag=target};item.SubItems.Add("Установлено");item.SubItems.Add(target.ExePath);list.Items.Add(item);item.Selected=true;
   Call("UpdateNativeDetails");
   ConfigManager.Instance.LastProxy="";Call("UpdateNativeStatus");
   Assert(primary.Text=="Настроить"&&primary.Enabled,"Missing proxy has no setup action");
   Call("RunPrimaryNativeAction");Assert(((Panel[])Get("_pageControls"))[1].Visible,"Setup action does not open connection page");
   Assert(form.ActiveControl!=null,"Setup has no keyboard focus");
   ConfigManager.Instance.LastProxy=proxy;Call("ShowFluentPage",0);Call("UpdateNativeStatus");
   Assert(primary.Text=="Запустить"&&primary.Enabled,"Ready app cannot launch");
   Assert(!toggle.Checked&&toggle.Enabled,"Unconfigured fixture shortcut marked enabled");
   Set("_proxyCheckFailed",true);Call("UpdateNativeStatus");Assert(primary.Text=="Проверить","Failed connection lacks recovery action");
   Assert(((Label)Get("_targetDetails")).Text.Contains("Ошибка подключения"),"Failure has no text status");
   Set("_proxyCheckFailed",false);
   var session=new NativeSession();fixture=Process.Start(new ProcessStartInfo("FixtureApp.exe",NativeShortcuts.Quote(Path.Combine(Path.GetTempPath(),"devproxy-ui-fixture-"+Guid.NewGuid().ToString("N")))+" --child"){UseShellExecute=false,CreateNoWindow=true});
   session.Processes.Add(fixture);
   var sessions=(Dictionary<string,NativeSession>)Get("_nativeSessions");sessions.Add(target.Id,session);
   Call("UpdateNativeStatus");Assert(primary.Text=="Открыть","Running app cannot be reopened");int pid=fixture.Id;
   Call("RunPrimaryNativeAction");Assert(sessions[target.Id].Processes.Count==1&&fixture.Id==pid&&!fixture.HasExited,"Open action duplicated or killed app");
   Assert(((ToolStripMenuItem)Get("_stopAppItem")).Enabled,"Running app cannot stop via menu");
   session.Dispose();sessions.Remove(target.Id);fixture=null;Call("UpdateNativeStatus");Assert(primary.Text=="Запустить","Exited app still marked running");
   Assert(!((ToolStripMenuItem)Get("_stopAppItem")).Enabled,"Stop enabled without session");
   Set("_shortcutIssue","Ярлык изменён");Call("UpdateNativeStatus");Assert(!toggle.Enabled&&((Label)Get("_shortcutExplanation")).Text.Contains("изменён"),"Shortcut error hidden or toggle active");
   Assert(((ToolStripMenuItem)Get("_restoreShortcutItem")).Enabled,"No recovery for shortcut error");Call("UpdateNativeDetails");
   Set("_nativeBusy",true);Call("SetNativeButtonsEnabled",false);Assert(!primary.Enabled&&!toggle.Enabled,"Busy state accepts conflicting actions");Set("_nativeBusy",false);Call("SetNativeButtonsEnabled",true);
   form.ClientSize=new Size(1200,820);Application.DoEvents();Snapshot("simple-ui-default.png");Layout(form);
   form.Size=form.MinimumSize;Application.DoEvents();Snapshot("simple-ui-minimum.png");Layout(form);
   Call("ShowFluentPage",1);Layout(form);Snapshot("simple-ui-connection.png");
   Call("ShowFluentPage",2);Layout(form);Snapshot("simple-ui-antigravity.png");
   Call("ShowFluentPage",0);list.Items.Clear();Call("UpdateNativeDetails");Assert(!primary.Enabled&&!toggle.Enabled,"Empty selection allows app actions");Snapshot("simple-ui-empty.png");
   Console.WriteLine("PASS "+checks+" UI state, real process reuse, recovery, menu, keyboard focus and visible control bounds checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{ConfigManager.Instance.LastProxy=proxy;if(fixture!=null&&!fixture.HasExited)fixture.Kill();if(form!=null){Call("ExitApplication");form.Dispose();}}
 }
}
