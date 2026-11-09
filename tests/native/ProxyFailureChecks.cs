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
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
    var stream=client.GetStream();stream.ReadTimeout=5000;
    byte[] request=Encoding.ASCII.GetBytes("GET "+SelectiveProxyRules.Path+" HTTP/1.1\r\nHost: 127.0.0.1:"+bridge.BoundPort+"\r\n\r\n");
    stream.Write(request,0,request.Length);
    Assert(Header(stream).Contains(" 404 "),"PAC must not advertise an unkeyed bridge");
   }
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes("GET http://metadata.google.internal/computeMetadata/v1/ HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");c.GetStream().Write(b,0,b.Length);Assert(Header(c.GetStream()).Contains(" 404 "),"Metadata endpoint not intercepted");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;var stream=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("CONNECT metadata.google.internal:80 HTTP/1.1\r\nHost: metadata.google.internal:80\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 200 "),"Metadata CONNECT tunnel not established");b=Encoding.ASCII.GetBytes("GET /computeMetadata/v1/ HTTP/1.1\r\nHost: metadata.google.internal\r\nMetadata-Flavor: Google\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 404 "),"Metadata CONNECT request did not receive 404");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;var stream=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("CONNECT metadata.google.internal:80 HTTP/1.1\r\nHost: metadata.google.internal:80\r\n\r\nGET / HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 200 ")&&Header(stream).Contains(" 404 "),"Pipelined metadata CONNECT request lost bytes");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes("GET http://example.test/metadata.google.internal HTTP/1.1\r\nHost: example.test\r\n\r\n");c.GetStream().Write(b,0,b.Length);Assert(Header(c.GetStream()).Contains(" 204 "),"Metadata substring intercepted unrelated request");}
   Assert(worker.Join(5000)&&failure==null&&received.StartsWith("GET http://example.test/"),"Unrelated request did not reach configured proxy");
  }}finally{server.Stop();worker.Join(1000);}
 }
 static void SocksBridge(bool rejectAuth) {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception failure=null;string observedHost=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){var stream=peer.GetStream();stream.ReadTimeout=4000;
   byte[] greeting=new byte[3];for(int i=0;i<greeting.Length;i++)greeting[i]=(byte)stream.ReadByte();if(greeting[0]!=5||greeting[1]!=1||greeting[2]!=2)throw new Exception("SOCKS auth was not mandatory");
   stream.Write(new byte[]{5,2},0,2);int version=stream.ReadByte(),ulen=stream.ReadByte();byte[] u=new byte[ulen];for(int i=0;i<ulen;i++)u[i]=(byte)stream.ReadByte();int plen=stream.ReadByte();byte[] pass=new byte[plen];for(int i=0;i<plen;i++)pass[i]=(byte)stream.ReadByte();
   if(version!=1||Encoding.UTF8.GetString(u)!="fixture@user"||Encoding.UTF8.GetString(pass)!="fixture#password")throw new Exception("SOCKS credentials incorrect");
   stream.Write(new byte[]{1,(byte)(rejectAuth?1:0)},0,2);if(rejectAuth)return;
   byte[] h=new byte[4];for(int i=0;i<4;i++)h[i]=(byte)stream.ReadByte();if(h[0]!=5||h[1]!=1||h[3]!=3)throw new Exception("SOCKS request invalid");
   int n=stream.ReadByte();byte[] host=new byte[n];for(int i=0;i<n;i++)host[i]=(byte)stream.ReadByte();observedHost=Encoding.ASCII.GetString(host);int hi=stream.ReadByte(),lo=stream.ReadByte();if(hi*256+lo!=443)throw new Exception("Wrong SOCKS port");
   byte[] answer={5,0,0,1,127,0,0,1,0,1};stream.Write(answer,0,answer.Length);byte[] data=new byte[4];for(int i=0;i<4;i++)data[i]=(byte)stream.ReadByte();stream.Write(data,0,data.Length);
  }}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try{using(var bridge=new LocalProxyBridge()){Assert(bridge.Start(0,"socks5://fixture%40user:fixture%23password@127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"SOCKS bridge failed to start");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=5000;var stream=client.GetStream();byte[] req=Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");stream.Write(req,0,req.Length);string header=Header(stream);
    Assert(header.Contains(rejectAuth?" 502 ":" 200 "),"Unexpected SOCKS bridge response");if(!rejectAuth){byte[] ping=Encoding.ASCII.GetBytes("PING");stream.Write(ping,0,ping.Length);for(int i=0;i<4;i++)Assert(stream.ReadByte()==ping[i],"SOCKS relay lost data");}
   }
   Assert(worker.Join(5000)&&failure==null,"SOCKS fixture failed: "+failure);if(!rejectAuth)Assert(observedHost=="example.test","SOCKS hostname was resolved locally");
  }}finally{server.Stop();worker.Join(1000);}
 }
 sealed class SocksFixtureStream:Stream {
  readonly MemoryStream input;public readonly MemoryStream output=new MemoryStream();public SocksFixtureStream(byte[] response){input=new MemoryStream(response);}
  public override bool CanRead{get{return true;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}
  public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
  public override int Read(byte[] b,int o,int n){return input.Read(b,o,Math.Min(n,1));}public override void Write(byte[] b,int o,int n){output.Write(b,o,n);}public override void Flush(){}
  public override long Seek(long o,SeekOrigin p){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}
 }
 static void SocksProtocolFixtures(){
  var proxy=new Uri("socks5://127.0.0.1:1080");
  foreach(byte[] response in new[]{new byte[]{4,0},new byte[]{5,255},new byte[]{5,0,5,1,0,1},new byte[]{5,0,5,0,1,1},new byte[]{5,0,5,0,0,99},new byte[]{5,0,5,0,0,1,127}}){
   bool rejected=false;try{Socks5Transport.Connect(new SocksFixtureStream(response),proxy,"example.test",443);}catch(IOException){rejected=true;}Assert(rejected,"Malformed or failed SOCKS response accepted");
  }
  var ipv6=new SocksFixtureStream(new byte[]{5,0,5,0,0,1,127,0,0,1,0,1});Socks5Transport.Connect(ipv6,proxy,"2001:db8::1",443);byte[] sent=ipv6.output.ToArray();
  Assert(sent.Length==25&&sent[6]==4&&sent[7]==0x20&&sent[8]==0x01&&sent[23]==1&&sent[24]==0xbb,"IPv6 SOCKS address encoded incorrectly");
  var downgrade=new SocksFixtureStream(new byte[]{5,0});bool denied=false;try{Socks5Transport.Connect(downgrade,new Uri("socks5://user:pass@127.0.0.1:1080"),"example.test",443);}catch(IOException){denied=true;}Assert(denied,"SOCKS authentication downgrade accepted");
 }
 static void SocksHttp() {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception failure=null;string received=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){var stream=peer.GetStream();stream.ReadTimeout=4000;
   byte[] greeting=new byte[3];for(int i=0;i<3;i++)greeting[i]=(byte)stream.ReadByte();if(greeting[0]!=5||greeting[1]!=1||greeting[2]!=0)throw new Exception("Unexpected no-auth greeting");
   stream.Write(new byte[]{5,0},0,2);byte[] h=new byte[4];for(int i=0;i<4;i++)h[i]=(byte)stream.ReadByte();if(h[0]!=5||h[1]!=1||h[3]!=3)throw new Exception("Bad request");
   int n=stream.ReadByte();byte[] host=new byte[n];for(int i=0;i<n;i++)host[i]=(byte)stream.ReadByte();int high=stream.ReadByte(),low=stream.ReadByte();if(Encoding.ASCII.GetString(host)!="example.test"||high*256+low!=8080)throw new Exception("Wrong destination");
   byte[] response={5,0,0,1,127,0,0,1,0,1};stream.Write(response,0,response.Length);received=Header(stream);byte[] ok=Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");stream.Write(ok,0,ok.Length);
  }}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try {using(var bridge=new LocalProxyBridge()){Assert(bridge.Start(0,"socks5://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"SOCKS HTTP bridge failed");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=5000;byte[] req=Encoding.ASCII.GetBytes("GET http://example.test:8080/hello?q=1 HTTP/1.1\r\nHost: example.test:8080\r\nProxy-Authorization: Basic should-not-leak\r\nConnection: close\r\n\r\n");client.GetStream().Write(req,0,req.Length);Assert(Header(client.GetStream()).Contains(" 204 "),"SOCKS HTTP request failed");}
   Assert(worker.Join(5000)&&failure==null,"SOCKS HTTP fixture failed: "+failure);Assert(received.StartsWith("GET /hello?q=1 HTTP/1.1")&&!received.Contains("should-not-leak")&&!received.Contains("Proxy-Authorization"),"SOCKS HTTP sent wrong request or leaked credentials");
  }}finally{server.Stop();worker.Join(1000);}
 }
 static int Main(){try {
  string builtInPac=SelectiveProxyRules.BuiltInPac(53200);
  Assert(builtInPac.Contains("gemini.google.com")&&builtInPac.Contains("ttvnw.net")&&!builtInPac.Contains("test-dns.dagami.ru"),"Packaged PAC rules are missing or route the gateway");
  string pac=SelectiveProxyRules.GeneratePac(new[]{"exact.example",".sub.example"},53200);
  Assert(pac.Contains("host === \"exact.example\"")&&pac.Contains("dnsDomainIs(host, \".sub.example\")")&&pac.Contains("PROXY 127.0.0.1:53200")&&pac.Contains("return \"DIRECT\""),"Selective PAC rules invalid");
  foreach(string bad in new[]{"evil.example\";alert(1)",".com/other","localhost\nreturn DIRECT"}){bool denied=false;try{SelectiveProxyRules.GeneratePac(new[]{bad},53200);}catch(InvalidDataException){denied=true;}Assert(denied,"Unsafe PAC input accepted");}
  FailedProxyMustNotConnectDirectly();
  SocksBridge(false);SocksBridge(true);SocksHttp();SocksProtocolFixtures();
  foreach(int status in new[]{200,407,403,503})Probe(status);
  MetadataHostIsExact();
  Assert(UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru:50128"),"Known DNS endpoint not identified");
  Assert(!UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru:pw@proxy.example:8080"),"Username mistaken for DNS proxy host");
  Assert(!UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru.evil.example:8080"),"DNS hostname substring matched");
  var destination=(Uri)typeof(LocalProxyBridge).GetMethod("HttpDestination",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"GET","http://example.test:8080/a?q=1","GET http://example.test:8080/a?q=1 HTTP/1.1\r\nHost: example.test\r\nProxy-Authorization: Basic secret\r\n\r\n"});
  string direct=HttpWire.RequestHeaders("GET http://example.test:8080/a?q=1 HTTP/1.1\r\nHost: example.test\r\nProxy-Authorization: Basic secret\r\n\r\n",destination,false,null);
  Assert(destination.Host=="example.test"&&destination.Port==8080&&direct.StartsWith("GET /a?q=1 HTTP/1.1")&&!direct.Contains("secret")&&!direct.Contains("Proxy-Authorization"),"Direct request leaked proxy credentials or used wrong target");
  foreach(string bad in new[]{"http://user:bad#password@127.0.0.1:1","http://127.0.0.1:1/path","http://127.0.0.1:1/?token=fixture"}) {
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
