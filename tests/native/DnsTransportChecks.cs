using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using DevProxyApp;

class DnsTransportChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static byte[] Read(Stream stream,int length){var b=new byte[length];int offset=0;while(offset<length){int n=stream.Read(b,offset,length-offset);if(n==0)throw new EndOfStreamException();offset+=n;}return b;}
 static byte[] Name(string host){using(var b=new MemoryStream()){foreach(string label in host.Split('.')){byte[] s=Encoding.ASCII.GetBytes(label);b.WriteByte((byte)s.Length);b.Write(s,0,s.Length);}b.WriteByte(0);return b.ToArray();}}
 static byte[] Answer(byte[] query,string mode) {
  byte[] question=(byte[])query.Clone();
  if(mode=="wrong-question")question[13]=(byte)'x';
  question[2]=mode=="truncated"?(byte)0x83:(byte)0x81;question[3]=0x80;question[7]=1;
  using(var output=new MemoryStream()) {
   output.Write(question,0,question.Length);
   byte[] owner=mode=="wrong-owner"?Name("other.invalid"):new byte[]{0xc0,12};output.Write(owner,0,owner.Length);
   byte[] record={0,1,0,1,0,0,0,0,0,4,8,8,4,4};
   if(mode=="cached")record[7]=30;
   if(mode=="local")record[10]=127;
   output.Write(record,0,record.Length);return output.ToArray();
  }
 }
 sealed class HttpFixture:MemoryStream {
  public volatile bool Closed;
  public int Requests;
  public string LastHeader;
  public HttpFixture(byte[] response):base(response){}
  public override int WriteTimeout{get;set;}
  public override void Write(byte[] bytes,int offset,int count) {
   if(count>=5&&Encoding.ASCII.GetString(bytes,offset,5)=="POST "){
    Requests++;
    LastHeader=Encoding.ASCII.GetString(bytes,offset,count);
   }
  }
  protected override void Dispose(bool disposing){Closed=true;base.Dispose(disposing);}
 }
 static byte[] HttpAnswer(byte[] query,string connection="keep-alive",string mode="valid",Func<string,string> rewrite=null){
  byte[] body=Answer(query,mode);
  string header="HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: "+body.Length+"\r\nConnection: "+connection+"\r\n\r\n";
  byte[] headers=Encoding.ASCII.GetBytes(rewrite==null?header:rewrite(header));
  var result=new byte[headers.Length+body.Length];Buffer.BlockCopy(headers,0,result,0,headers.Length);Buffer.BlockCopy(body,0,result,headers.Length,body.Length);return result;
 }
 static void CheckDohReuse(){
  var exchange=typeof(DnsHttpsTransport).GetMethod("ExchangeDoh",BindingFlags.Static|BindingFlags.NonPublic);
  var clear=typeof(DnsHttpsTransport).GetMethod("ClearDohPool",BindingFlags.Static|BindingFlags.NonPublic);
  var query=DnsHttpsTransport.Question("example.test");
  var response=HttpAnswer(query);
  var two=new byte[response.Length*2];Buffer.BlockCopy(response,0,two,0,response.Length);Buffer.BlockCopy(response,0,two,response.Length,response.Length);
  var first=new HttpFixture(two);var second=new HttpFixture(HttpAnswer(query,"close"));
  int connects=0;
  Func<int,Stream> connect=left=>++connects==1?first:second;
  Action<string,Func<int,Stream>> sendTo=(host,open)=>exchange.Invoke(null,new object[]{"127.0.0.1",host,443,"/dns-query",query,2000,open});
  Action send=()=>sendTo("fixture.invalid",connect);
  try {
   send();send();
   Assert(connects==1&&first.Requests==2&&!first.Closed,"DoH did not reuse a framed connection");
   send();
   Assert(connects==2&&first.Closed&&second.Closed,"A stale DoH connection was not retried or a closing response was retained");
   var invalid=new HttpFixture(HttpAnswer(query,"keep-alive","wrong-question"));
   bool rejected=false;
   try{sendTo("fixture.invalid",_=>invalid);}
   catch(TargetInvocationException error){rejected=error.InnerException is InvalidDataException;}
   Assert(rejected&&invalid.Closed,"An invalid DNS response was allowed into the connection pool");
   var owned=new HttpFixture(response);var other=new HttpFixture(response);
   sendTo("fixture.invalid",_=>owned);
   sendTo("another.invalid",_=>other);
   Assert(owned.Requests==1&&other.Requests==1,"DoH reused a connection across authorities");
   clear.Invoke(null,null);
   Assert(owned.Closed&&other.Closed,"Network change did not close idle DoH sessions");
   var oldNetwork=new HttpFixture(response);
   sendTo("fixture.invalid",_=>{clear.Invoke(null,null);return oldNetwork;});
   Assert(oldNetwork.Closed,"DoH returned an active connection to the pool after a network change");
   var closing=new HttpFixture(HttpAnswer(query,rewrite:header=>header.Replace("Connection: keep-alive","Connection: keep-alive\r\nConnection: CLOSE")));
   sendTo("fixture.invalid",_=>closing);
   Assert(closing.Closed,"DoH retained a connection with a duplicate close directive");
   var customPort=new HttpFixture(HttpAnswer(query,"close"));
   exchange.Invoke(null,new object[]{"127.0.0.1","fixture.invalid",8443,"/dns-query",query,2000,new Func<int,Stream>(_=>customPort)});
   Assert(customPort.LastHeader.Contains("\r\nHost: fixture.invalid:8443\r\n"),"DoH request omitted the non-default port from Host");
   Func<string,string>[] malformed={
    header=>header.Replace("Connection:","Connection :"),
    header=>header.Replace("Content-Length: ","Content-Length: +"),
    header=>header.Replace("OK\r\n","OK\n"),
   };
   foreach(var rewrite in malformed) {
    var fixture=new HttpFixture(HttpAnswer(query,rewrite:rewrite));
    rejected=false;
    try{sendTo("fixture.invalid",_=>fixture);}
    catch(TargetInvocationException error){rejected=error.InnerException is InvalidDataException;}
    Assert(rejected&&fixture.Closed,"DoH accepted malformed HTTP framing");
   }
   var expired=new HttpFixture(response);
   sendTo("fixture.invalid",_=>expired);
   Assert(SpinWait.SpinUntil(()=>expired.Closed,12500),"Idle DoH connection remained open without further requests");
  } finally {clear.Invoke(null,null);}
 }
 sealed class Server:IDisposable {
  readonly UdpClient udp; readonly TcpListener tcp; readonly Thread udpThread,tcpThread;
  public readonly IPEndPoint Endpoint; public int UdpQueries,TcpQueries; readonly string mode; volatile bool stopped;
  public Server(string value){
   mode=value;tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();Endpoint=(IPEndPoint)tcp.LocalEndpoint;
   udp=new UdpClient(Endpoint);udpThread=new Thread(Udp){IsBackground=true};tcpThread=new Thread(Tcp){IsBackground=true};udpThread.Start();tcpThread.Start();
  }
  void Udp(){try{while(!stopped){IPEndPoint peer=null;byte[] query=udp.Receive(ref peer);Interlocked.Increment(ref UdpQueries);if(mode=="tcp-only"||mode=="dead")continue;byte[] answer=Answer(query,mode);udp.Send(answer,answer.Length,peer);}}catch(SocketException){}catch(ObjectDisposedException){}}
  void Tcp(){try{while(!stopped)using(var client=tcp.AcceptTcpClient()){
   var stream=client.GetStream();stream.ReadTimeout=1000;byte[] prefix=Read(stream,2);byte[] query=Read(stream,(prefix[0]<<8)|prefix[1]);Interlocked.Increment(ref TcpQueries);
   if(mode=="dead")continue;byte[] answer=Answer(query,mode=="truncated"?"valid":mode);
   // Fragment the length prefix and payload to exercise real TCP framing.
   stream.WriteByte((byte)(answer.Length>>8));stream.Flush();Thread.Sleep(3);stream.WriteByte((byte)answer.Length);
   for(int offset=0;offset<answer.Length;offset+=7){int count=Math.Min(7,answer.Length-offset);stream.Write(answer,offset,count);stream.Flush();}
  }}catch(SocketException){}catch(ObjectDisposedException){}catch(IOException){}}
  public void Dispose(){stopped=true;udp.Close();tcp.Stop();Assert(udpThread.Join(2000)&&tcpThread.Join(2000),"DNS fixture did not stop");}
 }
 static ScopedDnsResolver Resolver(params Server[] servers){
  var endpoints=Array.ConvertAll(servers,s=>s.Endpoint);
  return (ScopedDnsResolver)Activator.CreateInstance(typeof(ScopedDnsResolver),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{endpoints,200},null);
 }
 static void Reject(Server server){bool rejected=false;try{Resolver(server).Resolve("www.googleapis.com");}catch(IOException){rejected=true;}Assert(rejected,"Unsafe DNS reply accepted");Assert(server.UdpQueries==1&&server.TcpQueries==1,"Failed transport attempted more than once");}
 static string Header(Stream stream){var bytes=new MemoryStream();int state=0;while(state<4){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();bytes.WriteByte((byte)b);state=(state==0||state==2)?(b==13?state+1:0):(b==10?state+1:0);if(bytes.Length>16384)throw new IOException();}return Encoding.ASCII.GetString(bytes.ToArray());}
 static void SmartDnsFailureUsesConfiguredProxy(bool connectionFailure){
  using(var dns=new Server("dead")){
   // Keep the loopback port bound but not listening, so the Smart DNS connect
   // fails locally without sending traffic to a real external server.
   Socket unavailable=null;
   if(connectionFailure){unavailable=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);unavailable.ExclusiveAddressUse=true;unavailable.Bind(new IPEndPoint(IPAddress.Parse("127.203.10.17"),443));}
   var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();Exception failure=null;string request=null;
   var worker=new Thread(()=>{try{using(var peer=upstream.AcceptTcpClient()){peer.GetStream().ReadTimeout=5000;request=Header(peer.GetStream());byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n");peer.GetStream().Write(reply,0,reply.Length);}}catch(Exception ex){failure=ex;}});worker.IsBackground=true;worker.Start();
   try{using(var bridge=new LocalProxyBridge()){
    Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)upstream.LocalEndpoint).Port),"Proxy fixture bridge failed");
    var resolver=Resolver(dns);
    if(connectionFailure){var cache=(Dictionary<string,KeyValuePair<DateTime,IPAddress>>)typeof(ScopedDnsResolver).GetField("_cache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(resolver);cache["www.googleapis.com"]=new KeyValuePair<DateTime,IPAddress>(DateTime.UtcNow.AddMinutes(1),IPAddress.Parse("127.203.10.17"));}
    typeof(LocalProxyBridge).GetField("_scopedDns",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bridge,resolver);
    using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=5000;byte[] bytes=Encoding.ASCII.GetBytes("CONNECT www.googleapis.com:443 HTTP/1.1\r\nHost: www.googleapis.com:443\r\n\r\n");client.GetStream().Write(bytes,0,bytes.Length);Assert(Header(client.GetStream()).Contains(" 200 "),"Smart DNS failure did not recover through proxy");}
    Assert(worker.Join(5000)&&failure==null&&request.StartsWith("CONNECT www.googleapis.com:443"),"Configured upstream did not receive fallback CONNECT");
   }}finally{upstream.Stop();worker.Join(1000);if(unavailable!=null)unavailable.Close();}
  }
 }
 static void CloudCodeHonorsSmartDns(string host){
  var destination=new TcpListener(IPAddress.Parse("127.203.10.19"),443);destination.Start();
  var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();Exception failure=null;
  var worker=new Thread(()=>{try{using(var peer=destination.AcceptTcpClient()){peer.GetStream().ReadTimeout=5000;Assert(peer.GetStream().ReadByte()==42,"Tunnel payload lost");peer.GetStream().WriteByte(43);}}catch(Exception ex){failure=ex;}});worker.IsBackground=true;worker.Start();
  try{using(var bridge=new LocalProxyBridge())using(var dns=new Server("dead")){
   Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)upstream.LocalEndpoint).Port),"Cloud Code fixture bridge failed");
   var resolver=Resolver(dns);
   var cache=(Dictionary<string,KeyValuePair<DateTime,IPAddress>>)typeof(ScopedDnsResolver).GetField("_cache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(resolver);
   cache[host]=new KeyValuePair<DateTime,IPAddress>(DateTime.UtcNow.AddMinutes(1),IPAddress.Parse("127.203.10.19"));
   typeof(LocalProxyBridge).GetField("_scopedDns",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bridge,resolver);
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
    var stream=client.GetStream();stream.ReadTimeout=5000;byte[] bytes=Encoding.ASCII.GetBytes("CONNECT "+host+":443 HTTP/1.1\r\nHost: "+host+":443\r\n\r\n");stream.Write(bytes,0,bytes.Length);
    Assert(Header(stream).Contains(" 200 "),"Smart DNS CONNECT failed");stream.WriteByte(42);Assert(stream.ReadByte()==43,"Smart DNS did not carry data");
   }
   Assert(worker.Join(5000)&&failure==null,"Smart DNS target did not receive connection");
   Assert(!upstream.Pending(),"Cloud Code silently ignored selected Smart DNS");
  }}finally{destination.Stop();upstream.Stop();worker.Join(1000);}
 }
 static void CloudCodeUsesProxyWithSmartDnsOff(){
  var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();Exception failure=null;string request=null;
  var worker=new Thread(()=>{try{using(var peer=upstream.AcceptTcpClient()){peer.GetStream().ReadTimeout=5000;request=Header(peer.GetStream());byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n");peer.GetStream().Write(reply,0,reply.Length);}}catch(Exception ex){failure=ex;}});worker.IsBackground=true;worker.Start();
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)upstream.LocalEndpoint).Port),"Proxy-only bridge failed");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=5000;byte[] bytes=Encoding.ASCII.GetBytes("CONNECT daily-cloudcode-pa.googleapis.com:443 HTTP/1.1\r\nHost: daily-cloudcode-pa.googleapis.com:443\r\n\r\n");client.GetStream().Write(bytes,0,bytes.Length);Assert(Header(client.GetStream()).Contains(" 200 "),"Proxy-only route failed");}
   Assert(worker.Join(5000)&&failure==null&&request.StartsWith("CONNECT daily-cloudcode-pa.googleapis.com:443"),"Smart DNS off did not use proxy");
  }}finally{upstream.Stop();worker.Join(1000);}
 }
 static int Main(){typeof(Logger).GetField("_logFilePath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(Path.GetTempPath(),"DnsTransportChecks-proxy.log"));try {
  var defaults=ScopedDnsResolver.ServerCandidates("DNS.DAGAMI.RU.");
  Assert(defaults.Length==5&&defaults[0]=="48.102.213.179"&&defaults[1]=="31.76.52.235","Dagami/MagicDNS default pool missing or out of order");
  Assert(ScopedDnsResolver.ServerCandidates("31.76.52.235")[0]=="31.76.52.235","Chosen node lost priority");
  Assert(ScopedDnsResolver.ServerCandidates("46.29.165.108")[0]=="48.102.213.179","Legacy node did not migrate to current Dagami bootstrap");
  Assert(ScopedDnsResolver.ServerCandidates("192.0.2.53").Length==1,"Custom DNS silently sent to Dagami");
  var priorityResolver=new ScopedDnsResolver("dns.dagami.ru");
  var pool=new[]{new DnsEndpoint("dagami","192.0.2.1","dns.example"),new DnsEndpoint("magicdns","192.0.2.2","dns.example")};
  var cooldown=(Dictionary<string,DateTime>)typeof(ScopedDnsResolver).GetField("_cooldown",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(priorityResolver);
  cooldown["192.0.2.1/HTTPS"]=DateTime.UtcNow.AddSeconds(30);
  var ordered=(DnsEndpoint[])typeof(ScopedDnsResolver).GetMethod("PrioritizeEndpoints",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(priorityResolver,new object[]{pool});
  Assert(ordered.Length==2&&ordered[0]==pool[1]&&ordered[1]==pool[0],"Cooldown discarded reserve DNS instead of delaying it");
  Assert(!ScopedDnsResolver.IsScoped("accounts.google.com",true)&&ScopedDnsResolver.IsScoped("accounts.google.com",false),"DNS-only login exception changed proxy-mode routing");
  using(var server=new Server("valid")){Assert(Resolver(server).Resolve("accounts.google.com").ToString()=="8.8.4.4","Resolver rejected a host selected by proxy-mode routing");}
  using(var server=new Server("cached")){var resolver=Resolver(server);Assert(resolver.Resolve("www.googleapis.com").ToString()=="8.8.4.4","UDP resolution failed");resolver.Resolve("www.googleapis.com");Assert(server.UdpQueries==1&&server.TcpQueries==0,"Valid TTL cache ignored");}
  using(var server=new Server("tcp-only")){var resolver=Resolver(server);Assert(resolver.Resolve("www.googleapis.com").ToString()=="8.8.4.4","TCP fallback failed");resolver.Resolve("oauth2.googleapis.com");Assert(server.UdpQueries==1&&server.TcpQueries==2,"Unavailable UDP not cooled down");}
  using(var server=new Server("truncated")){Assert(Resolver(server).Resolve("www.googleapis.com").ToString()=="8.8.4.4","Truncated UDP not retried over TCP");Assert(server.TcpQueries==1,"TCP was not used");}
  using(var primary=new Server("dead"))using(var backup=new Server("valid")){var resolver=Resolver(primary,backup);Assert(resolver.Resolve("www.googleapis.com").ToString()=="8.8.4.4","Backup DNS not used");resolver.Resolve("oauth2.googleapis.com");Assert(primary.UdpQueries==1&&primary.TcpQueries==1&&backup.UdpQueries==2,"Dead primary repeatedly delayed requests");}
  foreach(string mode in new[]{"wrong-question","wrong-owner","local"})using(var server=new Server(mode))Reject(server);
  SmartDnsFailureUsesConfiguredProxy(false);SmartDnsFailureUsesConfiguredProxy(true);
  CloudCodeHonorsSmartDns("cloudcode-pa.googleapis.com");CloudCodeHonorsSmartDns("daily-cloudcode-pa.googleapis.com");
  CloudCodeHonorsSmartDns("accounts.google.com");
  CloudCodeUsesProxyWithSmartDnsOff();
  Assert(LocalProxyBridge.IsCloudCodeApiHost("DAILY-CLOUDCODE-PA.GOOGLEAPIS.COM.")&&!LocalProxyBridge.IsCloudCodeApiHost("daily-cloudcode-pa.googleapis.com.evil.invalid"),"Cloud Code host matching is not exact");
  using(var server=new Server("valid")){var resolver=Resolver(server);resolver.Resolve("www.googleapis.com");resolver.Resolve("www.googleapis.com");Assert(server.UdpQueries==2,"TTL zero answer was cached");bool rejected=false;try{resolver.Resolve("www.googleapis.com.attacker.invalid");}catch(InvalidOperationException){rejected=true;}Assert(rejected&&server.UdpQueries==2,"Unscoped host queried");}
  CheckDohReuse();
  Console.WriteLine("PASS "+checks+" DNS pool, DoH reuse, UDP/TCP framing, truncation, failover/cooldown, TTL and reply validation checks (local fixtures only)");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
