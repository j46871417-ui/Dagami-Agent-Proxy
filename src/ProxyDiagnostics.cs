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
  public static ProxyProbeResult CheckService(int bridgePort,string service) {
   if(service=="Antigravity")return Check(bridgePort);
   if(bridgePort<=0)return new ProxyProbeResult{Message="Локальный мост не запущен."};
   try {
    return new ProxyProbeResult{Available=true,Message=AntigravityDiagnostics.ProbeService(bridgePort,service)};
   }catch(Exception error) {
    return new ProxyProbeResult{Message="Проверка "+service+" через Dagami не прошла: "+error.Message};
   }
  }
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
 public static class Socks5Transport {
  static void ReadExact(Stream stream,byte[] data,int count) {
   for(int pos=0;pos<count;) {int n=stream.Read(data,pos,count-pos);if(n<=0)throw new EndOfStreamException();pos+=n;}
  }
  public static void Connect(Stream stream,Uri proxy,string host,int port) {
   if(stream==null||proxy==null||string.IsNullOrEmpty(host)||port<1||port>65535)throw new ArgumentException("Invalid SOCKS destination");
   bool auth=!string.IsNullOrEmpty(proxy.UserInfo);
   byte[] greeting=auth?new byte[]{5,1,2}:new byte[]{5,1,0};stream.Write(greeting,0,greeting.Length);
   byte[] reply=new byte[2];ReadExact(stream,reply,2);
   if(reply[0]!=5||reply[1]!=(auth?2:0))throw new IOException("SOCKS authentication method rejected");
   if(auth) {
    string[] parts=proxy.UserInfo.Split(new[]{':'},2);byte[] user=Encoding.UTF8.GetBytes(Uri.UnescapeDataString(parts[0]));
    byte[] password=Encoding.UTF8.GetBytes(parts.Length==2?Uri.UnescapeDataString(parts[1]):"");
    if(user.Length==0||user.Length>255||password.Length==0||password.Length>255)throw new IOException("Invalid SOCKS credentials length");
    byte[] credentials=new byte[3+user.Length+password.Length];credentials[0]=1;credentials[1]=(byte)user.Length;
    Buffer.BlockCopy(user,0,credentials,2,user.Length);credentials[2+user.Length]=(byte)password.Length;Buffer.BlockCopy(password,0,credentials,3+user.Length,password.Length);
    stream.Write(credentials,0,credentials.Length);Array.Clear(credentials,0,credentials.Length);Array.Clear(password,0,password.Length);
    ReadExact(stream,reply,2);if(reply[0]!=1||reply[1]!=0)throw new IOException("SOCKS authentication rejected");
   }
   IPAddress address;byte[] target;byte kind;
   if(IPAddress.TryParse(host.Trim('[',']'),out address)){target=address.GetAddressBytes();kind=target.Length==4?(byte)1:(byte)4;}
   else {host=new System.Globalization.IdnMapping().GetAscii(host);target=Encoding.ASCII.GetBytes(host);if(target.Length==0||target.Length>255)throw new IOException("Invalid SOCKS hostname");kind=3;}
   using(var request=new MemoryStream()) {
    request.Write(new byte[]{5,1,0,kind},0,4);if(kind==3)request.WriteByte((byte)target.Length);request.Write(target,0,target.Length);request.WriteByte((byte)(port>>8));request.WriteByte((byte)port);
    byte[] bytes=request.ToArray();stream.Write(bytes,0,bytes.Length);
   }
   byte[] header=new byte[4];ReadExact(stream,header,4);if(header[0]!=5||header[1]!=0||header[2]!=0)throw new IOException("SOCKS CONNECT rejected");
   int length;if(header[3]==1)length=4;else if(header[3]==4)length=16;else if(header[3]==3){int n=stream.ReadByte();if(n<1)throw new IOException("Invalid SOCKS reply");length=n;}else throw new IOException("Invalid SOCKS address type");
   ReadExact(stream,new byte[length+2],length+2);
  }
 }
 internal static class PublicProxyCandidates {
  const string Source = "https://raw.githubusercontent.com/ProxyScrape/free-proxy-list/main/proxies/protocols/https/data.txt";
  const string Mirror = "https://cdn.jsdelivr.net/gh/ProxyScrape/free-proxy-list@main/proxies/protocols/https/data.txt";
  const string GeminiHost = "gemini.google.com";
  static readonly string[] OtherHosts = {"github.com","chatgpt.com"};

  static bool IsPublicIpv4(IPAddress address) {
   if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
   byte[] b=address.GetAddressBytes();
   return b[0]!=0 && b[0]!=10 && b[0]!=127 && b[0]<224 &&
     !(b[0]==100 && b[1]>=64 && b[1]<=127) &&
     !(b[0]==169 && b[1]==254) && !(b[0]==172 && b[1]>=16 && b[1]<=31) &&
     !(b[0]==192 && (b[1]==168 || (b[1]==0 && (b[2]==0 || b[2]==2)) ||
        (b[1]==88 && b[2]==99))) &&
     !(b[0]==198 && (b[1]==18 || b[1]==19 || (b[1]==51 && b[2]==100))) &&
     !(b[0]==203 && b[1]==0 && b[2]==113);
  }

  static bool Probe(Uri candidate,string host) {
   try {
    using(var tcp=new TcpClient(AddressFamily.InterNetwork))
    using(var deadline=new System.Threading.Timer(_=>tcp.Close(),null,8000,System.Threading.Timeout.Infinite)) {
     var pending=tcp.BeginConnect(IPAddress.Parse(candidate.Host),candidate.Port,null,null);
     using(pending.AsyncWaitHandle) {
      if(!pending.AsyncWaitHandle.WaitOne(2500))return false;
      tcp.EndConnect(pending);
     }
     using(var stream=tcp.GetStream()) {
      stream.ReadTimeout=2500;
      stream.WriteTimeout=2500;
      byte[] request=Encoding.ASCII.GetBytes("CONNECT "+host+":443 HTTP/1.1\r\nHost: "+host+":443\r\n\r\n");
      stream.Write(request,0,request.Length);
      byte[] reply=new byte[4096];
      int length=LocalProxyBridge.ReadConnectResponse(stream,reply);
      if(LocalProxyBridge.ConnectStatus(Encoding.ASCII.GetString(reply,0,length))!=200)return false;
      using(var tls=TlsDiagnostics.Connect(stream,host,"public-proxy",2500))return tls.IsAuthenticated;
     }
    }
   } catch(IOException) {return false;}
   catch(SocketException) {return false;}
   catch(AuthenticationException) {return false;}
   catch(InvalidDataException) {return false;}
   catch(ObjectDisposedException) {return false;}
  }

  static List<Uri> Download(string source) {
   var request=(HttpWebRequest)WebRequest.Create(source);
   request.AllowAutoRedirect=false;
   request.Timeout=7000;
   request.ReadWriteTimeout=3000;
   request.Proxy=null;
   var candidates=new List<Uri>();
   using(var response=(HttpWebResponse)request.GetResponse())
   using(var input=response.GetResponseStream())
   using(var body=new MemoryStream()) {
    if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("ProxyScrape не вернул список.");
    byte[] buffer=new byte[4096];
    int read;
    var watch=System.Diagnostics.Stopwatch.StartNew();
    while((read=input.Read(buffer,0,buffer.Length))>0) {
     if(body.Length+read>131072||watch.ElapsedMilliseconds>7000)throw new IOException("Превышен лимит загрузки списка.");
     body.Write(buffer,0,read);
    }
    var seen=new HashSet<string>();
    foreach(string line in Encoding.ASCII.GetString(body.ToArray()).Split('\n')) {
     string value=line.Trim();
     Uri uri;
     IPAddress ip;
     if(!UpstreamProxyDiagnostics.TryParse(value,out uri)||uri.Scheme!="http"||uri.UserInfo.Length!=0||
         !IPAddress.TryParse(uri.Host,out ip)||!IsPublicIpv4(ip)||!seen.Add(uri.Authority))continue;
     candidates.Add(uri);
     if(candidates.Count==12)break;
    }
   }
   if(candidates.Count==0)throw new InvalidDataException("В списке нет допустимых адресов.");
   return candidates;
  }

  public static KeyValuePair<string,string> Find(bool requireGemini) {
   List<Uri> candidates=null;
   foreach(string source in new[]{Source,Mirror}) {
    try {candidates=Download(source);break;}
    catch(WebException error) {Logger.Log("[Public proxy] "+new Uri(source).Host+" download: "+error.Status);}
    catch(IOException error) {Logger.Log("[Public proxy] "+new Uri(source).Host+" download: "+error.GetType().Name);}
    catch(InvalidDataException) {Logger.Log("[Public proxy] "+new Uri(source).Host+" returned no valid addresses.");}
   }
   if(candidates==null)throw new IOException("Список ProxyScrape недоступен через GitHub и jsDelivr.");
   Tuple<string,string> found=null;
   string[] hosts=requireGemini?new[]{GeminiHost}:OtherHosts;
   System.Threading.Tasks.Parallel.ForEach(candidates,new System.Threading.Tasks.ParallelOptions{MaxDegreeOfParallelism=4},(candidate,state)=>{
    foreach(string host in hosts) {
     if(state.IsStopped)return;
     if(!Probe(candidate,host))continue;
     System.Threading.Interlocked.CompareExchange(ref found,
         Tuple.Create(candidate.GetLeftPart(UriPartial.Authority),host),null);
     state.Stop();
     return;
    }
   });
   return found==null?default(KeyValuePair<string,string>):new KeyValuePair<string,string>(found.Item1,found.Item2);
  }
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
    (proxy.Scheme=="http"||proxy.Scheme=="https"||proxy.Scheme=="socks5"||proxy.Scheme=="socks5h")&&!string.IsNullOrEmpty(proxy.Host)&&proxy.Port>0&&
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
    return new ProxyProbeResult{Message="Укажите HTTP/HTTPS/SOCKS5-адрес прокси без пути, query и fragment. Спецсимволы в логине и пароле должны быть URL-кодированы."};
   try {
    using(var client=new TcpClient()) {
     var pending=client.BeginConnect(proxy.Host,proxy.Port,null,null);
     using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeoutMilliseconds))throw new TimeoutException();client.EndConnect(pending);}
     Stream stream=client.GetStream();stream.ReadTimeout=timeoutMilliseconds;stream.WriteTimeout=timeoutMilliseconds;
     if(proxy.Scheme=="https")stream=TlsDiagnostics.Connect(stream,proxy.Host,"https-proxy",timeoutMilliseconds);
     if(proxy.Scheme=="socks5"||proxy.Scheme=="socks5h") { using(stream){Socks5Transport.Connect(stream,proxy,"www.googleapis.com",443);return new ProxyProbeResult{Available=true,Status=200,Message="SOCKS5 принял соединение к www.googleapis.com:443. TLS, вход и ответ модели не проверены."};} }
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
