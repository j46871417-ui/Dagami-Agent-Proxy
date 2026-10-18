using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using DevProxyApp;
class SystemDnsUiChecks {
 static int checks;
 static void Assert(bool b,string reason){checks++;if(!b)throw new Exception(reason);}
 static void Reject(Action a){bool caught=false;try{a();}catch(ArgumentException){caught=true;}Assert(caught,"Invalid DNS request accepted");}
 [STAThread]static int Main(){
  try {
   string sharedPath=Path.Combine(Environment.CurrentDirectory,"dns-status-sharing-"+Guid.NewGuid().ToString("N")+".json");
   try {
    File.WriteAllText(sharedPath,"{\"Status\":\"Applied\"}",new UTF8Encoding(false));
    using(var writer=new FileStream(sharedPath,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete))
     Assert(SystemDnsManager.ReadStoredText(sharedPath).Contains("Applied"),"Status reader blocks a writer holding the file");
   }finally{if(File.Exists(sharedPath))File.Delete(sharedPath);}
   Assert(SystemDnsManager.Friendly("Apply failed: DNS backup file is busy: state.json. Previous settings restored.").Contains("Файл копии DNS занят"),"Persistent file lock has no clear guidance");
   int prompts=0;
   Assert(!SystemDnsForm.ConfirmFullReset((text,title)=>{prompts++;return DialogResult.No;}) && prompts==1,"First reset cancellation did not stop immediately");
   prompts=0;
   Assert(!SystemDnsForm.ConfirmFullReset((text,title)=>{prompts++;return prompts==1?DialogResult.Yes:DialogResult.No;}) && prompts==2,"Second reset cancellation did not block reset");
   prompts=0;
   Assert(SystemDnsForm.ConfirmFullReset((text,title)=>{prompts++;Assert(text.StartsWith("Вы точно уверены в том что вы делаете?"),"Reset confirmation text changed");return DialogResult.Yes;}) && prompts==2,"Full reset requires exactly two explicit Yes replies");
   var resetRequest=new SystemDnsRequest{JobId=Guid.NewGuid().ToString("N"),Operation="ResetAll"};
   Reject(()=>SystemDnsManager.Validate(resetRequest));resetRequest.ConfirmResetAll=true;
   Assert(SystemDnsManager.EncodeRequest(resetRequest).Length>0,"Confirmed reset depends on adapter or server selection");
   Assert(SystemDnsManager.EngineStart("ResetAll",SystemDnsManager.EncodeRequest(resetRequest),"fixture.ps1").CreateNoWindow,"Full reset engine opens a console");
   var request=new SystemDnsRequest{JobId=Guid.NewGuid().ToString("N"),Operation="Apply",Server="185.149.145.6",Preset="Full",ChangeAdapters=true,UseNrpt=true,Adapters=new[]{new SystemDnsAdapter{Index=1,Guid=Guid.NewGuid().ToString()}}};
   string payload=SystemDnsManager.EncodeRequest(request);
   Assert(Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Contains("185.149.145.6"),"Payload lost server");
   request.Server="185.149.145.6; calc.exe";Reject(()=>SystemDnsManager.EncodeRequest(request));
   request.Server="dns.dagami.ru";Reject(()=>SystemDnsManager.EncodeRequest(request));request.Server="185.149.145.6";
   request.JobId="../escape";Reject(()=>SystemDnsManager.EncodeRequest(request));request.JobId=Guid.NewGuid().ToString("N");
   request.Adapters=new SystemDnsAdapter[0];Reject(()=>SystemDnsManager.EncodeRequest(request));
   request.ChangeAdapters=false;SystemDnsManager.Validate(request);checks++;
   request.UseNrpt=false;Reject(()=>SystemDnsManager.EncodeRequest(request));
   request.Operation="Restore";SystemDnsManager.Validate(request);checks++;
   request.KeepExternalChanges=true;
   Assert(Encoding.UTF8.GetString(Convert.FromBase64String(SystemDnsManager.EncodeRequest(request))).Contains("\"KeepExternalChanges\":true"),"Preserve option lost in helper payload");
   Assert(SystemDnsManager.Script().Contains("function Invoke-SystemDns")&&SystemDnsManager.Script().Contains("cloudcode-pa.googleapis.com"),"Embedded DNS engine missing");
   Assert(SystemDnsManager.Friendly("Conflicting NRPT rule for x").Contains("резервной копией"),"NRPT conflict instruction untranslated");
   Assert(SystemDnsManager.Friendly("DNS rollback completed with preserved external settings. Backup retained.").Contains("с пропусками"),"Partial restore reported as full restore");
   Assert(SystemDnsManager.Friendly("A DNS backup is already active.").Contains("замена уже включена"),"Active backup has no recovery guidance");
   Assert(new SystemDnsRequest().KeepExternalChanges && new SystemDnsRequest().ReplaceActiveSetup,"CLI defaults still need manual recovery flags");
   Assert(SystemDnsManager.MatchesServerFamily("185.149.145.6, 2001:db8:1::53, 2001:db8:2::53","185.149.145.6"),"Status rejects mobile IPv6 DNS after a valid IPv4 write");
   Assert(!SystemDnsManager.MatchesServerFamily("192.0.2.53, 2001:db8::53","185.149.145.6"),"Status accepts wrong IPv4 DNS");
   Assert(!SystemDnsManager.MatchesServerFamily("185.149.145.6, 9.9.9.9, 2001:db8::53","185.149.145.6"),"Status accepts extra DNS in the changed family");
   Assert(SystemDnsManager.MatchesServerFamily("192.0.2.53, 2001:db8::6","2001:db8::6"),"Status rejects unchanged IPv4 after an IPv6 DNS write");
   Assert(SystemDnsManager.Friendly("DNS applied. Family: IPv4.").Contains("DNS IPv6 сохранён без изменений"),"IPv4 result hides retained IPv6 DNS");
   Assert(SystemDnsManager.Friendly("DNS server preflight failed: no answer").Contains("Настройки сети не менялись"),"Server preflight error does not state that DNS stayed unchanged");
   Assert(SystemDnsManager.Friendly("Connectivity preflight failed: offline").Contains("Настройки сети не менялись"),"Baseline error does not state that DNS stayed unchanged");
   Assert(SystemDnsManager.Friendly("Apply failed: HTTPS outage. Previous settings restored.").Contains("Прежние настройки возвращены"),"Automatic rollback result missing");
   Assert(SystemDnsManager.Friendly("Apply failed: HTTPS outage. Previous settings restored, but connectivity could not be verified: offline").Contains("Доступ к сети после отката не подтверждён"),"Unverified recovery falsely described as healthy");
   Assert(SystemDnsManager.Friendly("DNS applied. Family: IPv4. DNS and HTTPS checks passed.").Contains("Проверки DNS и HTTPS пройдены"),"Success does not distinguish network verification from DNS write");
   string guiPath=Path.Combine(Path.GetTempPath(),"DagamiAgentProxy.exe");
   var helper=SystemDnsManager.HelperStart(request,guiPath,false);
   Assert(helper.FileName==guiPath,"DNS helper does not reuse the only executable");
   string renamed=Path.Combine(Path.GetTempPath(),"My renamed Dagami.exe");
   Assert(SystemDnsManager.HelperStart(request,renamed,false).FileName==renamed,"Renamed program requires another EXE for DNS");
   Assert(helper.UseShellExecute && helper.Verb=="runas" && helper.WindowStyle==ProcessWindowStyle.Hidden,"Helper elevation must stay hidden");
   helper=SystemDnsManager.HelperStart(request,guiPath,true);
   Assert(!helper.UseShellExecute && helper.CreateNoWindow && string.IsNullOrEmpty(helper.Verb),"Already elevated helper requests UAC again");
   var engine=SystemDnsManager.EngineStart("Restore",SystemDnsManager.EncodeRequest(request),Path.Combine(Path.GetTempPath(),"DNS test.ps1"));
   Assert(!engine.UseShellExecute && engine.CreateNoWindow && engine.WindowStyle==ProcessWindowStyle.Hidden,"DNS engine can create a console");
   Assert(engine.RedirectStandardOutput && engine.RedirectStandardError && engine.Arguments.Contains("-NonInteractive -WindowStyle Hidden"),"Engine output is not kept inside the report");
   // Exercise the real process path with an inert script, never the DNS engine.
   string inert=Path.Combine(Environment.CurrentDirectory,"Dagami-dns-inert-"+Guid.NewGuid().ToString("N")+".ps1");
   try {
    File.WriteAllText(inert,"param($Operation,$RequestBase64)\r\nWrite-Output 'INERT_DNS_ENGINE_OK'\r\n",new UTF8Encoding(true));
    using(var child=Process.Start(SystemDnsManager.EngineStart("Restore",SystemDnsManager.EncodeRequest(request),inert))) {
     Assert(child.WaitForExit(10000),"Hidden engine did not finish");
     Assert(child.ExitCode==0 && child.StandardOutput.ReadToEnd().Contains("INERT_DNS_ENGINE_OK"),"Hidden PowerShell engine did not run");
     Assert(child.StandardError.ReadToEnd().Length==0,"Hidden engine failed to keep errors inside its report");
    }
   } finally {if(File.Exists(inert))File.Delete(inert);}
   string realGui=typeof(SystemDnsManager).Assembly.Location;
   bool elevated=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
   if(elevated) {
   using(var child=Process.Start(new ProcessStartInfo(realGui,"--brand-info"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8})) {
    Assert(child.WaitForExit(10000),"Single EXE CLI did not exit");
    Assert(child.ExitCode==0 && child.StandardOutput.ReadToEnd().Contains("Dagami Agent Proxy"),"Single EXE CLI failed with redirected output");
    Assert(child.StandardError.ReadToEnd().Length==0,"Single EXE CLI emitted an error");
   }
   Assert(!NativeCliApps.IsCli(new AppTarget{ExePath=realGui}),"DNS elevation target has a console subsystem");
   using(var child=Process.Start(new ProcessStartInfo(realGui,"--system-dns-helper Restore invalid-base64"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})) {
    Assert(child.WaitForExit(5000),"Invalid helper request opened a blocking window");
    Assert(child.ExitCode==1 || child.ExitCode==5,"Invalid helper request did not fail safely before DNS access");
   }
   } else {
    bool needsElevation=false;
    try{using(var child=Process.Start(new ProcessStartInfo(realGui,"--brand-info"){UseShellExecute=false,CreateNoWindow=true})) { child.WaitForExit(5000); }}
    catch(System.ComponentModel.Win32Exception ex){needsElevation=ex.NativeErrorCode==740;}
    Assert(needsElevation,"Windows allows the app to start without the requested admin rights");
   }
   Application.EnableVisualStyles();
   using(var form=new SystemDnsForm()) {
    using(var stream=typeof(SystemDnsManager).Assembly.GetManifestResourceStream("Dagami.AgentProxy.ico"))using(var icon=new Icon(stream))using(var actual=form.Icon.ToBitmap())using(var expected=icon.ToBitmap()){
     Assert(form.Icon!=null && actual.Size==expected.Size,"DNS window does not use the application icon");
     int differences=0;for(int y=0;y<actual.Height;y++)for(int x=0;x<actual.Width;x++)if(actual.GetPixel(x,y)!=expected.GetPixel(x,y))differences++;
     Assert(differences==0,"DNS icon differs from the executable icon");
    }
    var fields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    Assert(((CheckBox)typeof(SystemDnsForm).GetField("_nrpt",fields).GetValue(form)).Checked,"Default Smart DNS must use domain rules");
    Assert(!((CheckBox)typeof(SystemDnsForm).GetField("_changeAdapters",fields).GetValue(form)).Checked,"Default Smart DNS changes the whole network DNS");
    Assert(typeof(SystemDnsForm).GetField("_keepExternal",fields)==null,"Recovery still depends on an extra checkbox");
    var adapterList=(CheckedListBox)typeof(SystemDnsForm).GetField("_adapters",fields).GetValue(form);
    adapterList.Items.Add(new SystemDnsAdapter{Index=1,Guid=Guid.NewGuid().ToString(),Name="Wi Fi"},true);
    var create=typeof(SystemDnsForm).GetMethod("CreateRequest",fields);
    var apply=(SystemDnsRequest)create.Invoke(form,new object[]{"Apply"});
    Assert(apply.KeepExternalChanges && apply.ReplaceActiveSetup && apply.Adapters.Length==1,"Apply button does not recover an active setup in the same request");
    var restore=(SystemDnsRequest)create.Invoke(form,new object[]{"Restore"});
    Assert(restore.KeepExternalChanges,"Restore button still rejects VMware edits and missing devices");
    var encoded=Encoding.UTF8.GetString(Convert.FromBase64String(SystemDnsManager.EncodeRequest(apply)));
    Assert(encoded.Contains("\"ReplaceActiveSetup\":true") && encoded.Contains("\"KeepExternalChanges\":true"),"GUI recovery flags lost before UAC");
    Assert(((Button)typeof(SystemDnsForm).GetField("_apply",fields).GetValue(form)).Text.Contains("Записать DNS"),"Explicit system DNS write action missing");
    var resetButton=(Button)typeof(SystemDnsForm).GetField("_resetAll",fields).GetValue(form);
    foreach(string name in new[]{"_apply","_restore"}){var button=(Button)typeof(SystemDnsForm).GetField(name,fields).GetValue(form);Assert((bool)button.GetType().GetProperty("Primary").GetValue(button,null),"DNS action is not blue");}
    Assert(resetButton.Text.Contains("DNS и NRPT") && resetButton.BackColor==Color.FromArgb(185,28,28) && resetButton.ForeColor==Color.White && !resetButton.UseVisualStyleBackColor,"Full reset button is not visibly red");
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
    var loading=typeof(SystemDnsForm).GetField("_loading",fields);
    var deadline=DateTime.UtcNow.AddSeconds(10);
    while((bool)loading.GetValue(form) && DateTime.UtcNow<deadline){Application.DoEvents();System.Threading.Thread.Sleep(20);}
    Assert(!(bool)loading.GetValue(form) && resetButton.Enabled,"Full reset button remains disabled after adapter refresh");
    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save("native-system-dns.png");}
    Assert(form.Visible,"DNS UI did not open");form.Close();
   }
   Console.WriteLine("PASS "+checks+" DNS request validation, embedded resource, localized guidance and UI render checks; no system DNS changes");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
 }
}
