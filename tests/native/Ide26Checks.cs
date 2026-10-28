using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DevProxyApp;

class Ide26Checks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static byte[] Header(Stream stream){var data=new MemoryStream();int state=0;while(state<4){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();data.WriteByte((byte)b);state=(state==0||state==2)?(b==13?state+1:0):(b==10?state+1:0);if(data.Length>16384)throw new IOException("Header overflow");}return data.ToArray();}
 static string ReadAll(Stream stream){var bytes=new MemoryStream();byte[] b=new byte[8192];int n;while((n=stream.Read(b,0,b.Length))>0)bytes.Write(b,0,n);return Encoding.ASCII.GetString(bytes.ToArray());}
 static string Request(int port,string request){using(var c=new TcpClient("127.0.0.1",port)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes(request);c.GetStream().Write(b,0,b.Length);return ReadAll(c.GetStream());}}
 static void Rpc(LocalProxyBridge bridge,bool origin,bool ipv6) {
  var server=new TcpListener(ipv6?IPAddress.IPv6Loopback:IPAddress.Loopback,0);server.Start();
  int port=((IPEndPoint)server.LocalEndpoint).Port;
  string authority=(ipv6?"[::1]":"localhost")+":"+port,observed=null,body=null;Exception failure=null;
  string wire="5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n";
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){
   var s=peer.GetStream();s.ReadTimeout=5000;observed=Encoding.ASCII.GetString(Header(s));
   byte[] received=new byte[wire.Length];int count=0;while(count<received.Length){int n=s.Read(received,count,received.Length-count);if(n==0)break;count+=n;}body=Encoding.ASCII.GetString(received,0,count);
   foreach(string part in new[]{"HTTP/1.1 200 OK\r\nContent-Type: application/connect+proto\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nfirst\r\n","6\r\nsecond\r\n0\r\nx-rpc-status: ok\r\n\r\n"}){byte[] b=Encoding.ASCII.GetBytes(part);s.Write(b,0,b.Length);s.Flush();Thread.Sleep(40);}
  }}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try {
   string path="/test.Service/Stream?value=1",target=origin?path:"http://"+authority+path;
   string response=Request(bridge.BoundPort,"POST "+target+" HTTP/1.1\r\nHost: "+authority+"\r\nx-codeium-csrf-token: fixture-csrf\r\nAuthorization: Bearer fixture-user\r\nProxy-Authorization: Basic fixture-proxy\r\nContent-Type: application/connect+proto\r\nTransfer-Encoding: chunked\r\n\r\n"+wire);
   Assert(worker.Join(6000)&&failure==null,"Local RPC server failed");
   Assert(observed.StartsWith("POST "+path+" HTTP/1.1\r\n"),"Absolute URI sent to local backend");
   Assert(observed.Contains("x-codeium-csrf-token: fixture-csrf")&&observed.Contains("Authorization: Bearer fixture-user"),"CSRF/auth modified");
   Assert(!observed.Contains("fixture-proxy")&&!observed.Contains("upstream-password"),"Proxy credentials leaked to backend");
   Assert(body==wire,"Chunked ConnectRPC request changed");
   Assert(response.Contains("5\r\nfirst\r\n6\r\nsecond\r\n0\r\nx-rpc-status: ok"),"Streaming/trailers changed");
  }finally{server.Stop();worker.Join(1000);}
 }
 static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"Dagami-ide26-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try {
   string install=Path.Combine(root,"Programs","Antigravity IDE"),local=Path.Combine(root,"local");Directory.CreateDirectory(install);
   string ide=Path.Combine(install,"Antigravity IDE.exe");File.Copy(typeof(MainForm).Assembly.Location,ide);
   var target=new AppTarget{Id="manual-ide",ExePath=ide};
   Assert(AntigravityIntegration.Supports(ide)&&AntigravityIntegration.IsIde(ide),"IDE filename unsupported");
   Assert(ApplicationSupport.Kind(target)=="vscode"&&!NativeCliApps.IsCli(target),"IDE treated as CLI");
   Assert(ApplicationSupport.SettingsPath(target).Contains(@"Antigravity IDE\User\settings.json"),"IDE uses Manager settings");
   Assert(ApplicationDiscovery.AntigravityIde(new[]{Path.Combine(root,"Programs")})==ide,"IDE install discovery failed");
   bool missing=false;try{AntigravityIntegration.BackendPaths(ide,local);}catch(UnsupportedAntigravityBinaryException e){missing=e.Message.Contains("agy");}Assert(missing,"Missing external CLI silently accepted");
   string agy=Path.Combine(local,@"agy\bin\agy.exe");Directory.CreateDirectory(Path.GetDirectoryName(agy));
   byte[] binary=Encoding.ASCII.GetBytes("Go build ID: fixture\0\n\nineligible\x18\x09\0name=ineligible,proto3\0https_proxy");File.WriteAllBytes(agy,binary);
   Assert(AntigravityIntegration.BackendPaths(ide,local).Count==1&&AntigravityIntegration.BackendPaths(ide,local)[0]==agy,"External agy not discovered");
   Assert(AntigravityIntegration.PrepareClient(agy).Prepared,"External agy preparation failed");AntigravityIntegration.RestoreClient(agy);
   Assert(Convert.ToBase64String(File.ReadAllBytes(agy))==Convert.ToBase64String(binary),"External agy restore failed");
   string extension=Path.Combine(install,@"resources\app\extensions\agy"),backend=Path.Combine(extension,@"bin\language_server_windows_x64.exe");Directory.CreateDirectory(Path.GetDirectoryName(backend));File.WriteAllBytes(backend,binary);
   string js=Path.Combine(extension,"extension.js");File.WriteAllText(js,"fixture: createConnectTransport; x-codeium-csrf-token; getUserStatus absent");
   Assert(AntigravityIntegration.BackendPaths(ide,local).Count==2&&AntigravityIntegration.BackendPaths(ide,local).Contains(backend),"New plugin or shared backend ignored");
   Assert(AntigravityIntegration.PrepareClient(ide).Prepared,"New plugin binary preparation failed");AntigravityIntegration.RestoreClient(ide);
   Assert(File.ReadAllText(js).Contains("getUserStatus absent"),"Extension/auth rewritten");
   target.ConfigPath=Path.Combine(root,"settings.json");string settings="{\"antigravity.serverUrl\":\"http://127.0.0.1:52101\",\"custom\":true}";File.WriteAllText(target.ConfigPath,settings);string message;
   Assert(ProxyEngine.ApplyProxyToTarget(target,"http://127.0.0.1:52102",out message),"Valid serverUrl blocked");
   Assert(File.ReadAllText(target.ConfigPath).Contains("52101"),"serverUrl overwritten with bridge");
   Assert(ProxyEngine.RestoreTarget(target,out message),"Settings rollback failed");
   string bad=settings.Replace("52101","52102");File.WriteAllText(target.ConfigPath,bad);
   Assert(!ProxyEngine.ApplyProxyToTarget(target,"http://127.0.0.1:52102",out message)&&message.Contains("serverUrl"),"Proxy-as-serverUrl accepted");
   Assert(File.ReadAllText(target.ConfigPath)==bad&&!File.Exists(target.ConfigPath+".devproxy.bak"),"Rejected settings modified");
   Assert(LocalProxyBridge.LoopbackDestination("CONNECT","[::1]:1234","").Port==1234,"IPv6 loopback rejected");
   Assert(LocalProxyBridge.LoopbackDestination("CONNECT","[::ffff:127.0.0.1]:1234","")!=null,"IPv4-mapped loopback leaked upstream");
   Assert(!LocalProxyBridge.IsBridgeEndpoint(new Uri("http://[::1]:1234"),1234)&&LocalProxyBridge.IsBridgeEndpoint(new Uri("http://[::ffff:127.0.0.1]:1234"),1234),"Independent IPv6 backend treated as self-loop");
   Assert(LocalProxyBridge.LoopbackDestination("GET","http://localhost.evil.invalid/x","")==null,"Hostname bypass accepted");
   Assert(LocalProxyBridge.LoopbackDestination("CONNECT","192.168.1.1:443","")==null,"LAN bypass accepted");
   var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();
   try{using(var bridge=new LocalProxyBridge()){
    Assert(bridge.Start(0,"http://user:upstream-password@127.0.0.1:"+((IPEndPoint)upstream.LocalEndpoint).Port),"Bridge failed");
    Rpc(bridge,false,false);Rpc(bridge,true,false);if(Socket.OSSupportsIPv6)Rpc(bridge,false,true);
    Assert(Request(bridge.BoundPort,"POST http://127.0.0.1:"+bridge.BoundPort+"/rpc HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n").Contains(" 502 "),"Self-loop not rejected");
    Assert(!upstream.Pending(),"Local RPC leaked to external upstream");
    using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
     client.GetStream().ReadTimeout=5000;byte[] request=Encoding.ASCII.GetBytes("CONNECT localhost:"+((IPEndPoint)upstream.LocalEndpoint).Port+" HTTP/1.1\r\nHost: localhost\r\n\r\n");client.GetStream().Write(request,0,request.Length);
     using(var peer=upstream.AcceptTcpClient()){
      Assert(Encoding.ASCII.GetString(Header(client.GetStream())).Contains(" 200 "),"Local CONNECT failed");
      byte[] payload=Encoding.ASCII.GetBytes("opaque-fixture");client.GetStream().Write(payload,0,payload.Length);byte[] received=new byte[payload.Length];int count=0;peer.GetStream().ReadTimeout=5000;
      while(count<received.Length){int n=peer.GetStream().Read(received,count,received.Length-count);if(n==0)break;count+=n;}
      Assert(count==payload.Length&&Convert.ToBase64String(payload)==Convert.ToBase64String(received),"Local CONNECT data lost");
      bridge.Dispose();bool closed=false;try{closed=client.GetStream().ReadByte()<0;}catch(IOException){closed=true;}Assert(closed,"Disposed bridge retained an old live route");
     }
    }
   }}finally{upstream.Stop();}
   Console.WriteLine("PASS "+checks+" IDE 2.6 discovery/settings/backends, CSRF/ConnectRPC streaming, loopback routing and shutdown checks");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally{
   string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-ide26-"))throw new IOException("Unsafe fixture cleanup");
   Directory.Delete(root,true);
  }
 }
}
