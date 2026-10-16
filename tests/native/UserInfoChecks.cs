using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using DevProxyApp;
class UserInfoChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static string Describe(string path,string status,bool token){
  try{return (string)typeof(AntigravityDiagnostics).GetMethod("DescribeHttpResult",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{"www.googleapis.com",path,status,token});}
  catch(TargetInvocationException ex){throw ex.InnerException;}
 }
 static void Rejected(string path,string status,bool token){
  bool rejected=false;try{Describe(path,status,token);}catch(IOException){rejected=true;}catch(InvalidDataException){rejected=true;}
  Assert(rejected,"Bad response accepted: "+status);
 }
 sealed class RefusingProxy : IDisposable {
  readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
  readonly Thread thread;
  public int Port;
  public int Requests;
  public string LastRequest;
  readonly string status;
  public RefusingProxy(string value){status=value;listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;thread=new Thread(Run){IsBackground=true};thread.Start();}
  void Run(){try{while(true)using(var client=listener.AcceptTcpClient()){
   var stream=client.GetStream();stream.ReadTimeout=3000;
   var header=new StringBuilder();while(header.Length<16384){int b=stream.ReadByte();if(b<0)break;header.Append((char)b);if(header.ToString().EndsWith("\r\n\r\n"))break;}
   LastRequest=header.ToString();Interlocked.Increment(ref Requests);
   byte[] response=Encoding.ASCII.GetBytes(status+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");stream.Write(response,0,response.Length);
  }}catch(SocketException){}catch(ObjectDisposedException){}}
  public void Dispose(){listener.Stop();Assert(thread.Join(4000),"Fixture did not stop");}
 }
 static int Main(){try{
  Assert(ScopedDnsResolver.IsScoped("www.googleapis.com"),"userinfo host missing from DNS route");
  Assert(Describe("/oauth2/v2/userinfo","HTTP/1.1 401 Unauthorized",false).Contains("ожидаем без токена"),"401 not identified as synthetic probe");
  Assert(Describe("/token","HTTP/1.1 400 Bad Request",true).Contains("вход не выполнен"),"OAuth 400 not explained");
  foreach(int code in new[]{407,429,500,502,503,504})Rejected("/oauth2/v2/userinfo","HTTP/1.1 "+code+" Failure",false);
  foreach(int code in new[]{200,302,403,404})Rejected("/oauth2/v2/userinfo","HTTP/1.1 "+code+" Unexpected",false);
  Rejected("/token","HTTP/1.1 502 Bad Gateway",true);
  Rejected("/token","HTTP/1.1 200 OK",true);
  Rejected("/","HTTP/1.1 502 Bad Gateway",false);
  Rejected("/oauth2/v2/userinfo","HTTP/1.1 2000 Malformed",false);
  using(var proxy=new RefusingProxy("HTTP/1.1 502 Bad Gateway"))using(var bridge=new LocalProxyBridge()){
   string uri="http://127.0.0.1:"+proxy.Port;
   Assert(bridge.Start(0,uri),"Bridge did not start");
   string report=AntigravityDiagnostics.LoginRouteReport(bridge.BoundPort,true,"unused.invalid",uri);
   Assert(report.Contains("ОШИБКА userinfo"),"Primary failure hidden");
   Assert(report.Contains("ОШИБКА альтернативного маршрута"),"Comparison failure hidden");
   Assert(report.Contains("FileVersion="+System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(AntigravityDiagnostics).Assembly.Location).FileVersion),"Build cannot be identified");
   Assert(proxy.Requests==2,"Probe replayed or comparison skipped");
   Assert(proxy.LastRequest.StartsWith("CONNECT www.googleapis.com:443 HTTP/1.1"),"Wrong host probed");
   Assert(!proxy.LastRequest.Contains("Bearer") && !proxy.LastRequest.Contains("code="),"Account material in diagnostic request");
   Assert(!report.Contains("userinfo доступен"),"Broken route reported healthy");
   Assert(bridge.BoundPort>0,"Running bridge was replaced");
  }
  using(var proxy=new RefusingProxy("HTTP/1.1 2000 Malformed"))using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"http://127.0.0.1:"+proxy.Port),"Malformed fixture bridge failed");
   bool failed=false;try{AntigravityDiagnostics.ProbeUserInfo(bridge.BoundPort);}catch(IOException){failed=true;}
   Assert(failed,"Malformed CONNECT accepted");
  }
  Console.WriteLine("PASS "+checks+" userinfo response classification, real proxy failures, route comparison and no authenticated replay checks");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
