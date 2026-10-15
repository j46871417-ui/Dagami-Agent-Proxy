using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using DevProxyApp;

class NativeEofChecks {
 static int checks;
 static void Assert(bool b,string message) { checks++; if(!b)throw new Exception(message); }
 static string Headers(Stream s) {
  var b=new StringBuilder();int x;
  while((x=s.ReadByte())>=0) {b.Append((char)x);if(b.ToString().EndsWith("\r\n\r\n"))return b.ToString();if(b.Length>20000)throw new Exception("overflow");}
  throw new EndOfStreamException();
 }
 static void Fixture(string behavior,int expectedConnections,string expectedStatus) {
  var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int connections=0,applicationBytes=0;Exception failure=null;
  var thread=new Thread(()=>{
   try {
    for(int i=0;i<expectedConnections;i++) {
     using(var peer=listener.AcceptTcpClient()) {
      Interlocked.Increment(ref connections); var stream=peer.GetStream(); stream.ReadTimeout=4000;
      Headers(stream);
      if(behavior=="recover" && i<2)continue;
      if(behavior=="empty")continue;
      string reply=behavior=="deny"?"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n":
       behavior=="malformed"?"HTTP/1.1 2000 not-a-status\r\n\r\n":"HTTP/1.1 200 OK\r\n\r\n";
      byte[] bytes=Encoding.ASCII.GetBytes(reply);stream.Write(bytes,0,bytes.Length);
      if(behavior=="recover") {
       applicationBytes=stream.ReadByte(); stream.WriteByte(42);
       Assert(stream.ReadByte()==-1,"Client was not half-closed");
      }
     }
    }
   }catch(Exception e){failure=e;}
  });thread.IsBackground=true;thread.Start();
  using(var bridge=new LocalProxyBridge()) {
   Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port),"Bridge not started");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)) {
    Stream s=client.GetStream();s.ReadTimeout=6000;
    byte[] req=Encoding.ASCII.GetBytes("CONNECT oauth2.googleapis.com:443 HTTP/1.1\r\nHost: oauth2.googleapis.com:443\r\n\r\n");
    s.Write(req,0,req.Length);
    // Optimistic client data must be held until a successful CONNECT, never
    // copied to the failed connections. The upstream sees it exactly once.
    if(behavior=="recover")s.WriteByte(17);
    Assert(Headers(s).Contains(" "+expectedStatus+" "),"Wrong downstream CONNECT status: "+behavior);
    if(behavior=="recover") {Assert(s.ReadByte()==42,"Relay lost response after retry");client.Client.Shutdown(SocketShutdown.Send);Assert(s.ReadByte()==-1,"Relay did not finish");}
   }
   Assert(thread.Join(5000),"Fixture hung");
   Assert(connections==expectedConnections,"Unexpected retries: "+behavior);
   Assert(failure==null,"Fixture failed: "+failure);
   if(behavior=="recover")Assert(applicationBytes==17,"Prefetched application data replayed/lost");
   Assert(!listener.Pending(),"Extra connection after final status");
  }listener.Stop();
 }
 public static int Main() {
  try {
   Logger.OnLog=Console.WriteLine;
   Fixture("recover",3,"200");Fixture("empty",3,"502");Fixture("deny",1,"502");Fixture("malformed",1,"502");
   Assert(LocalProxyBridge.ConnectStatus("HTTP/1.0 200 OK\r\n\r\n")==200,"HTTP/1.0 rejected");
   bool rejected=false;try{LocalProxyBridge.ConnectStatus("HTTP/1.1 2000 Evil\r\n\r\n");}catch(InvalidDataException){rejected=true;}
   Assert(rejected,"Invalid 200 prefix accepted");
   rejected=false;try{LocalProxyBridge.SafeAuthority("alice:secret@oauth2.googleapis.com:443");}catch(InvalidDataException){rejected=true;}
   Assert(rejected,"Credentials in CONNECT authority accepted");
   rejected=false;try{LocalProxyBridge.ReadConnectResponse(new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n")),new byte[1024]);}catch(EndOfStreamException){rejected=true;}
   Assert(rejected,"Partial response accepted");
   string name=Process.GetCurrentProcess().ProcessName;
   rejected=false;try{NativeLogin.RequireStopped(name+".exe");}catch(InvalidOperationException){rejected=true;}
   Assert(rejected,"Existing process ignored");
   Console.WriteLine("PASS "+checks+" EOF recovery, no replay, error reporting and existing-session checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
 }
}
