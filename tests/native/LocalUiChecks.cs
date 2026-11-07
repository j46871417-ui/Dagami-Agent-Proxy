using System;
using System.Collections.Generic;
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
 static void DismissThemeMenu(MainForm form, ContextMenuStrip menu) {
    Assert(menu.Visible,"Theme menu did not open");
    var outside=Message.Create(form.Handle,0x0201,new IntPtr(1),new IntPtr((80<<16)|600));
    Application.FilterMessage(ref outside);
    Application.DoEvents();
    Assert(!menu.Visible&&!menu.IsDisposed,"An outside click must dismiss the reusable theme menu");
 }
 [STAThread] static int Main() {
  try {
   Application.EnableVisualStyles();
   Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
   Console.WriteLine("UI fixture: constructing preview");Console.Out.Flush();
   ContextMenuStrip themeMenu;
   using (var form = new MainForm(null, false, true)) {
    Console.WriteLine("UI fixture: rendering pages");Console.Out.Flush();
    Assert(((ListView)Get(form,"_targetList")).Items.Count==0,"App discovery delayed construction of the first window");
    Assert(typeof(MainForm).GetMethod("AntigravitySession",Private).Invoke(form,null)==null,"Login lookup crashes on missing application paths without a session");
    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Opacity = 1; form.Show(); Application.DoEvents();
    var menu = (ContextMenuStrip)Get(form, "_appActions");
    Assert(menu.Items[0].Text == "Настроить обычный запуск", "Ordinary launch action missing from menu");
    foreach(ToolStripItem item in menu.Items)Assert(item.Text!="Найти или создать ярлык","Partial duplicate shortcut action remains");
    bool notice = false; foreach (ToolStripItem item in menu.Items) { var parent = item as ToolStripMenuItem; if (parent == null) continue; foreach (ToolStripItem entry in parent.DropDownItems) if (entry.Text.Contains("заменить ручные правки")) notice = true; }
    Assert(notice, "Restore overwrite notice missing");
    var list = (ListView)Get(form, "_targetList");
    string installedPath=typeof(MainForm).Assembly.Location;
    var fixtures=new List<AppTarget>{
     new AppTarget{Id="icon-fixture",DisplayName="Dagami · тестовый EXE",ExePath=installedPath},
     new AppTarget{Id="missing-icon-fixture",DisplayName="Приложение без файла",ExePath=Path.Combine(Path.GetTempPath(),"missing-"+Guid.NewGuid()+".exe")}
    };
    var iconLoader=typeof(MainForm).GetMethod("LoadNativeAppIcons",BindingFlags.Static|BindingFlags.NonPublic);
    var icons=(Dictionary<string,Bitmap>)iconLoader.Invoke(null,new object[]{fixtures});
    Assert(icons.Count==1&&icons.ContainsKey(installedPath),"Installed executable did not supply a cached icon");
    Call(form,"RenderNativeTargets",fixtures,new HashSet<string>(),icons);
    typeof(MainForm).GetField("_shortcutIssue", Private).SetValue(form, "Ярлык удалён");
    if(list.SelectedItems.Count>0&&((AppTarget)list.SelectedItems[0].Tag).IsInstalled){var cfg=ConfigManager.Instance;string oldProxy=cfg.LastProxy;bool oldFlag=cfg.UseProxy;try{cfg.LastProxy="http://127.0.0.1:1234";cfg.UseProxy=true;Call(form,"UpdateAppActionState");Assert(((Button)Get(form,"_primaryAppAction")).Text=="Запустить через Dagami","Direct launch incorrectly requires shortcut setup");}finally{cfg.LastProxy=oldProxy;cfg.UseProxy=oldFlag;}}
    if (list.SelectedItems.Count > 0 && ((AppTarget)list.SelectedItems[0].Tag).IsInstalled)
     Assert(((ToolStripMenuItem)Get(form, "_patchMenuItem")).Enabled, "Repair disabled by stale shortcut issue");
    Assert(form.Controls.Find("ShortcutStateLabel",true).Length==1,"Shortcut state missing");
    if(list.SelectedItems.Count>0&&AntigravityIntegration.Supports(((AppTarget)list.SelectedItems[0].Tag).ExePath))Assert(form.Controls.Find("ClientPatchStatusLabel",true)[0].Text.Contains("Патч:"),"Binary patch status absent from selected application");
    Assert(typeof(MainForm).GetField("_shortcutToggle",Private)==null,"Duplicate partial-patch checkbox remains");
    Bounds(form); Picture(form, "local-apps.png");
    Assert(form.Controls.Find("BugReportNavigationButton",true).Length==1,"Global report button missing");
    Assert(form.Controls.Find("SaveJournalButton",true).Length==1,"Journal export button missing");
    Assert(form.Controls.Find("BlockUpdatesCheckBox",true).Length==1,"Antigravity update blocker checkbox missing");
    Assert(form.Controls.Find("MainStartWithWindowsCheckBox",true).Length==0,"Autostart still duplicated on main screen");
    Assert(form.Controls.Find("MainUseProxyCheckBox",true).Length==0,"Proxy switch still duplicated on main screen");
    Assert(form.Controls.Find("MainUseDnsCheckBox",true).Length==0,"DNS switch still duplicated on main screen");
    Assert(form.Controls.Find("ManualDnsButton",true).Length==1,"Manual DNS is not on its own page");
    Assert(form.Controls.Find("ResetDnsNrptButton",true).Length==1,"Reset is not on the DNS page");
    Assert(form.Controls.Find("UseProxyCheckBox",true).Length==1,"Settings UseProxy checkbox missing");
    Assert(Get(form,"_dnsObfuscation")!=null,"Pre-DNS obfuscation checkbox missing");
    Assert(VpnDiagnostics.IsFakeIpAddress(System.Net.IPAddress.Parse("198.18.0.25"))&&!VpnDiagnostics.IsFakeIpAddress(System.Net.IPAddress.Parse("8.8.8.8")),"Fake-IP detection failed");
    Assert(VpnDiagnostics.IsVirtualVpnAdapter("Nekoray Tun","WireGuard Tunnel",System.Net.NetworkInformation.NetworkInterfaceType.Ethernet),"Virtual VPN TUN detection failed");
    Assert(Buttons(form,"Сообщить о сбое")==1&&Buttons(form,"Баг-репорт")==0,"Duplicate report action remains");
    Assert(VisibleButtons(form)<=10,"Main screen still has too many visible buttons");
    Assert(((Panel[])Get(form,"_pageControls")).Length==5,"Flat pages are missing");
    Assert(Get(form,"_sessionRefreshTimer")==null,"Static preview starts background polling");
    Assert(form.Controls.Find("AppearanceMenuLink",true).Length==1,"Appearance menu missing");
    Assert(form.Controls.Find("ManagedPairLink",true).Length==1,"Managed pairing entry missing");
    var appearance=(LinkLabel)Get(form,"_themeLink");
    themeMenu=appearance.ContextMenuStrip;
    themeMenu.Show(appearance,0,appearance.Height);
    DismissThemeMenu(form,themeMenu);
    Color light=form.BackColor;
    themeMenu.Show(appearance,0,appearance.Height);
    themeMenu.Items[2].PerformClick();
    Application.DoEvents();
    Assert(form.BackColor.GetBrightness()<0.2f&&form.ForeColor.GetBrightness()>0.8f,"Dark theme colors missing");
    themeMenu.Show(appearance,0,appearance.Height);
    Assert(((ToolStripMenuItem)themeMenu.Items[2]).Checked,"Theme menu lost the active theme");
    DismissThemeMenu(form,themeMenu);
    Bounds(form);Picture(form,"dark-apps.png");
    Call(form,"ShowFluentPage",1);Bounds(form);Picture(form,"dark-connection.png");
    var pages=(Panel[])Get(form,"_pageControls");
    Assert(pages[1].Controls.Find("ManagedPairLink",true)[0].Visible&&pages[1].Controls.Find("CheckManagedPairLink",true)[0].Visible,"Pairing actions are missing from the connection page");
    Assert(pages[1].Controls.Find("ProbeServicePicker",true).Length==0,"Selecting a diagnostic service still looks like a connection setting");
    Call(form,"ShowFluentPage",4);
    Assert(pages[4].Controls.Find("ProbeServicePicker",true)[0].Visible,"Route diagnostics are missing from their own page");
    Call(form,"ShowFluentPage",1);
    Call(form,"ApplyThemeChoice","light");
    Assert(form.BackColor.ToArgb()==light.ToArgb(),"Switching back did not restore the light theme");
    for(int index=0;index<5;index++){Call(form,"ShowFluentPage",index);Bounds(form);Picture(form,"local-page-"+index+".png");}
    Call(form,"ShowFluentPage",1);
    var advanced=(Button)form.Controls.Find("AdvancedConnectionButton",true)[0];
    var wholeNetwork=(CheckBox)Get(form,"_wholeNetworkDns");
    Assert(!wholeNetwork.Visible,"Advanced connection settings must start collapsed");
    advanced.PerformClick();Application.DoEvents();
    Assert(wholeNetwork.Visible,"Advanced connection settings did not open");
    wholeNetwork.Checked=true;Application.DoEvents();Bounds(form);wholeNetwork.Checked=false;
    advanced.PerformClick();Application.DoEvents();
    form.Size = form.MinimumSize; Application.DoEvents();
    var custom=(TextBox)form.Controls.Find("CustomProxyAddress",true)[0];
    var reveal=(CheckBox)form.Controls.Find("ShowCustomProxy",true)[0];
    Assert(custom.Visible&&!ManagedAccess.IsProfile(custom.Text),"Custom proxy input is unavailable or displays the managed profile");
    Assert(form.Controls.Find("UseCustomProxyButton",true)[0].Visible,"Custom route action is buried");
    custom.Text="https://fixture-user:fixture-password@127.0.0.1:7897";
    Assert(custom.UseSystemPasswordChar,"Pasting proxy credentials exposed them");
    reveal.Checked=true;
    Assert(!custom.UseSystemPasswordChar,"Cannot show proxy credentials explicitly");
    reveal.Checked=false;
    for(int attempt=0;attempt<3;attempt++) {
     Call(form,"ShowFluentPage",4);Call(form,"ShowFluentPage",1);
     Assert(custom.Visible&&custom.Text.Contains("fixture-user")&&custom.UseSystemPasswordChar,"Custom proxy draft or masking was lost during navigation");
     Bounds(form);
    }
    custom.Clear();
    for(int index=0;index<5;index++){Call(form,"ShowFluentPage",index);Bounds(form);Picture(form,"local-page-"+index+"-min.png");}

   }
   Assert(themeMenu.IsDisposed,"Theme menu must be disposed when the form is disposed");
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
