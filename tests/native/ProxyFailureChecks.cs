using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Reflection;
using DevProxyApp;

class ProxyFailureChecks {
 static int checks;
 static void Assert(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
 static string Header(Stream stream){var result=new MemoryStream();int state=0;while(state<4){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();result.WriteByte((byte)b);state=(state==0||state==2)?(b==13?state+1:0):(b==10?state+1:0);if(result.Length>16384)throw new IOException("Header overflow");}return Encoding.ASCII.GetString(result.ToArray());}
 static void Probe(int status) {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception error=null;string request=null;int after=-2;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){peer.GetStream().ReadTimeout=4000;request=Header(peer.GetStream());byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" Fixture\r\nContent-Length: 0\r\n\r\n");peer.GetStream().Write(reply,0,reply.Length);after=peer.GetStream().ReadByte();}}catch(Exception e){error=e;}});worker.IsBackground=true;worker.Start();
  try {
   var result=UpstreamProxyDiagnostics.Check("http://fixture%40user:fixture%23password@127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port,3000);
   Assert(result.Status==status&&result.Available==(status==200),"TCP-only false positive or wrong CONNECT status");
   Assert(worker.Join(5000)&&error==null,"Proxy fixture failed");
   Assert(request.StartsWith("CONNECT www.googleapis.com:443 HTTP/1.1"),"Probe bypassed upstream");
   Assert(request.Contains("Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture@user:fixture#password"))),"Escaped credentials encoded incorrectly");
   Assert(after==-1,"Probe sent account/application bytes after CONNECT");
   Assert(!result.Message.Contains("fixture#password")&&!result.Message.Contains("fixture@user"),"Credentials exposed by diagnostics");
   if(status==407)Assert(result.Message.Contains("407")&&result.Message.Contains("пароль"),"407 has no recovery guidance");
   if(status==403||status==503)Assert(result.Message.Contains("CONNECT")&&result.Message.Contains(status.ToString()),"Upstream refusal confused with model/account response");
  }finally{server.Stop();worker.Join(1000);}
 }
 static void MetadataHostIsExact(){
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();string received=null;Exception failure=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){peer.GetStream().ReadTimeout=5000;received=Header(peer.GetStream());byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");peer.GetStream().Write(response,0,response.Length);}}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"Metadata fixture bridge failed");
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes("GET http://metadata.google.internal/computeMetadata/v1/ HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");c.GetStream().Write(b,0,b.Length);Assert(Header(c.GetStream()).Contains(" 404 "),"Metadata endpoint not intercepted");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;var stream=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("CONNECT metadata.google.internal:80 HTTP/1.1\r\nHost: metadata.google.internal:80\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 200 "),"Metadata CONNECT tunnel not established");b=Encoding.ASCII.GetBytes("GET /computeMetadata/v1/ HTTP/1.1\r\nHost: metadata.google.internal\r\nMetadata-Flavor: Google\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 404 "),"Metadata CONNECT request did not receive 404");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;var stream=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("CONNECT metadata.google.internal:80 HTTP/1.1\r\nHost: metadata.google.internal:80\r\n\r\nGET / HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 200 ")&&Header(stream).Contains(" 404 "),"Pipelined metadata CONNECT request lost bytes");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes("GET http://example.test/metadata.google.internal HTTP/1.1\r\nHost: example.test\r\n\r\n");c.GetStream().Write(b,0,b.Length);Assert(Header(c.GetStream()).Contains(" 204 "),"Metadata substring intercepted unrelated request");}
   Assert(worker.Join(5000)&&failure==null&&received.StartsWith("GET http://example.test/"),"Unrelated request did not reach configured proxy");
  }}finally{server.Stop();worker.Join(1000);}
 }
 static int Main(){try {
  FailedProxyMustNotConnectDirectly();
  foreach(int status in new[]{200,407,403,503})Probe(status);
  MetadataHostIsExact();
  Assert(UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru:50128"),"Known DNS endpoint not identified");
  Assert(!UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru:pw@proxy.example:8080"),"Username mistaken for DNS proxy host");
  Assert(!UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru.evil.example:8080"),"DNS hostname substring matched");
  var destination=(Uri)typeof(LocalProxyBridge).GetMethod("HttpDestination",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"GET","http://example.test:8080/a?q=1","GET http://example.test:8080/a?q=1 HTTP/1.1\r\nHost: example.test\r\nProxy-Authorization: Basic secret\r\n\r\n"});
  string direct=(string)typeof(LocalProxyBridge).GetMethod("LoopbackRequest",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"GET http://example.test:8080/a?q=1 HTTP/1.1\r\nHost: example.test\r\nProxy-Authorization: Basic secret\r\n\r\n",destination});
  Assert(destination.Host=="example.test"&&destination.Port==8080&&direct.StartsWith("GET /a?q=1 HTTP/1.1")&&!direct.Contains("secret")&&!direct.Contains("Proxy-Authorization"),"Direct request leaked proxy credentials or used wrong target");
  foreach(string bad in new[]{"socks5://127.0.0.1:1","http://user:bad#password@127.0.0.1:1","http://127.0.0.1:1/path","http://127.0.0.1:1/?token=fixture"}) {
   var result=UpstreamProxyDiagnostics.Check(bad);Assert(!result.Available&&result.Status==0,"Malformed proxy URL accepted");
  }
  var occupied=new List<TcpListener>();int preferred=0;
  for(int candidate=54000;candidate<55500;candidate+=10) {
   try{for(int port=candidate;port<candidate+10;port++){var listener=new TcpListener(IPAddress.Loopback,port);listener.ExclusiveAddressUse=true;listener.Start();occupied.Add(listener);}preferred=candidate;break;}
   catch(SocketException){foreach(var listener in occupied)listener.Stop();occupied.Clear();}
  }
  Assert(preferred>0,"Cannot reserve fixture port range");
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(preferred,"http://127.0.0.1:1"),"Ten occupied ports prevent bridge startup");
   Assert(bridge.BoundPort>0&&(bridge.BoundPort<preferred||bridge.BoundPort>=preferred+10),"Ephemeral fallback not used");
  }}finally{foreach(var listener in occupied)listener.Stop();}
  using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(preferred,"http://127.0.0.1:"+preferred),"Self-upstream fixture startup failed");
   Assert(bridge.BoundPort!=preferred,"Bridge selected itself as its upstream");
  }
  int calls=0;
  string report=AntigravityDiagnostics.TokenRouteReport(12345,true,null,"http://127.0.0.1:1",port=>{calls++;if(port==12345)throw new TimeoutException("fixture");return "synthetic POST expected 401";});
  Assert(calls==2&&report.Contains("upstream без Smart DNS")&&report.Contains("expected 401"),"OAuth timeout did not compare synthetic routes");
  calls=0;report=AntigravityDiagnostics.TokenRouteReport(12345,true,null,"",port=>{calls++;throw new TimeoutException("fixture");});
  Assert(calls==1&&report.Contains("Сравнение недоступно"),"Missing comparison settings not handled");
  Console.WriteLine("PASS "+checks+" CONNECT auth/status, URL validation, port fallback, self-upstream and synthetic OAuth comparison checks");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}

 sealed class NoDirectBridge:LocalProxyBridge {
  public int DirectCalls;
  protected override TcpClient ConnectDirect(string host,int port,out Stream stream){
   Interlocked.Increment(ref DirectCalls);stream=null;throw new IOException("Fixture intercepted forbidden direct route before DNS/network");
  }
 }
 static void FailedProxyMustNotConnectDirectly(){
  using(var unavailable=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)){
   unavailable.Bind(new IPEndPoint(IPAddress.Loopback,0));
   using(var bridge=new NoDirectBridge()){
    Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)unavailable.LocalEndPoint).Port),"Failure fixture bridge did not start");
    // Resolver is enabled, but the unscoped host must never be resolved/dialled.
    typeof(LocalProxyBridge).GetField("_scopedDns",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bridge,new ScopedDnsResolver("192.0.2.53"));
    foreach(string method in new[]{"CONNECT","GET"})using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
     client.GetStream().ReadTimeout=5000;string authority="outside.fixture.invalid:443";
     string request=method+" "+(method=="CONNECT"?authority:"http://outside.fixture.invalid/fixture")+" HTTP/1.1\r\nHost: "+authority+"\r\n\r\n";
     byte[] bytes=Encoding.ASCII.GetBytes(request);client.GetStream().Write(bytes,0,bytes.Length);
     string reply=Header(client.GetStream());
     Assert(reply.Contains(" 502 ")&&reply.Contains("X-Dagami-Failure-Phase: upstream-connect"),"Proxy failure was not reported at the selected route");
     Assert(bridge.DirectCalls==0,"Failed configured proxy silently attempted direct "+method);
    }
   }
  }
 }
}
