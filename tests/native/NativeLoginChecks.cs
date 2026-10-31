using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Reflection;
using System.Text;
using System.Threading;
using DevProxyApp;

public static class NativeLoginChecks
{
    static int checks;
    static void Assert(bool value,string reason) { checks++;if(!value)throw new Exception(reason); }
    static void Reject(Action action) { bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Assert(rejected,"Invalid login was accepted"); }
    static byte[] Headers(Stream stream) {
        var bytes=new MemoryStream();int state=0;
        while(state<4){int v=stream.ReadByte();if(v<0)throw new EndOfStreamException();bytes.WriteByte((byte)v);state=(state==0||state==2)?(v==13?state+1:0):(v==10?state+1:0);if(bytes.Length>16384)throw new Exception("Header overflow");}
        return bytes.ToArray();
    }
    public static int Main() {
        string temp=Path.Combine(Path.GetTempPath(),"native-login-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var app=new AppTarget{Id="manual",ExePath=@"C:\Apps\Antigravity.exe"};
            string flags=NativeLogin.ElectronArguments(app,"http://127.0.0.1:53200",Path.Combine(temp,"profile"));
            Assert(!flags.Contains("user-data-dir"),"Antigravity received a second profile");
            Assert(!Directory.Exists(Path.Combine(temp,"profile")),"Antigravity profile directory created");
            Assert(!flags.Contains("new-window"),"OAuth return forced another window");
            Assert(flags.Contains("--disable-quic") && flags.Contains("[::1]"),"Proxy/loopback policy missing");
            app.ExePath=@"C:\Apps\Cursor.exe";
            Assert(!NativeLogin.ElectronArguments(app,"http://127.0.0.1:53200",Path.Combine(temp,"profile")).Contains("user-data-dir"),"Cursor lost its real settings/account profile");
            string url="https://accounts.google.com/v3/signin/accountchooser?state=PRIVATE&code_challenge=SECRET";
            Assert(NativeLogin.ValidateLoginUrl(url)==url,"Valid URL changed");
            foreach(string bad in new string[]{"http://accounts.google.com/","https://accounts.google.com.evil.invalid/","https://user:pass@accounts.google.com/","https://accounts.google.com:444/",url+"\" --no-proxy-server"}) Reject(()=>NativeLogin.ValidateLoginUrl(bad));
            string log=Path.Combine(temp,"language_server.log");File.WriteAllText(log,"prefix "+url+"\n");
            Assert(NativeLogin.ReadFreshLoginUrl(log)==url,"Fresh link missing");
            File.SetLastWriteTimeUtc(log,DateTime.UtcNow.AddMinutes(-11));Reject(()=>NativeLogin.ReadFreshLoginUrl(log));
            string browser=NativeLogin.BrowserArguments(url,53200,Path.Combine(temp,"browser"));
            Assert(browser.Contains("--proxy-server=http://127.0.0.1:53200"),"Browser used another route");
            Assert(browser.Contains("--user-data-dir="),"Browser changed user's main profile");
            var redact=typeof(Logger).GetMethod("Redact",BindingFlags.NonPublic|BindingFlags.Static);
            string safe=(string)redact.Invoke(null,new object[]{url+" http://alice:password@proxy.invalid:8080/ Authorization=TOKEN"});
            Assert(!safe.Contains("PRIVATE")&&!safe.Contains("SECRET")&&!safe.Contains("alice")&&!safe.Contains("password")&&!safe.Contains("TOKEN"),"Login secrets leaked");
            // Observe socket behavior, not just a type predicate: TLS wrapper
            // must not emit a raw FIN on its underlying TCP connection.
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            using(var sender=new TcpClient("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port))
            using(var receiver=listener.AcceptTcpClient()) {
                receiver.GetStream().ReadTimeout=100;
                using(var ssl=new SslStream(new MemoryStream())) LocalProxyBridge.CompleteWrite(ssl,sender);
                bool timeout=false;try{receiver.GetStream().ReadByte();}catch(IOException){timeout=true;}
                Assert(timeout,"TLS stream was half-closed at TCP level");
                LocalProxyBridge.CompleteWrite(new MemoryStream(),sender);
                Assert(receiver.GetStream().ReadByte()==-1,"Plain stream lost half-close");
            }listener.Stop();
            var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();Exception serverFailure=null;
            var server=new Thread(()=>{try{using(var peer=upstream.AcceptTcpClient()){Stream s=peer.GetStream();Headers(s);byte[] ok=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n");s.Write(ok,0,ok.Length);byte[] b=new byte[8192];int n;while((n=s.Read(b,0,b.Length))>0)s.Write(b,0,n);}}catch(Exception e){serverFailure=e;}});server.IsBackground=true;server.Start();
            using(var bridge=new LocalProxyBridge()) {
                Assert(bridge.Start(53400,"http://127.0.0.1:"+((IPEndPoint)upstream.LocalEndpoint).Port),"Bridge start failed");
                using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)) {
                    Stream s=client.GetStream();s.ReadTimeout=5000;byte[] req=Encoding.ASCII.GetBytes("CONNECT echo.invalid:443 HTTP/1.1\r\nHost: echo.invalid:443\r\n\r\n");s.Write(req,0,req.Length);Assert(Encoding.ASCII.GetString(Headers(s)).Contains(" 200 "),"CONNECT failed");
                    byte[] payload=new byte[262144];for(int i=0;i<payload.Length;i++)payload[i]=(byte)(i%251);
                    Exception sendFailure=null;var send=new Thread(()=>{try{s.Write(payload,0,payload.Length);client.Client.Shutdown(SocketShutdown.Send);}catch(Exception e){sendFailure=e;}});send.Start();
                    int at=0;byte[] received=new byte[payload.Length];while(at<received.Length){int n=s.Read(received,at,received.Length-at);if(n==0)break;at+=n;}
                    send.Join(5000);Assert(sendFailure==null,"Relay send failed");Assert(at==payload.Length,"Relay truncated response");
                    for(int i=0;i<payload.Length;i++)if(payload[i]!=received[i])throw new Exception("Relay corruption");checks++;
                }
            }upstream.Stop();server.Join(5000);Assert(serverFailure==null,"Upstream fixture failed");
            Console.WriteLine("PASS: "+checks+" native login, profile, redaction and real relay/socket checks");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {Directory.Delete(temp,true);}
    }
}
