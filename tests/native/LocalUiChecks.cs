using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DevProxyApp;
class LocalUiChecks {
 static int checks; const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
 static void Assert(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
 static object Get(MainForm form, string field) { return typeof(MainForm).GetField(field, Private).GetValue(form); }
 static void Call(MainForm form, string name, params object[] args) { typeof(MainForm).GetMethod(name, Private).Invoke(form, args); Application.DoEvents(); }
 static void Bounds(Control parent) { foreach (Control child in parent.Controls) if (child.Visible) { var scroll=parent as ScrollableControl;int height=scroll!=null&&scroll.AutoScroll?Math.Max(parent.ClientSize.Height,parent.DisplayRectangle.Bottom):parent.ClientSize.Height;Assert(child.Left >= 0 && child.Top >= 0 && child.Right <= parent.ClientSize.Width + 2 && child.Bottom <= height + 2, "Clipped control: " + child.GetType().Name+" "+child.Text+" bounds="+child.Bounds+" parent="+parent.GetType().Name+" "+parent.ClientSize+" display="+parent.DisplayRectangle+" scroll="+(scroll==null?"none":scroll.AutoScroll+" min="+scroll.AutoScrollMinSize)); Bounds(child); } }
 static void Picture(MainForm form, string name) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(name); } }
 static int Buttons(Control parent,string text){int count=0;foreach(Control child in parent.Controls){if(child is Button&&child.Text==text)count++;count+=Buttons(child,text);}return count;}
 static int VisibleButtons(Control parent){int count=0;foreach(Control child in parent.Controls)if(child.Visible){if(child is Button)count++;count+=VisibleButtons(child);}return count;}
 [STAThread] static int Main() {
  try {
   Application.EnableVisualStyles();
   Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
   Console.WriteLine("UI fixture: constructing preview");Console.Out.Flush();
   using (var form = new MainForm(null, false, true)) {
    Console.WriteLine("UI fixture: rendering pages");Console.Out.Flush();
    Assert(typeof(MainForm).GetMethod("AntigravitySession",Private).Invoke(form,null)==null,"Login lookup crashes on missing application paths without a session");
    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Opacity = 1; form.Show(); Application.DoEvents();
    var menu = (ContextMenuStrip)Get(form, "_appActions");
    Assert(menu.Items[0].Text == "Настроить обычный запуск", "Ordinary launch action missing from menu");
    foreach(ToolStripItem item in menu.Items)Assert(item.Text!="Найти или создать ярлык","Partial duplicate shortcut action remains");
    bool notice = false; foreach (ToolStripItem item in menu.Items) { var parent = item as ToolStripMenuItem; if (parent == null) continue; foreach (ToolStripItem entry in parent.DropDownItems) if (entry.Text.Contains("заменить ручные правки")) notice = true; }
    Assert(notice, "Restore overwrite notice missing");
    typeof(MainForm).GetField("_shortcutIssue", Private).SetValue(form, "Ярлык удалён");
    Call(form, "UpdateAppActionState");
    var list = (ListView)Get(form, "_targetList");
    if(list.SelectedItems.Count>0&&((AppTarget)list.SelectedItems[0].Tag).IsInstalled){var cfg=ConfigManager.Instance;string oldProxy=cfg.LastProxy;try{cfg.LastProxy="http://127.0.0.1:1234";Call(form,"UpdateAppActionState");Assert(((Button)Get(form,"_primaryAppAction")).Text=="Настроить обычный запуск","Missing launcher does not suggest one-time setup");}finally{cfg.LastProxy=oldProxy;}}
    if (list.SelectedItems.Count > 0 && ((AppTarget)list.SelectedItems[0].Tag).IsInstalled)
     Assert(((ToolStripMenuItem)Get(form, "_patchMenuItem")).Enabled, "Repair disabled by stale shortcut issue");
    Assert(form.Controls.Find("ShortcutStateLabel",true).Length==1,"Shortcut state missing");
    if(list.SelectedItems.Count>0&&AntigravityIntegration.Supports(((AppTarget)list.SelectedItems[0].Tag).ExePath))Assert(form.Controls.Find("ClientPatchStatusLabel",true)[0].Text.Contains("Патч:"),"Binary patch status absent from selected application");
    Assert(typeof(MainForm).GetField("_shortcutToggle",Private)==null,"Duplicate partial-patch checkbox remains");
    Bounds(form); Picture(form, "local-apps.png");
    Assert(form.Controls.Find("BugReportNavigationButton",true).Length==1,"Global report button missing");
    Assert(form.Controls.Find("SaveJournalButton",true).Length==1,"Journal export button missing");
    Assert(Buttons(form,"Сообщить о сбое")==1&&Buttons(form,"Баг-репорт")==0,"Duplicate report action remains");
    Assert(VisibleButtons(form)<=9,"Main screen still has too many visible buttons");
    Assert(((Panel[])Get(form,"_pageControls")).Length==3,"Advanced tools remain in main navigation");
    Assert(!((Timer)Get(form,"_nativeLogTimer")).Enabled,"Static preview starts background polling");
    Call(form,"ShowFluentPage",1);var tabs=(TabControl)Get(form,"_settingsTabs");
    for(int index=0;index<tabs.TabPages.Count;index++){tabs.SelectedIndex=index;Application.DoEvents();Bounds(form);Picture(form,"local-settings-"+index+".png");}
    tabs.SelectedIndex=1;((CheckBox)Get(form,"_wholeNetworkDns")).Checked=true;Application.DoEvents();Bounds(form);((CheckBox)Get(form,"_wholeNetworkDns")).Checked=false;
    Call(form, "ShowFluentPage", 2); Bounds(form); Picture(form, "local-dns.png");
    form.Size = form.MinimumSize; Application.DoEvents();
    foreach(int index in new[]{0,1,2}){Call(form,"ShowFluentPage",index);Bounds(form);Picture(form,"local-page-"+index+"-min.png");}
    Call(form,"ShowFluentPage",1);for(int index=0;index<tabs.TabPages.Count;index++){tabs.SelectedIndex=index;Application.DoEvents();Bounds(form);Picture(form,"local-settings-"+index+"-min.png");}
   }
   string reportLog=Path.Combine(Environment.CurrentDirectory,"report-fixture.log");File.WriteAllText(reportLog,"[TEST] Interactive console regression fixture\nAuthorization: Bearer fixture-secret\n[TEST] HTTP bridge ready on loopback\n");
   using(var report=new BugReportDialog(reportLog)){
    report.StartPosition=FormStartPosition.Manual;report.Location=new Point(-30000,-30000);report.Show();Application.DoEvents();
    using(var bitmap=new Bitmap(report.Width,report.Height)){report.DrawToBitmap(bitmap,new Rectangle(Point.Empty,report.Size));bitmap.Save("local-report.png");}
    report.Size=report.MinimumSize;Application.DoEvents();
    using(var bitmap=new Bitmap(report.Width,report.Height)){report.DrawToBitmap(bitmap,new Rectangle(Point.Empty,report.Size));bitmap.Save("local-report-min.png");}
   }
   Console.WriteLine("PASS " + checks + " local shortcut recovery actions, restore notice and default/minimum UI bounds; report dialog rendered at both sizes"); return 0;
  } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
}
