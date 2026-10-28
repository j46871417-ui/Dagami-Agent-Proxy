using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
 static int Main(){try {
  foreach(int status in new[]{200,407,403,503})Probe(status);
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
}
