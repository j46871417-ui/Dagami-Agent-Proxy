using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace DevProxyApp {
 public sealed class ProxyProbeResult {
  public bool Available {get;set;}
  public int Status {get;set;}
  public string Message {get;set;}
 }
 public static class UpstreamProxyDiagnostics {
  public static bool TryParse(string value,out Uri proxy) {
   proxy=null;
   return !string.IsNullOrWhiteSpace(value)&&Uri.TryCreate(value.Trim(),UriKind.Absolute,out proxy)&&
    (proxy.Scheme=="http"||proxy.Scheme=="https")&&!string.IsNullOrEmpty(proxy.Host)&&proxy.Port>0&&
    proxy.AbsolutePath=="/"&&string.IsNullOrEmpty(proxy.Query)&&string.IsNullOrEmpty(proxy.Fragment);
  }
  public static string StatusMessage(int status) {
   if(status>=200&&status<300)return "Прокси принял CONNECT к www.googleapis.com:443. Вход в аккаунт и ответ модели не проверены.";
   if(status==407)return "Прокси отклонил авторизацию (HTTP 407). Проверьте логин, пароль и состояние доступа у провайдера прокси.";
   if(status==403)return "Прокси запретил CONNECT к www.googleapis.com:443 (HTTP 403). Проверьте ограничения прокси; это не ответ аккаунта Google.";
   return "Прокси не выполнил CONNECT к www.googleapis.com:443 (HTTP "+status+"). Проверьте доступность и ограничения прокси; запрос к аккаунту Google не отправлялся.";
  }
  // Probe the upstream itself: Smart DNS must not mask failed proxy authentication.
  // No account tokens, OAuth codes or application requests are read or replayed.
  public static ProxyProbeResult Check(string value,int timeoutMilliseconds=8000) {
   Uri proxy;
   if(!TryParse(value,out proxy))
    return new ProxyProbeResult{Message="Укажите HTTP/HTTPS-адрес прокси без пути, query и fragment. Спецсимволы в логине и пароле должны быть URL-кодированы."};
   try {
    using(var client=new TcpClient()) {
     var pending=client.BeginConnect(proxy.Host,proxy.Port,null,null);
     using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeoutMilliseconds))throw new TimeoutException();client.EndConnect(pending);}
     Stream stream=client.GetStream();stream.ReadTimeout=timeoutMilliseconds;stream.WriteTimeout=timeoutMilliseconds;
     if(proxy.Scheme=="https") {var tls=new SslStream(stream,false);tls.AuthenticateAsClient(proxy.Host,null,SslProtocols.Tls12,true);stream=tls;}
     using(stream) {
      string request="CONNECT www.googleapis.com:443 HTTP/1.1\r\nHost: www.googleapis.com:443\r\n";
      if(!string.IsNullOrEmpty(proxy.UserInfo))request+="Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(proxy.UserInfo)))+"\r\n";
      byte[] bytes=Encoding.ASCII.GetBytes(request+"\r\n");stream.Write(bytes,0,bytes.Length);stream.Flush();
      byte[] response=new byte[16384];int length=LocalProxyBridge.ReadConnectResponse(stream,response);
      int status=LocalProxyBridge.ConnectStatus(Encoding.ASCII.GetString(response,0,length));
      return new ProxyProbeResult{Available=status>=200&&status<300,Status=status,Message=StatusMessage(status)};
     }
    }
   }catch(AuthenticationException){return new ProxyProbeResult{Message="TLS-соединение с HTTPS-прокси не прошло проверку. Проверьте адрес и сертификат; проверка сертификатов не отключалась."};}
   catch(Exception error){return new ProxyProbeResult{Message="Прокси не прошёл проверку CONNECT ("+error.GetType().Name+"). Проверьте адрес, порт и доступность сервера."};}
  }
 }
}
