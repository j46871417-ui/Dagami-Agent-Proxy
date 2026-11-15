using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using DevProxyApp;

class SecurityReviewChecks {
 static int checks;
 static void Assert(bool value,string why){checks++;if(!value)throw new Exception(why);}
 static string Header(Stream input){var result=new StringBuilder();while(result.Length<65536){int b=input.ReadByte();if(b<0)throw new EndOfStreamException();result.Append((char)b);if(result.ToString().EndsWith("\r\n\r\n"))return result.ToString();}throw new IOException();}
 static void Send(Stream stream,string value){byte[] data=Encoding.ASCII.GetBytes(value);stream.Write(data,0,data.Length);stream.Flush();}
 static void Capacity(){
  var target=new TcpListener(IPAddress.Loopback,0);target.Start(160);var accepted=new List<TcpClient>();var clients=new List<TcpClient>();
  var worker=new Thread(()=>{try{while(true){var c=target.AcceptTcpClient();lock(accepted)accepted.Add(c);}}catch(SocketException){}}){IsBackground=true};worker.Start();
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"direct"),"Capacity bridge failed");
   for(int i=0;i<LocalProxyBridge.MaximumConcurrentConnections;i++){
    var c=new TcpClient("127.0.0.1",bridge.BoundPort);clients.Add(c);c.GetStream().ReadTimeout=10000;
    Send(c.GetStream(),"CONNECT 127.0.0.1:"+((IPEndPoint)target.LocalEndpoint).Port+" HTTP/1.1\r\nHost: localhost\r\n\r\n");
    Assert(Header(c.GetStream()).Contains(" 200 "),"Live tunnel unexpectedly rejected");
   }
   using(var denied=new TcpClient("127.0.0.1",bridge.BoundPort)){denied.GetStream().ReadTimeout=5000;Assert(Header(denied.GetStream()).Contains(" 503 "),"129th live tunnel bypassed capacity");}
   foreach(var c in clients)c.Close();lock(accepted)foreach(var c in accepted)c.Close();
   var watch=System.Diagnostics.Stopwatch.StartNew();while(bridge.ActiveConnections!=0&&watch.ElapsedMilliseconds<10000)Thread.Sleep(20);
   Assert(bridge.ActiveConnections==0,"Closed relay leaked active count");
   var slots=(SemaphoreSlim)typeof(LocalProxyBridge).GetField("_connectionSlots",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(bridge);
   Assert(slots.CurrentCount==LocalProxyBridge.MaximumConcurrentConnections,"Closed relay leaked semaphore lease");
  }}finally{foreach(var c in clients)c.Close();lock(accepted)foreach(var c in accepted)c.Close();target.Stop();worker.Join(2000);}
 }
 static void Http(){
  var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();Exception failure=null;var observed=new List<string>();
  var worker=new Thread(()=>{try{for(int i=0;i<2;i++)using(var peer=upstream.AcceptTcpClient()){
   var stream=peer.GetStream();stream.ReadTimeout=5000;observed.Add(Header(stream));
   Send(stream,"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK");
   Assert(stream.ReadByte()==-1,"Pipelined request escaped transaction framing");
  }}catch(Exception e){failure=e;}}){IsBackground=true};worker.Start();
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"http://fixture:password@127.0.0.1:"+((IPEndPoint)upstream.LocalEndpoint).Port),"HTTP bridge failed");
   for(int i=0;i<2;i++)using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
    client.GetStream().ReadTimeout=5000;
    Send(client.GetStream(),"GET http://fixture.invalid/first HTTP/1.1\r\nHost: fixture.invalid\r\nProxy-Authorization: Basic client-secret\r\n\r\nGET http://fixture.invalid/second HTTP/1.1\r\nHost: fixture.invalid\r\n\r\n");
    string response=Header(client.GetStream());Assert(response.Contains("Connection: close"),"Client was promised unsafe keep-alive");
    Assert(client.GetStream().ReadByte()=='O'&&client.GetStream().ReadByte()=='K'&&client.GetStream().ReadByte()==-1,"Response framing did not finish independently of upstream close");
   }
   Assert(worker.Join(10000)&&failure==null,"HTTP fixture failed: "+failure);
   string expected="Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:password"));
   Assert(observed.Count==2&&observed.TrueForAll(h=>h.Contains(expected)&&!h.Contains("client-secret")&&h.Contains("Connection: close")),"Proxy credentials lost or client credentials trusted");
  }}finally{upstream.Stop();worker.Join(2000);}
  bool rejected=false;try{HttpWire.RequestHeaders("POST / HTTP/1.1\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n",new Uri("http://fixture.invalid/"),true,null);}catch(InvalidDataException){rejected=true;}
  Assert(rejected,"Ambiguous HTTP body accepted");
 }
 static string B64(byte[] data){return Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_');}
 static void BidiRpc(){
  var backend=new TcpListener(IPAddress.Loopback,0);backend.Start();Exception failure=null;
  var worker=new Thread(()=>{try{using(var peer=backend.AcceptTcpClient()){
   var stream=peer.GetStream();stream.ReadTimeout=5000;Header(stream);
   string first="5\r\nhello\r\n";byte[] bytes=new byte[first.Length];int n=0;while(n<bytes.Length){int r=stream.Read(bytes,n,bytes.Length-n);if(r==0)throw new EndOfStreamException();n+=r;}
   Assert(Encoding.ASCII.GetString(bytes)==first,"Initial RPC chunk changed");
   Send(stream,"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nfirst\r\n");
   string end="";for(int i=0;i<5;i++){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();end+=(char)b;}
   Assert(end=="0\r\n\r\n","Final RPC chunk changed");Send(stream,"0\r\nx-rpc-status: ok\r\n\r\n");
  }}catch(Exception e){failure=e;}}){IsBackground=true};worker.Start();
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"direct"),"RPC bridge failed");using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
    var stream=client.GetStream();stream.ReadTimeout=5000;int port=((IPEndPoint)backend.LocalEndpoint).Port;
    Send(stream,"POST http://127.0.0.1:"+port+"/rpc HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n");
    Assert(Header(stream).Contains(" 200 "),"RPC waited for complete upload before response");
    string first="";for(int i=0;i<10;i++)first+=(char)stream.ReadByte();Assert(first=="5\r\nfirst\r\n","Early streaming response changed");
    Send(stream,"0\r\n\r\n");string rest="";int b;while((b=stream.ReadByte())>=0)rest+=(char)b;Assert(rest=="0\r\nx-rpc-status: ok\r\n\r\n","RPC response trailers changed");
   }
   Assert(worker.Join(7000)&&failure==null,"Bidirectional RPC fixture failed: "+failure);
  }}finally{backend.Stop();worker.Join(2000);}
 }
 static void Licenses(){
  Assert(typeof(MainForm).Assembly.GetType("DevProxyApp.LicenseManager")==null,"Application licensing returned");
  string error;
  Assert(!ProxyAccess.Connect("DAGAMI-TEST-9999-XVSF",out error),"Legacy application key granted proxy access");
  Assert(!ManagedAccess.IsAccessCode("DAGAMI2.fixture.invalid"),"Application license accepted as proxy key");
 }
 static void UpdateTransaction(string root){
  string stage=Path.Combine(root,"update-stage");Directory.CreateDirectory(stage);string candidate=Path.Combine(stage,"DagamiAgentProxy.exe");File.Copy(typeof(MainForm).Assembly.Location,candidate);
  string tag="v"+System.Diagnostics.FileVersionInfo.GetVersionInfo(candidate).ProductVersion;
  var release=new UpdateRelease{Tag=tag,Page="https://github.com/"+NativeUpdates.Repository+"/releases/tag/"+tag,Gui=new UpdateAsset{Name="DagamiAgentProxy.exe",Size=new FileInfo(candidate).Length,Sha256=NativeUpdates.FileHash(candidate),Url="https://github.com/"+NativeUpdates.Repository+"/releases/download/"+tag+"/DagamiAgentProxy.exe"}};
  string directory=Path.Combine(root,"installed");Directory.CreateDirectory(directory);string gui=Path.Combine(directory,"DagamiAgentProxy.exe");File.WriteAllText(gui,"old-fixture");
  string[] helpers={Path.Combine(directory,"DagamiRecovery.exe"),Path.Combine(directory,"DagamiUninstall.exe")};
  foreach(string helper in helpers)File.Copy(gui,helper);
  var plan=new UpdatePlan{Stage=stage,GuiPath=gui,OldGuiHash=NativeUpdates.FileHash(gui),Release=release,Restart=true};
  string backup=UpdateInstaller.InstallCore(plan,null,null);Assert(File.ReadAllText(Path.Combine(backup,"DagamiAgentProxy.exe"))=="old-fixture","Update backup lost");
  foreach(string helper in helpers)Assert(NativeUpdates.FileHash(helper)==release.Gui.Sha256,"Recovery helper not updated with GUI");
  Assert(UpdateInstaller.ShouldRestart(plan,true),"Successful verified update cannot restart");
  File.AppendAllText(gui,"tampered");bool refused=false;try{UpdateInstaller.ShouldRestart(plan,true);}catch(IOException){refused=true;}Assert(refused,"Tampered installed EXE can restart");
  File.WriteAllText(gui,"old-fixture");plan.OldGuiHash=NativeUpdates.FileHash(gui);refused=false;
  foreach(string helper in helpers)File.Copy(gui,helper,true);
  try{UpdateInstaller.InstallCore(plan,null,()=>{throw new IOException("fixture failure");});}catch(IOException){refused=true;}
  Assert(refused&&File.ReadAllText(gui)=="old-fixture","Update failure did not roll back");
  foreach(string helper in helpers)Assert(File.ReadAllText(helper)=="old-fixture","Recovery helper did not roll back");
  File.WriteAllText(helpers[0],"foreign");bool foreign=false;try{UpdateInstaller.InstallCore(plan,null,null);}catch(IOException){foreign=true;}
  Assert(foreign&&File.ReadAllText(helpers[0])=="foreign"&&File.ReadAllText(gui)=="old-fixture","Foreign helper overwritten or GUI changed");
 }
 static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"Dagami-security-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  typeof(Logger).GetField("_logFilePath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(root,"fixture.log"));
  try{
   Capacity();Http();BidiRpc();Licenses();UpdateTransaction(root);
   Assert(MainForm.PatchStatusLabel(2,"Changed shortcut",true)=="Проверить запуск","Damaged shortcuts displayed ready");
   Assert(MainForm.PatchStatusLabel(2,null,false)=="Пропатчено","Ready shortcuts lost state");
   Assert(MainForm.PatchStatusLabel(0,null,false)=="Не пропатчено","Unconfigured target displayed ready");
   Assert(!UpdateInstaller.ShouldRestart(new UpdatePlan{Restart=true,GuiPath="untrusted.exe"},false),"Failed helper can restart target");
   string exe=Path.Combine(root,"Gemini.exe");File.Copy(typeof(MainForm).Assembly.Location,exe);
   int port;using(var session=new NativeSession()){
    session.PrivateBridge=AppProxyPolicy.CreateGeminiBridge(new AppTarget{ExePath=exe},true,"http://127.0.0.1:1");
    Assert(session.PrivateBridge!=null,"Gemini did not receive private proxy-only bridge");port=session.PrivateBridge.BoundPort;
    Assert(typeof(LocalProxyBridge).GetField("_scopedDns",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(session.PrivateBridge)==null,"Gemini traffic can take Smart DNS instead of proxy");
   }
   bool closed=false;try{using(var probe=new TcpClient("127.0.0.1",port)){} }catch(SocketException){closed=true;}Assert(closed,"Session disposal left Gemini bridge open");
   Console.WriteLine("PASS "+checks+" real live-tunnel saturation/recovery, HTTP auth/framing, proxy-only key separation, damaged status and Gemini proxy-only lifecycle checks");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally{if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-security-",StringComparison.Ordinal))throw new IOException("Unsafe fixture cleanup");Directory.Delete(root,true);}
 }
}
