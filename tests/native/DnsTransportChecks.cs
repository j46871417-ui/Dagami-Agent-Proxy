using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
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
 static int Main(){typeof(Logger).GetField("_logFilePath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(Path.GetTempPath(),"DnsTransportChecks-proxy.log"));try {
  Assert(ScopedDnsResolver.ServerCandidates("DNS.DAGAMI.RU.").Length==6,"Dagami/MagicDNS default pool missing");
  Assert(ScopedDnsResolver.ServerCandidates("46.29.165.108")[0]=="46.29.165.108","Chosen node lost priority");
  Assert(ScopedDnsResolver.ServerCandidates("192.0.2.53").Length==1,"Custom DNS silently sent to Dagami");
  using(var server=new Server("cached")){var resolver=Resolver(server);Assert(resolver.Resolve("www.googleapis.com").ToString()=="8.8.4.4","UDP resolution failed");resolver.Resolve("www.googleapis.com");Assert(server.UdpQueries==1&&server.TcpQueries==0,"Valid TTL cache ignored");}
  using(var server=new Server("tcp-only")){var resolver=Resolver(server);Assert(resolver.Resolve("www.googleapis.com").ToString()=="8.8.4.4","TCP fallback failed");resolver.Resolve("oauth2.googleapis.com");Assert(server.UdpQueries==1&&server.TcpQueries==2,"Unavailable UDP not cooled down");}
  using(var server=new Server("truncated")){Assert(Resolver(server).Resolve("www.googleapis.com").ToString()=="8.8.4.4","Truncated UDP not retried over TCP");Assert(server.TcpQueries==1,"TCP was not used");}
  using(var primary=new Server("dead"))using(var backup=new Server("valid")){var resolver=Resolver(primary,backup);Assert(resolver.Resolve("www.googleapis.com").ToString()=="8.8.4.4","Backup DNS not used");resolver.Resolve("oauth2.googleapis.com");Assert(primary.UdpQueries==1&&primary.TcpQueries==1&&backup.UdpQueries==2,"Dead primary repeatedly delayed requests");}
  foreach(string mode in new[]{"wrong-question","wrong-owner","local"})using(var server=new Server(mode))Reject(server);
  using(var server=new Server("valid")){var resolver=Resolver(server);resolver.Resolve("www.googleapis.com");resolver.Resolve("www.googleapis.com");Assert(server.UdpQueries==2,"TTL zero answer was cached");bool rejected=false;try{resolver.Resolve("www.googleapis.com.attacker.invalid");}catch(InvalidOperationException){rejected=true;}Assert(rejected&&server.UdpQueries==2,"Unscoped host queried");}
  Console.WriteLine("PASS "+checks+" DNS pool, UDP/TCP framing, truncation, failover/cooldown, TTL and reply validation checks (loopback only)");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
