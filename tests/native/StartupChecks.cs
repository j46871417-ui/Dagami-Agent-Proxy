using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using DevProxyApp;
class StartupChecks {
 static int checks;
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static void Assert(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 static void Reject(Action action){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Assert(failed,"Foreign startup task was changed");}
 class Tasks:IStartupTaskStore {
  public readonly Dictionary<string,string> Items=new Dictionary<string,string>();public int Writes,Deletes;public bool Fail;
  public string Read(string name){return Items.ContainsKey(name)?Items[name]:null;}
  public void Write(string name,string xml,string sid){if(Fail)throw new IOException("fixture scheduler failure");Items[name]=xml;Writes++;}
  public void Delete(string name){Items.Remove(name);Deletes++;}
 }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
 [DllImport("kernel32.dll")]static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
 [DllImport("kernel32.dll")]static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32.dll")]static extern IntPtr LockResource(IntPtr resource);
 [DllImport("kernel32.dll")]static extern uint SizeofResource(IntPtr module,IntPtr resource);
 [DllImport("kernel32.dll")]static extern bool FreeLibrary(IntPtr module);
 static void Layout(Control parent){foreach(Control child in parent.Controls)if(child.Visible){Assert(child.Left>=0&&child.Top>=0&&child.Right<=parent.ClientSize.Width+2&&child.Bottom<=parent.ClientSize.Height+2,"Clipped control: "+child.Text+" "+child.Bounds+" in "+parent.ClientSize);Layout(child);}}
 static void Snapshot(Form form,string path){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(path);}}
 [STAThread]static int Main(string[] args){
  Environment.CurrentDirectory=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
  if(args.Length==1&&args[0]=="--log"){var writer=new StreamWriter("startup-checks.log",false,Encoding.UTF8){AutoFlush=true};Console.SetOut(writer);Console.SetError(writer);}
 try{
  Assert(WindowsExecutionContext.IntegrityRid()>=8192,"Fixture must run with normal user integrity, outside a Low-labelled output directory");
  Assert(WindowsExecutionContext.StartupProblem(4096)!=null&&WindowsExecutionContext.StartupProblem(0)!=null,"Low/untrusted process allowed to start child applications");
  Assert(WindowsExecutionContext.StartupProblem(8192)==null&&WindowsExecutionContext.StartupProblem(12288)==null,"Normal user process incorrectly requires elevation");
  string exe=typeof(SystemDnsManager).Assembly.Location;
  Assert(new WindowsStartupTaskStore().Read("DagamiAgentProxy-ReadCheck-"+Guid.NewGuid().ToString("N"))==null,"Missing task cannot be read safely through the Windows backend");
  IntPtr module=LoadLibraryEx(exe,IntPtr.Zero,0x22);Assert(module!=IntPtr.Zero,"Cannot inspect executable manifest");
  try{
   IntPtr resource=FindResource(module,new IntPtr(1),new IntPtr(24));Assert(resource!=IntPtr.Zero,"No embedded Windows manifest");
   byte[] bytes=new byte[SizeofResource(module,resource)];Marshal.Copy(LockResource(LoadResource(module,resource)),bytes,0,bytes.Length);
   var manifest=new XmlDocument();manifest.LoadXml(Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
   var request=manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']");
   Assert(request!=null&&request.Attributes["level"].Value=="asInvoker","Application should normally run with the user's rights");
   Assert(request.Attributes["uiAccess"].Value=="false","Unexpected UIAccess privilege");
  }finally{FreeLibrary(module);}
  var tasks=new Tasks();string sid="S-1-5-21-111-222-333-1001";var startup=new NativeStartup(tasks,sid,exe);
  Assert(!startup.IsEnabled()&&tasks.Writes==0,"Startup was enabled without user choice");startup.SetEnabled(false);Assert(tasks.Deletes==0,"Disabling absent task creates or deletes unrelated tasks");
  string xml=startup.TaskXml();File.WriteAllText("startup-task-validation.xml",xml,new UTF8Encoding(false));
  Assert(xml.Contains("LeastPrivilege")&&xml.Contains("InteractiveToken")&&!xml.Contains("<Password>"),"Startup saves a password or uses elevated rights");
  tasks.Items[startup.TaskName]=xml.Replace("LeastPrivilege","HighestAvailable");Assert(startup.HasLegacyElevatedTask()&&!startup.IsEnabled(),"Legacy elevated task was not reported");startup.SetEnabled(true);Assert(startup.IsEnabled()&&!startup.HasLegacyElevatedTask(),"Legacy task was not safely migrated");startup.SetEnabled(false);tasks.Writes=0;tasks.Deletes=0;
  Assert(xml.Contains("--autostart")&&xml.Contains("IgnoreNew")&&xml.Contains("PT0S"),"Startup loses tray mode, duplicate guard or unlimited run time");
  startup.SetEnabled(true);Assert(startup.IsEnabled()&&tasks.Writes==1,"Startup enable/verify failed");
  startup.SetEnabled(false);Assert(!startup.IsEnabled()&&tasks.Deletes==1,"Startup disable failed");
  tasks.Items[startup.TaskName]=xml.Replace("<Enabled>true</Enabled>","<Enabled>false</Enabled>");startup.SetEnabled(true);Assert(startup.IsEnabled(),"Externally disabled managed task cannot be re-enabled");
  tasks.Items[startup.TaskName]=xml.Replace("Dagami Agent Proxy user startup v1","Foreign app");int writes=tasks.Writes,deletes=tasks.Deletes;
  Reject(()=>startup.SetEnabled(true));Reject(()=>startup.SetEnabled(false));Assert(tasks.Writes==writes&&tasks.Deletes==deletes,"Foreign task was overwritten or removed");
  tasks.Items[startup.TaskName]=xml.Replace(sid,"S-1-5-21-111-222-333-1002");Reject(()=>startup.SetEnabled(true));
  tasks.Items.Remove(startup.TaskName);tasks.Fail=true;bool failed=false;try{startup.SetEnabled(true);}catch(IOException){failed=true;}Assert(failed&&!startup.IsEnabled(),"Scheduler failure shown as enabled");tasks.Fail=false;
  string renamed=Path.Combine(Path.GetTempPath(),"Dagami & test space.exe");var moved=new NativeStartup(tasks,sid,renamed);
  var parsed=new XmlDocument();parsed.LoadXml(moved.TaskXml());Assert(parsed.SelectSingleNode("//*[local-name()='Command']").InnerText==renamed,"Startup corrupts spaces, ampersand or Unicode in the EXE path");
  Application.EnableVisualStyles();
  using(var form=new MainForm(null,false,true)){
   form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
   var toggle=(CheckBox)typeof(MainForm).GetField("_startWithWindows",Private).GetValue(form);Assert(toggle!=null&&!toggle.Checked,"Startup control absent or on by default");
   var show=typeof(MainForm).GetMethod("ShowFluentPage",Private);show.Invoke(form,new object[]{1});Application.DoEvents();Layout(form);Snapshot(form,"startup-settings.png");
   var buttons=(List<Button>)typeof(MainForm).GetField("_nativeButtons",Private).GetValue(form);
   foreach(var button in buttons)if(button.Text.Contains("Записать DNS")||button.Text=="Пропатчить"||button.Text=="Проверить прокси")Assert((bool)button.GetType().GetProperty("Primary").GetValue(button,null),"Functional button is not blue: "+button.Text);
   form.Size=form.MinimumSize;Application.DoEvents();Layout(form);Snapshot(form,"startup-settings-minimum.png");
  }
  using(var tray=new MainForm(null,false,true,true)){
   tray.StartPosition=FormStartPosition.Manual;tray.Location=new Point(-20000,-20000);tray.Show();Application.DoEvents();Assert(!tray.Visible&&!tray.ShowInTaskbar,"Autostart opens a visible window instead of tray");
   typeof(MainForm).GetMethod("ShowNativeWindow",Private).Invoke(tray,null);Application.DoEvents();Assert(tray.Visible&&tray.Opacity==1,"Tray startup cannot reopen the main window");
  }
  Console.WriteLine("PASS "+checks+" user-level manifest, startup migration/foreign-task guards, tray launch and settings layout; no real startup task changed");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
