using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using DevProxyApp;
class UpdateChecks {
 static int checks;
 static void Assert(bool value,string why){checks++;if(!value)throw new Exception(why);}
 static void Reject(Action action){bool rejected=false;try{action();}catch(Exception ex){if(!(ex is IOException)&&!(ex is InvalidDataException)&&!(ex is FormatException)&&!(ex is InvalidOperationException)&&!(ex is System.Security.Cryptography.CryptographicException))throw;rejected=true;}Assert(rejected,"Expected update refusal");}
 class Paths:ICliPathEnvironment{public string UserPath{get;set;}public string ProcessPath{get;set;}public string MachinePath{get{return "";}}public void Notify(){}}
 static Dictionary<string,object> Entry(string tag,bool pre,bool draft,bool branded=true){
  var assets=new List<object>();if(branded)foreach(string name in new[]{"DagamiAgentProxy.exe"})assets.Add(new{name=name,state="uploaded",size=1024,digest="sha256:"+new string('a',64),browser_download_url="https://github.com/"+NativeUpdates.Repository+"/releases/download/"+tag+"/"+name});
  return new Dictionary<string,object>{{"tag_name",tag},{"prerelease",pre},{"draft",draft},{"html_url","https://github.com/"+NativeUpdates.Repository+"/releases/tag/"+tag},{"assets",assets.ToArray()}};
 }
 static UpdateRelease Payload(string work,string stage){
  Directory.CreateDirectory(stage);File.Copy(Path.Combine(work,"DagamiAgentProxy.exe"),Path.Combine(stage,"DagamiAgentProxy.exe"),true);
  string tag="v"+FileVersionInfo.GetVersionInfo(Path.Combine(stage,"DagamiAgentProxy.exe")).ProductVersion.Split('+')[0];Func<string,UpdateAsset> asset=name=>new UpdateAsset{Name=name,Size=new FileInfo(Path.Combine(stage,name)).Length,Sha256=NativeUpdates.FileHash(Path.Combine(stage,name)),Url="https://github.com/"+NativeUpdates.Repository+"/releases/download/"+tag+"/"+name};
  return new UpdateRelease{Tag=tag,Page="https://github.com/"+NativeUpdates.Repository+"/releases/tag/"+tag,Prerelease=true,Gui=asset("DagamiAgentProxy.exe")};
 }
 static UpdatePlan Plan(string root,string work){string stage=Path.Combine(root,"stage"),install=Path.Combine(root,"installed");Directory.CreateDirectory(install);var release=Payload(work,stage);string gui=Path.Combine(install,"DagamiAgentProxy.exe");File.WriteAllText(gui,"old-gui");return new UpdatePlan{Stage=stage,GuiPath=gui,OldGuiHash=NativeUpdates.FileHash(gui),Release=release};}
 static void Unchanged(UpdatePlan p){Assert(File.ReadAllText(p.GuiPath)=="old-gui"&&Directory.GetFiles(Path.GetDirectoryName(p.GuiPath),"*.exe").Length==1,"Failed update changed the program or created extra EXE");}
 static void Layout(Control parent){foreach(Control c in parent.Controls)if(c.Visible){Assert(c.Left>=0&&c.Top>=0&&c.Right<=parent.ClientSize.Width+2&&c.Bottom<=parent.ClientSize.Height+2,"Clipped updater control "+c.Text);Layout(c);}}
 [STAThread]static int Main(){string root=Path.Combine(Path.GetTempPath(),"Dagami-update-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{
  Assert(UpdateVersion.Parse("v0.1.0-alpha.10").CompareTo(UpdateVersion.Parse("v0.1.0-alpha.2"))>0,"Numeric prerelease order");
  Assert(UpdateVersion.Parse("v0.1.0").CompareTo(UpdateVersion.Parse("v0.1.0-alpha.99"))>0,"Stable before alpha");
  Assert(UpdateVersion.Parse("0.1.0-alpha.5+build.2").CompareTo(UpdateVersion.Parse("v0.1.0-alpha.5"))==0,"Build metadata changes order");
  Reject(()=>UpdateVersion.Parse("v0.1.0/../../bad"));
  Reject(()=>UpdateVersion.Parse("v0.1.0-alpha..2"));Reject(()=>UpdateVersion.Parse("v0.1.0-alpha.02"));
  var json=new JavaScriptSerializer();var entries=new[]{Entry("v9.0.0",false,false,false),Entry("v0.1.0-alpha.2",true,false),Entry("v0.1.0-alpha.8",true,true),Entry("v0.1.0-alpha.6",true,false)};
  Assert(NativeUpdates.SelectRelease(json.Serialize(entries),true).Tag=="v0.1.0-alpha.6","Did not select prerelease pair");
  Assert(NativeUpdates.SelectRelease(json.Serialize(entries),false)==null,"Excluded channel leaked alpha");
  Assert(NativeUpdates.SelectRelease(json.Serialize(new[]{Entry("v0.1.0",false,false),Entry("v0.1.0-alpha.9",true,false)}),true).Tag=="v0.1.0","Stable SemVer incorrectly ordered");
  foreach(string url in new[]{"http://github.com/file","https://github.com.evil.invalid/file","https://user:secret@github.com/file","https://github.com:444/file","https://example.com/file"})Assert(!UpdateNetwork.Allowed(new Uri(url)),"Bad redirect allowed");
  foreach(string host in new[]{"api.github.com","github.com","release-assets.githubusercontent.com","objects.githubusercontent.com"})Assert(UpdateNetwork.Allowed(new Uri("https://"+host+"/file")),"GitHub host missing");
  string work=Path.GetDirectoryName(typeof(MainForm).Assembly.Location);
  string archiveStage=Path.Combine(root,"archive-source");var zipRelease=Payload(work,archiveStage);byte[] zip;
  using(var bytes=new MemoryStream()){using(var archive=new ZipArchive(bytes,ZipArchiveMode.Create,true)){
   foreach(string name in new[]{"DagamiAgentProxy.exe"})using(var stream=archive.CreateEntry(name).Open()){byte[] raw=File.ReadAllBytes(Path.Combine(archiveStage,name));stream.Write(raw,0,raw.Length);}
   using(var writer=new StreamWriter(archive.CreateEntry("SHA256-native.json").Open(),new UTF8Encoding(false)))writer.Write(json.Serialize(new Dictionary<string,string>{{"DagamiAgentProxy.exe",zipRelease.Gui.Sha256}}));
   using(var writer=new StreamWriter(archive.CreateEntry("../must-not-extract.txt").Open()))writer.Write("ignored");
  }zip=bytes.ToArray();}
  zipRelease.Archive=new UpdateAsset{Name="Dagami-Agent-Proxy-"+zipRelease.Tag+"-windows.zip",Url="https://github.com/"+NativeUpdates.Repository+"/releases/download/"+zipRelease.Tag+"/Dagami-Agent-Proxy-"+zipRelease.Tag+"-windows.zip",Size=zip.Length,Sha256=NativeUpdates.Hash(zip)};zipRelease.Gui=zipRelease.Cli=null;
  string extracted=Path.Combine(root,"extracted");NativeUpdates.ExtractArchive(zipRelease,zip,extracted);NativeUpdates.ValidatePayload(zipRelease,extracted);Assert(!File.Exists(Path.Combine(root,"must-not-extract.txt"))&&Directory.GetFiles(extracted).Length==1&&zipRelease.Cli==null,"ZIP wrote extra files or requires another EXE");
  var archiveEntry=Entry(zipRelease.Tag,true,false,false);archiveEntry["assets"]=new[]{new{name=zipRelease.Archive.Name,state="uploaded",size=zip.Length,digest="sha256:"+zipRelease.Archive.Sha256,browser_download_url=zipRelease.Archive.Url}};
  Assert(NativeUpdates.SelectRelease(json.Serialize(new[]{archiveEntry}),true).Archive!=null,"ZIP-only release ignored");
  zip[zip.Length-1]^=1;Reject(()=>NativeUpdates.ExtractArchive(zipRelease,zip,Path.Combine(root,"corrupt-zip")));
  var success=Plan(Path.Combine(root,"success"),work);NativeUpdates.ValidatePayload(success.Release,success.Stage);checks++;
  string planFile=Path.Combine(root,"plan.dat");NativeUpdates.SaveProtected(planFile,success);Assert(NativeUpdates.ReadProtected<UpdatePlan>(planFile).GuiPath==success.GuiPath,"DPAPI plan roundtrip");byte[] protectedBytes=File.ReadAllBytes(planFile);protectedBytes[protectedBytes.Length-1]^=1;File.WriteAllBytes(planFile,protectedBytes);Reject(()=>NativeUpdates.ReadProtected<UpdatePlan>(planFile));
  string backup=UpdateInstaller.InstallCore(success,null,null);Assert(NativeUpdates.FileHash(success.GuiPath)==success.Release.Gui.Sha256&&Directory.GetFiles(Path.GetDirectoryName(success.GuiPath),"*.exe").Length==1,"Single EXE not installed");Assert(File.ReadAllText(Path.Combine(backup,"DagamiAgentProxy.exe"))=="old-gui","Backup not retained");
  var partial=Plan(Path.Combine(root,"partial"),work);Reject(()=>UpdateInstaller.InstallCore(partial,null,()=>{throw new IOException("fixture failure after replace");}));Unchanged(partial);
  var locked=Plan(Path.Combine(root,"locked"),work);using(var held=new FileStream(locked.GuiPath,FileMode.Open,FileAccess.Read,FileShare.Read)){Reject(()=>UpdateInstaller.InstallCore(locked,null,null));Unchanged(locked);}
  var changed=Plan(Path.Combine(root,"changed"),work);File.WriteAllText(changed.GuiPath,"foreign-new");Reject(()=>UpdateInstaller.InstallCore(changed,null,null));Assert(File.ReadAllText(changed.GuiPath)=="foreign-new","Late foreign change overwritten");
  var corrupt=Plan(Path.Combine(root,"corrupt"),work);File.AppendAllText(Path.Combine(corrupt.Stage,"DagamiAgentProxy.exe"),"tampered");Reject(()=>UpdateInstaller.InstallCore(corrupt,null,null));Unchanged(corrupt);
  var badUrl=Payload(work,Path.Combine(root,"bad-url"));badUrl.Gui.Url="https://github.com/attacker/repo/releases/download/v1/a.exe";Reject(()=>NativeUpdates.ValidateRelease(badUrl));
  badUrl=Payload(work,Path.Combine(root,"bad-sha"));badUrl.Gui.Sha256="missing";Reject(()=>NativeUpdates.ValidateRelease(badUrl));
  var wrongVersion=Payload(work,Path.Combine(root,"wrong-version"));string actualTag=wrongVersion.Tag;wrongVersion.Tag="v9.9.9";wrongVersion.Page=wrongVersion.Page.Replace(actualTag,wrongVersion.Tag);wrongVersion.Gui.Url=wrongVersion.Gui.Url.Replace(actualTag,wrongVersion.Tag);Reject(()=>NativeUpdates.ValidatePayload(wrongVersion,Path.Combine(root,"wrong-version")));
  var paths=new Paths{UserPath="original;foreign",ProcessPath="original;foreign"};var store=new NativeCliApps(Path.Combine(root,"cli-store"),paths);string original=Path.Combine(root,"agy.ps1");File.WriteAllText(original,"param()\r\n");var app=new AppTarget{Id="update-fixture",ExePath=original};string shim=store.Enable(app,Path.Combine(work,"DagamiAgentProxy.exe"));
  // Change the source runner bytes without editing a signed/versioned vendor file.
  string nextRunner=Path.Combine(root,"runner.exe");File.Copy(Path.Combine(work,"DagamiAgentProxy.exe"),nextRunner);File.AppendAllText(nextRunner,"fixture-next-runner");Assert(store.RefreshRunner(nextRunner)==1,"CLI refresh skipped");string issue;Assert(store.VerifiedCount(app.Id,out issue)==1&&issue==null,"Refreshed CLI hash/journal mismatch");
  var cliFailure=Plan(Path.Combine(root,"cli-failure"),work);File.AppendAllText(shim,"foreign");Reject(()=>UpdateInstaller.InstallCore(cliFailure,null,()=>store.RefreshRunner(Path.Combine(work,"DagamiAgentProxy.exe"))));Unchanged(cliFailure);
  using(NativeUpdates.AcquireActivity(true)){Assert(NativeUpdates.CliBusy(),"Active CLI not detected");Reject(()=>{using(NativeUpdates.ActivityLock("activity.lock",true)){};});}
  using(NativeUpdates.ActivityLock("activity.lock",true)){Reject(()=>NativeUpdates.AcquireActivity(false));}
  // A CONNECT handler returns before its two relay threads. The updater must
  // still see a live tunnel as busy until both directions have closed.
  var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
  var peerThread=new Thread(()=>{using(var peer=listener.AcceptTcpClient()){var stream=peer.GetStream();stream.ReadTimeout=5000;var header=new StringBuilder();while(!header.ToString().EndsWith("\r\n\r\n")){int b=stream.ReadByte();if(b<0)return;header.Append((char)b);}byte[] ok=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n");stream.Write(ok,0,ok.Length);stream.ReadByte();}}){IsBackground=true};peerThread.Start();
  using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port),"Activity fixture bridge failed");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
    var stream=client.GetStream();stream.ReadTimeout=5000;byte[] connect=Encoding.ASCII.GetBytes("CONNECT activity.invalid:443 HTTP/1.1\r\nHost: activity.invalid:443\r\n\r\n");stream.Write(connect,0,connect.Length);var header=new StringBuilder();while(!header.ToString().EndsWith("\r\n\r\n")){int b=stream.ReadByte();if(b<0)throw new IOException("No CONNECT reply");header.Append((char)b);}Thread.Sleep(50);Assert(bridge.ActiveConnections>0,"Updater considers a live relay idle");
   }
   var clock=Stopwatch.StartNew();while(bridge.ActiveConnections!=0&&clock.Elapsed.TotalSeconds<5)Thread.Sleep(20);Assert(bridge.ActiveConnections==0,"Closed relay left activity lease");
  }listener.Stop();Assert(peerThread.Join(5000),"Activity fixture did not finish");
  Application.EnableVisualStyles();using(var form=new UpdateForm(()=>null,v=>{},()=>{},true)){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();Layout(form);form.Size=form.MinimumSize;Application.DoEvents();Layout(form);form.Close();}
  Console.WriteLine("PASS "+checks+" update feed/version/URL/hash/DPAPI, single EXE install and rollback, locked files, CLI refresh/activity and updater layout checks");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{string resolved=Path.GetFullPath(root),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(resolved.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("Dagami-update-check-"))try{Directory.Delete(resolved,true);}catch{}}}
}
