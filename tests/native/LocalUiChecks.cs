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
 static void Bounds(Control parent) { foreach (Control child in parent.Controls) if (child.Visible) { Assert(child.Left >= 0 && child.Top >= 0 && child.Right <= parent.ClientSize.Width + 2 && child.Bottom <= parent.ClientSize.Height + 2, "Clipped control: " + child.Text); Bounds(child); } }
 static void Picture(MainForm form, string name) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(name); } }
 [STAThread] static int Main() {
  try {
   Application.EnableVisualStyles();
   using (var form = new MainForm(null, false, true)) {
    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Opacity = 1; form.Show(); Application.DoEvents();
    var menu = (ContextMenuStrip)Get(form, "_appActions");
    Assert(menu.Items[0].Text == "Найти или создать ярлык", "Shortcut action remains misleading");
    bool notice = false; foreach (ToolStripItem item in menu.Items) { var parent = item as ToolStripMenuItem; if (parent == null) continue; foreach (ToolStripItem entry in parent.DropDownItems) if (entry.Text.Contains("заменить ручные правки")) notice = true; }
    Assert(notice, "Restore overwrite notice missing");
    typeof(MainForm).GetField("_shortcutIssue", Private).SetValue(form, "Ярлык удалён");
    Call(form, "UpdateAppActionState");
    var list = (ListView)Get(form, "_targetList");
    if (list.SelectedItems.Count > 0 && ((AppTarget)list.SelectedItems[0].Tag).IsInstalled)
     Assert(((CheckBox)Get(form, "_shortcutToggle")).Enabled, "Repair disabled by stale shortcut issue");
    Bounds(form); Picture(form, "local-apps.png");
    Assert(form.Controls.Find("BugReportNavigationButton",true).Length==1,"Global report button missing");
    Assert(form.Controls.Find("SaveJournalButton",true).Length==1,"Journal export button missing");
    Call(form,"ShowFluentPage",3);Bounds(form);Picture(form,"local-journal.png");
    Call(form, "ShowFluentPage", 2); Bounds(form); Picture(form, "local-antigravity.png");
    form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form); Picture(form, "local-antigravity-min.png");
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
