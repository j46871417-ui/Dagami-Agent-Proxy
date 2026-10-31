using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace DevProxyApp {
 public sealed class UpstreamConnectException : IOException {
  public int Status {get;private set;}
  public UpstreamConnectException(int status):base("Upstream proxy refused CONNECT; HTTP status="+status){Status=status;}
 }
 // Availability of the selected bridge route, not just a separate upstream.
 public static class ConnectionDiagnostics {
  public static ProxyProbeResult Check(int bridgePort,Func<int,string> probe=null){
   if(bridgePort<=0)return new ProxyProbeResult{Message="Локальный мост не запущен."};
   try {
    string result=(probe??AntigravityDiagnostics.ProbeUserInfo)(bridgePort);
    return new ProxyProbeResult{Available=true,Status=401,Message="Проверен только маршрут userinfo; ответ модели и регион API не проверены. "+result};
   }catch(Exception error){
    return new ProxyProbeResult{Message="Проверка маршрута через Dagami не прошла ("+error.GetType().Name+"). Проверьте журнал; доступ аккаунта не проверен."};
   }
  }
 }
 public static class TlsDiagnostics {
  public static SslStream Connect(Stream stream,string host,string layer,int timeoutMilliseconds=10000,bool checkRevocation=true) {
   SslPolicyErrors policy=SslPolicyErrors.None; string chainStatus="none"; bool checkedCertificate=false;
   var tls=new SslStream(stream,false,(sender,certificate,chain,errors)=>{
    checkedCertificate=true;policy=errors;
    if(chain!=null) {
     var codes=new System.Collections.Generic.List<string>();
     foreach(var status in chain.ChainStatus)codes.Add(status.Status.ToString());
     if(codes.Count>0)chainStatus=string.Join(",",codes.ToArray());
    }
    return errors==SslPolicyErrors.None;
   });
   try {
    tls.ReadTimeout=timeoutMilliseconds;tls.WriteTimeout=timeoutMilliseconds;
    tls.AuthenticateAsClient(host,null,SslProtocols.Tls12,checkRevocation);return tls;
   } catch(AuthenticationException error) {
    tls.Dispose();
    throw new AuthenticationException("TLS host="+host+" layer="+layer+"; certificate_checked="+checkedCertificate+
     "; policy="+policy+"; chain="+chainStatus+". Проверка TLS не пройдена.",error);
   } catch {tls.Dispose();throw;}
  }
 }
 public sealed class ProxyProbeResult {
  public bool Available {get;set;}
  public int Status {get;set;}
  public string Message {get;set;}
 }
 public static class UpstreamProxyDiagnostics {
  public static bool IsDnsEndpointMistakenForProxy(string value) {
   if (string.IsNullOrWhiteSpace(value)) return false;
   Uri proxy;if(!Uri.TryCreate(value.Trim(),UriKind.Absolute,out proxy))return false;
   string host=proxy.IdnHost.TrimEnd('.').ToLowerInvariant();
   return host=="dns.dagami.ru" || host=="dns.magicdns.ru" || host=="dns.comss.one" || host=="dns.geohide.ru" || host=="dns.xbox-dns.ru" || host=="dns.malw.link";
  }

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
     if(proxy.Scheme=="https")stream=TlsDiagnostics.Connect(stream,proxy.Host,"https-proxy",timeoutMilliseconds);
     using(stream) {
      string request="CONNECT www.googleapis.com:443 HTTP/1.1\r\nHost: www.googleapis.com:443\r\n";
      if(!string.IsNullOrEmpty(proxy.UserInfo))request+="Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(proxy.UserInfo)))+"\r\n";
      byte[] bytes=Encoding.ASCII.GetBytes(request+"\r\n");stream.Write(bytes,0,bytes.Length);stream.Flush();
      byte[] response=new byte[16384];int length=LocalProxyBridge.ReadConnectResponse(stream,response);
      int status=LocalProxyBridge.ConnectStatus(Encoding.ASCII.GetString(response,0,length));
      return new ProxyProbeResult{Available=status>=200&&status<300,Status=status,Message=StatusMessage(status)};
     }
    }
   }catch(AuthenticationException error){return new ProxyProbeResult{Message=error.Message};}
   catch(Exception error){return new ProxyProbeResult{Message="Прокси не прошёл проверку CONNECT ("+error.GetType().Name+"). Проверьте адрес, порт и доступность сервера."};}
  }
 }

 public sealed class VpnCompatibilityReport {
  public bool HasActiveVpn {get;set;}
  public bool HasFakeIpDns {get;set;}
  public bool HasProxyLoop {get;set;}
  public string[] ActiveVpnAdapters {get;set;}
  public int[] LocalVpnPorts {get;set;}
  public string StatusBadge {get;set;}
  public string DetailedReport {get;set;}
 }

 public static class VpnDiagnostics {
  static readonly string[] VpnMarkers=new[]{
   "wintun","wireguard","amnezia","tap-windows","openvpn","sing-tun","nekoray","mihomo",
   "clash","outline","tailscale","zerotier","radmin","hamachi","vpn","tun","tap","warp","v2ray","xray","hiddify"
  };
  static readonly int[] KnownLocalVpnPorts=new[]{10808,10809,2080,2081,7890,7891,1080};

  public static bool IsVirtualVpnAdapter(string name,string description,NetworkInterfaceType type) {
   if(type==NetworkInterfaceType.Ppp||type==NetworkInterfaceType.Tunnel)return true;
   string combined=((name??"")+" "+(description??"")).ToLowerInvariant();
   foreach(string marker in VpnMarkers)if(combined.Contains(marker))return true;
   return false;
  }

  public static bool IsFakeIpAddress(IPAddress address) {
   if(address==null||address.AddressFamily!=AddressFamily.InterNetwork)return false;
   byte[] b=address.GetAddressBytes();
   return (b[0]==198&&(b[1]==18||b[1]==19))||(b[0]==100&&(b[1]>=64&&b[1]<=127));
  }

  public static VpnCompatibilityReport Inspect(string configuredProxy,int bridgePort,bool checkDns=true) {
   var vpnAdapters=new List<string>();
   try {
    foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
     if(nic.OperationalStatus!=OperationalStatus.Up||nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
     if(IsVirtualVpnAdapter(nic.Name,nic.Description,nic.NetworkInterfaceType))
      vpnAdapters.Add(nic.Name+(string.IsNullOrEmpty(nic.Description)?"":" ("+nic.Description+")"));
    }
   } catch {}

   var localPorts=new List<int>();
   try {
    var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
    foreach(int port in KnownLocalVpnPorts) {
     if(port==bridgePort)continue;
     foreach(var ep in listeners)if(ep.Port==port){if(!localPorts.Contains(port))localPorts.Add(port);break;}
    }
   } catch {}

   bool loop=false;
   Uri parsed;
   if(UpstreamProxyDiagnostics.TryParse(configuredProxy,out parsed)&&bridgePort>0&&LocalProxyBridge.IsBridgeEndpoint(parsed,bridgePort))
    loop=true;

   bool fakeIp=false;
   string sampleResolved=null;
   if(checkDns) {
    try {
     var addresses=Dns.GetHostAddresses("cloudcode-pa.googleapis.com");
     foreach(var addr in addresses) {
      if(sampleResolved==null)sampleResolved=addr.ToString();
      if(IsFakeIpAddress(addr)){fakeIp=true;sampleResolved=addr.ToString();break;}
     }
    } catch {}
   }

   bool hasVpn=vpnAdapters.Count>0||localPorts.Count>0;
   string badge=loop?"VPN/Прокси: петля портов":
    fakeIp?"VPN активен (Fake-IP обходится через DoH)":
    hasVpn?"VPN: совместим ("+(vpnAdapters.Count>0?vpnAdapters[0].Split('(')[0].Trim():"порт "+localPorts[0])+")":
    "VPN: не обнаружен (прямой режим)";

   var report=new StringBuilder();
   report.AppendLine("[Проверка совместимости с VPN и внешними прокси]");
   report.AppendLine("Активные TUN/VPN-интерфейсы: "+(vpnAdapters.Count==0?"не обнаружены":string.Join(", ",vpnAdapters.ToArray())));
   report.AppendLine("Локальные порты VPN-клиентов (v2rayN/Nekoray/Clash): "+(localPorts.Count==0?"не обнаружены":string.Join(", ",localPorts.ConvertAll(p=>p.ToString()).ToArray())));
   if(loop)report.AppendLine("ВНИМАНИЕ: Адрес прокси указывает на собственный локальный мост Dagami (порт "+bridgePort+"). Измените порт прокси.");
   if(fakeIp)report.AppendLine("Обнаружен перехват системного DNS режимом Fake-IP ("+sampleResolved+"). Шифрованный DNS Dagami (DoH/DoT) обходит Fake-IP и возвращает реальные адреса Google/AI.");
   else if(sampleResolved!=null)report.AppendLine("Системный DNS для cloudcode-pa.googleapis.com: "+sampleResolved+" (без Fake-IP).");
   report.AppendLine("Защита адаптеров: виртуальные TUN-интерфейсы VPN исключены из автовыбора при смене DNS всей сети.");

   return new VpnCompatibilityReport {
    HasActiveVpn=hasVpn,
    HasFakeIpDns=fakeIp,
    HasProxyLoop=loop,
    ActiveVpnAdapters=vpnAdapters.ToArray(),
    LocalVpnPorts=localPorts.ToArray(),
    StatusBadge=badge,
    DetailedReport=report.ToString().TrimEnd()
   };
  }
 }
}
