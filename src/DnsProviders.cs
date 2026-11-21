using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace DevProxyApp {
 public sealed class DnsEndpoint {
  public string Provider {get;private set;}
  public string Address {get;private set;}
  public string Host {get;private set;}
  public string Path {get;private set;}
  public string Protocol {get;private set;}
  public int Port {get;private set;}
  public DnsEndpoint(string provider,string address,string host):this(provider,address,host,"/dns-query","HTTPS",443){}
  public DnsEndpoint(string provider,string address,string host,string path,string protocol,int port){
   Provider=provider;Address=address;Host=host;
   Path=string.IsNullOrEmpty(path)?"/dns-query":path;
   Protocol=string.IsNullOrEmpty(protocol)?"HTTPS":protocol.ToUpperInvariant();
   Port=port>0?port:(Protocol=="TLS"?853:(Protocol=="UDP"||Protocol=="TCP"?53:443));
  }
 }
 public static class DnsProviders {
  static readonly object Sync=new object();
  static readonly string[] ProviderIds={"dagami","magicdns"};
  static readonly string[] NamedProviderIds={"comss","geohide","malw","xboxdns"};
  const string DagamiDohIp="31.76.52.235";
  const string DagamiPublicDohIp="48.102.213.179";
  const string RetiredDagamiIp="31.76.61.24";
  internal const string MagicDnsHost="api.magicinfo.art";
  const string MagicDnsPath="/rpc";
  internal const string MagicDnsAuthority="https://"+MagicDnsHost+MagicDnsPath;
  static readonly string[] MagicDnsSeeds={"217.144.190.216","109.120.187.249"};
  static readonly Dictionary<string,string> Hosts=new Dictionary<string,string>{
   {"dagami","doh.dagami.ru"},
   {"magicdns",MagicDnsHost},
   {"comss","dns.comss.one"},
   {"geohide","dns.geohide.ru"},
   {"malw","dns.malw.link"},
   {"xboxdns","dns.xbox-dns.ru"}
  };
  static readonly Dictionary<string,Uri> Profiles=new Dictionary<string,Uri>{
   {"dagami",new Uri("https://dns.dagami.ru/dagami.mobileconfig")},
   {"magicdns",new Uri("https://"+MagicDnsHost+"/profile.mobileconfig")}
  };
  static List<DnsEndpoint> endpoints=Seeds();
  static List<DnsEndpoint> namedEndpoints=new List<DnsEndpoint>();
  static readonly Dictionary<string,string> probeStatus=new Dictionary<string,string>();
  static readonly Dictionary<string,int> probeLatencyMs=new Dictionary<string,int>();
  static string cachePath;static Timer timer;static int refreshing;
  static DateTime lastCheckedUtc;static string lastError;static string lastProbeSummary;
  static int probeGeneration;
  public static DateTime LastCheckedUtc {get{lock(Sync)return lastCheckedUtc;}}
  public static string LastError {get{lock(Sync)return lastError;}}
  public static string LastProbeSummary {get{lock(Sync)return lastProbeSummary;}}
  internal static string CataloguePath {get{lock(Sync)return cachePath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"devproxy","dns-endpoints.json");}}
  internal const string ProbeDomain="cloudcode-pa.googleapis.com";

  static List<DnsEndpoint> Seeds(){
   var result=new List<DnsEndpoint>();
   result.Add(ProviderEndpoint("dagami",DagamiPublicDohIp));
   result.Add(ProviderEndpoint("dagami",DagamiDohIp));
   foreach(string address in MagicDnsSeeds)result.Add(ProviderEndpoint("magicdns",address));
   return result;
  }

  static DnsEndpoint ProviderEndpoint(string provider,string address){
   string host=provider=="dagami"&&address==DagamiPublicDohIp?"dns.dagami.ru":Hosts[provider];
   return new DnsEndpoint(provider,address,host,provider=="magicdns"?MagicDnsPath:"/dns-query","HTTPS",443);
  }

  static void IncludeDagamiSeeds(List<string> addresses){
   addresses.Remove(RetiredDagamiIp);
   addresses.Remove(DagamiPublicDohIp);
   addresses.Insert(0,DagamiPublicDohIp);
   if(!addresses.Contains(DagamiDohIp))addresses.Add(DagamiDohIp);
   for(int index=addresses.Count-1;addresses.Count>8&&index>=0;index--)
    if(addresses[index]!=DagamiDohIp&&addresses[index]!=DagamiPublicDohIp)addresses.RemoveAt(index);
  }

  public static DnsEndpoint[] BuiltInExtraSeeds(){
   var result=new List<DnsEndpoint>();
   foreach(string address in new[]{"195.133.25.16","212.109.195.93","185.146.157.78","212.109.221.20"})
    result.Add(new DnsEndpoint("comss",address,Hosts["comss"],"/dns-query","HTTPS",443));
   foreach(string address in new[]{"159.194.200.33","193.233.112.67","193.233.112.68","193.233.112.88","37.230.192.51"})
    result.Add(new DnsEndpoint("geohide",address,Hosts["geohide"],"/dns-query","HTTPS",443));
   foreach(string address in new[]{"164.5.224.81","179.254.166.171"})
    result.Add(new DnsEndpoint("malw",address,Hosts["malw"],"","TLS",853));
   foreach(string address in new[]{"111.88.96.56","111.88.96.57"})
    result.Add(new DnsEndpoint("xboxdns",address,Hosts["xboxdns"],"/dns-query","HTTPS",443));
   return result.ToArray();
  }

  public static DnsEndpoint[] AllBuiltInEndpoints(){
   var list=new List<DnsEndpoint>(Snapshot());
   var seeds=BuiltInExtraSeeds();
   DnsEndpoint[] learned;lock(Sync)learned=namedEndpoints.ToArray();
   foreach(string provider in NamedProviderIds){
    var verified=Array.FindAll(learned,x=>x.Provider==provider);
    list.AddRange(verified.Length>0?verified:Array.FindAll(seeds,x=>x.Provider==provider));
   }
   foreach(var provider in DnsCatalogue.Options())list.AddRange(provider.Endpoints());
   return list.ToArray();
  }

  static DnsEndpoint NamedEndpoint(string provider,string address){
   return new DnsEndpoint(provider,address,Hosts[provider],"/dns-query",provider=="malw"?"TLS":"HTTPS",provider=="malw"?853:443);
  }

  static IEnumerable<string> LookupNamedAddresses(string host){
   var work=Dns.GetHostAddressesAsync(host);
   if(!work.Wait(2500))return new string[0];
   return Array.ConvertAll(work.Result,address=>address.ToString());
  }

  static bool VerifyEndpoint(DnsEndpoint endpoint){
   int ttl;
   byte[] query=DnsHttpsTransport.Question("example.com");
   ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query,2500),new byte[]{0,0},"example.com",out ttl);
   return true;
  }

  internal static void RefreshNamedEndpoints(Func<string,IEnumerable<string>> lookup,Func<DnsEndpoint,bool> verify){
   System.Threading.Tasks.Parallel.ForEach(NamedProviderIds,new System.Threading.Tasks.ParallelOptions{MaxDegreeOfParallelism=4},provider=>{
    try{
     DnsEndpoint[] cached;
     lock(Sync)cached=namedEndpoints.FindAll(x=>x.Provider==provider).ToArray();
     var addresses=new List<string>();
     foreach(string address in lookup(Hosts[provider])){
      if(!PublicAddress(address))continue;
      string canonical=IPAddress.Parse(address).ToString();
      if(!addresses.Contains(canonical))addresses.Add(canonical);
      if(addresses.Count==8)break;
     }
     foreach(var current in cached)if(addresses.Count<8&&!addresses.Contains(current.Address))addresses.Add(current.Address);
     foreach(var seed in BuiltInExtraSeeds())if(seed.Provider==provider&&addresses.Count<8&&!addresses.Contains(seed.Address))addresses.Add(seed.Address);
     var healthy=new List<DnsEndpoint>();
     foreach(string address in addresses){
      var candidate=NamedEndpoint(provider,address);
      try{if(verify(candidate))healthy.Add(candidate);}catch(Exception error){Logger.Trace("[DNS catalogue] "+provider+" "+address+" "+error.GetType().Name);}
     }
     if(healthy.Count==0)return;
     lock(Sync){namedEndpoints.RemoveAll(x=>x.Provider==provider);namedEndpoints.AddRange(healthy);}
    }catch(Exception error){Logger.Trace("[DNS catalogue] "+provider+" lookup: "+error.GetType().Name);}
   });
  }

  public static bool PublicAddress(string text){IPAddress ip;if(!IPAddress.TryParse(text,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;byte[] b=ip.GetAddressBytes();return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127);}
  public static DnsEndpoint[] Snapshot(){lock(Sync)return endpoints.ToArray();}

  internal static string MatchBuiltInProvider(string value){
   if(string.IsNullOrEmpty(value))return null;
   string lower=value.ToLowerInvariant();
   if(lower=="dns.dagami.ru"||lower=="doh.dagami.ru"||lower=="dagami.ru"||lower=="dagami"||lower=="auto")return "dagami";
   if(lower==MagicDnsHost||lower=="dns.magicdns.ru"||lower=="magicdns.ru"||lower=="magicdns")return "magicdns";
   if(lower=="dns.comss.one"||lower=="router.comss.one"||lower=="comss.one"||lower=="comss")return "comss";
   if(lower=="dns.geohide.ru"||lower=="geohide.ru"||lower=="geohide")return "geohide";
   if(lower=="dns.malw.link"||lower=="malw.link"||lower=="malw")return "malw";
   if(lower=="dns.xbox-dns.ru"||lower=="xbox-dns.ru"||lower=="xboxdns")return "xboxdns";
   return null;
  }

  public static DnsEndpoint[] ForServer(string server){
   string value=(server??"").Trim().TrimEnd('.');
   var shared=DnsCatalogue.ForUrl(value);
   if(shared!=null)return shared;
   string provider=MatchBuiltInProvider(value);
   if(Array.Exists(new[]{"89.22.224.205","89.125.103.139","45.147.31.32","46.29.165.108",RetiredDagamiIp},x=>x==value))provider="dagami";
   foreach(var endpoint in Seeds())if(value==endpoint.Address)provider=endpoint.Provider;
   var snapshot=Snapshot();
   foreach(var endpoint in snapshot)if(value==endpoint.Address)provider=endpoint.Provider;
   if(provider=="dagami"||provider=="magicdns"){
    var result=new List<DnsEndpoint>();
    foreach(var endpoint in snapshot)if(endpoint.Provider==provider&&endpoint.Address==value)result.Add(endpoint);
    foreach(string id in ProviderIds)foreach(var endpoint in snapshot)if((provider=="dagami"||id==provider)&&endpoint.Provider==id&&!result.Exists(x=>x.Address==endpoint.Address))result.Add(endpoint);
    return result.ToArray();
   }
   foreach(var endpoint in BuiltInExtraSeeds())if(value==endpoint.Address)provider=endpoint.Provider;
   if(provider!=null){
    var all=AllBuiltInEndpoints();
    var result=new List<DnsEndpoint>();
    foreach(var ep in all)if(ep.Provider==provider&&ep.Address==value)result.Add(ep);
    foreach(var ep in all)if(ep.Provider==provider&&!result.Exists(x=>x.Address==ep.Address))result.Add(ep);
    foreach(string backupId in new[]{"magicdns","dagami"})
     foreach(var ep in all)if(ep.Provider==backupId&&!result.Exists(x=>x.Address==ep.Address))result.Add(ep);
    return result.ToArray();
   }
   return ParseCustomSpec(value);
  }

  public static DnsEndpoint[] ExtendedPool(string server){
   var primary=ForServer(server);
   if(primary==null)return null;
   // Additional providers are available by explicit selection, never silently
   // substituted for the selected Dagami -> MagicDNS chain.
   return OrderByProbeHealth(new List<DnsEndpoint>(primary));
  }
  internal static string PrimaryProvider(string server){
   var pool=ForServer(server);
   return pool==null||pool.Length==0?"custom":pool[0].Provider;
  }
  internal static string ProviderName(string provider) {
   switch(provider){
    case "dagami":return "Dagami";
    case "magicdns":return "MagicDNS";
    case "geohide":return "GeoHide";
    case "comss":return "Comss.one";
    case "malw":return "Malw.link";
    case "xboxdns":return "Xbox DNS";
    default:return DnsCatalogue.ProviderName(provider)??"Свой DNS";
   }
  }
  internal static int AttemptTimeout(int remaining,int attempts,int ceiling){
   return Math.Min(remaining,Math.Min(ceiling,Math.Max(1,remaining/Math.Max(1,attempts))));
  }
  static DnsEndpoint[] OrderByProbeHealth(List<DnsEndpoint> list){
   lock(Sync){
    var result=new List<DnsEndpoint>();
    var providers=new List<string>();
    foreach(var ep in list)if(!providers.Contains(ep.Provider))providers.Add(ep.Provider);
    // Health and latency only reorder nodes within a provider, never the selected
    // provider and its explicit fallback order. Do not truncate reserve nodes.
    foreach(string provider in providers){
     var working=new List<DnsEndpoint>();var untested=new List<DnsEndpoint>();var failed=new List<DnsEndpoint>();
     foreach(var ep in list){if(ep.Provider!=provider)continue;int ms;
      if(probeLatencyMs.TryGetValue(EndpointKey(ep),out ms)){if(ms>=0)working.Add(ep);else failed.Add(ep);}
      else untested.Add(ep);
     }
     working.Sort((a,b)=>probeLatencyMs[EndpointKey(a)].CompareTo(probeLatencyMs[EndpointKey(b)]));
     result.AddRange(working);result.AddRange(untested);result.AddRange(failed);
    }
    return result.ToArray();
   }
  }

  public static string FormatPoolStatus(string server){
   var pool=VerificationPool(server);
   lock(Sync)return FormatProbeReport(pool,probeStatus);
  }

  static string FormatProbeReport(DnsEndpoint[] pool,Dictionary<string,string> statuses){
   var sb=new StringBuilder();
   if(pool.Length==0)return "Адрес DNS не найден. Проверьте имя или введите IP.";
   string primary=pool[0].Provider;
   foreach(var ep in pool){
    string key=EndpointKey(ep);
    string st;
    if(!statuses.TryGetValue(key,out st))st="ожидает проверки";
    string host=ep.Protocol=="HTTPS"?"https://"+ep.Host+ep.Path.Split('?')[0]:ep.Host;
    sb.AppendLine(ProviderName(ep.Provider)+(ep.Provider!=primary?" (резерв)":"")+" · "+host);
    sb.AppendLine("   "+ep.Address+":"+ep.Port+" · "+ep.Protocol+" · "+st);
   }
   return sb.ToString().TrimEnd();
  }

  static string EndpointKey(DnsEndpoint ep){return ep.Provider+":"+ep.Address+":"+ep.Port+"/"+ep.Protocol+"/"+ep.Host+ep.Path;}

  static DnsEndpoint[] VerificationPool(string server){
   var pool=ExtendedPool(server);
   if(pool!=null)return pool;
   if(string.IsNullOrWhiteSpace(server)||Uri.CheckHostName(server.Trim())==UriHostNameType.Unknown)return new DnsEndpoint[0];
   var fallback=new List<DnsEndpoint>();
   foreach(var ip in ResolveBootstrapIps(server.Trim())){
    fallback.Add(new DnsEndpoint("custom",ip,server,"","UDP",53));
    fallback.Add(new DnsEndpoint("custom",ip,server,"","TCP",53));
   }
   return fallback.ToArray();
  }

  internal static string ProbeConfigured(string server,out string report){
   var pool=VerificationPool(server);
   if(pool.Length==0){report="";return "Адрес DNS не найден. Проверьте имя или IP.";}
   return ProbePool(pool,out report);
  }

  public static string ProbeAllBuiltIn(){
   string report;
   return ProbePool(AllBuiltInEndpoints(),out report);
  }

  static string ProbePool(DnsEndpoint[] endpointsToCheck,out string report){
   byte[] query=DnsHttpsTransport.Question(ProbeDomain);
   return RunEndpointProbes(endpointsToCheck,endpoint=>{
    int ttl;return ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query,2500),new byte[]{0,0},ProbeDomain,out ttl).ToString();
   },4500,out report);
  }
  internal static string ProbeEndpoints(DnsEndpoint[] all,Func<DnsEndpoint,string> probe,int waitMilliseconds){
   string report;
   return RunEndpointProbes(all,probe,waitMilliseconds,out report);
  }
  static string RunEndpointProbes(DnsEndpoint[] all,Func<DnsEndpoint,string> probe,int waitMilliseconds,out string report){
   if(all==null||probe==null)throw new ArgumentNullException();
   int generation;lock(Sync){generation=++probeGeneration;}
   var gate=new object();bool accepting=true;
   var statuses=new Dictionary<string,string>();var latencies=new Dictionary<string,int>();
   int okCount=0;
   int remaining=all.Length;
   foreach(var endpoint in all){
    DnsEndpoint current=endpoint;
    ThreadPool.QueueUserWorkItem(state=>{
     var sw=Stopwatch.StartNew();
     string statusText;int latency=-1;
     try{
      string ip=probe(current);
      sw.Stop();
      latency=(int)sw.ElapsedMilliseconds;
      statusText="ответ за "+latency+" мс, IP "+ip;
     }catch(Exception ex){
      sw.Stop();
      statusText=ex is TimeoutException?"тайм-аут":ex is System.Security.Authentication.AuthenticationException?"ошибка TLS":"нет ответа ("+ex.GetType().Name+")";
     }
     lock(gate){
      if(accepting){string key=EndpointKey(current);statuses[key]=statusText;latencies[key]=latency;if(latency>=0)okCount++;}
      remaining--;Monitor.PulseAll(gate);
     }
    });
   }
   int unfinished;lock(gate){
    var watch=Stopwatch.StartNew();while(remaining>0){int left=waitMilliseconds-(int)watch.ElapsedMilliseconds;if(left<=0)break;Monitor.Wait(gate,left);}
    accepting=false;unfinished=remaining;
   }
   string summary=DateTime.Now.ToString("HH:mm:ss")+" · ответов DNS "+okCount+" из "+all.Length+"."+(unfinished>0?" Тайм-аутов проверки: "+unfinished+".":"");
   foreach(var endpoint in all){string key=EndpointKey(endpoint);if(!statuses.ContainsKey(key)){statuses[key]="тайм-аут проверки";latencies[key]=-1;}}
   report=FormatProbeReport(all,statuses);
   lock(Sync){
    if(generation==probeGeneration){
     foreach(var item in statuses)probeStatus[item.Key]=item.Value;
     foreach(var item in latencies)probeLatencyMs[item.Key]=item.Value;
     lastCheckedUtc=DateTime.UtcNow;
     lastError=okCount>0?null:(unfinished>0?"Проверка не завершена":"Все узлы недоступны");
     lastProbeSummary=summary;
    }
   }
   return summary;
  }

  public static bool IsValidSpec(string spec){
   string value=(spec??"").Trim().TrimEnd('.');
   if(string.IsNullOrEmpty(value))return false;
   if(ForServer(value)!=null)return true;
   IPAddress ip;
   if(IPAddress.TryParse(value,out ip))return true;
   return Uri.CheckHostName(value)!=UriHostNameType.Unknown;
  }
  class CachedEndpoints { public DnsEndpoint[] Endpoints; public DateTime ExpiresUtc; }
  class CachedIps { public string[] Ips; public DateTime ExpiresUtc; }
  static readonly Dictionary<string, CachedEndpoints> customSpecCache = new Dictionary<string, CachedEndpoints>(StringComparer.OrdinalIgnoreCase);
  static readonly Dictionary<string, CachedIps> bootstrapCache = new Dictionary<string, CachedIps>(StringComparer.OrdinalIgnoreCase);
  static readonly object cacheLock = new object();

  public static DnsEndpoint[] ParseCustomSpec(string spec){
   if(string.IsNullOrWhiteSpace(spec))return null;
   string key = spec.Trim();
   lock(cacheLock){
    CachedEndpoints cached;
    if(customSpecCache.TryGetValue(key, out cached) && DateTime.UtcNow < cached.ExpiresUtc){
     return cached.Endpoints;
    }
   }
   var parsed = ParseCustomSpecInternal(key);
   if(parsed != null){
    lock(cacheLock){
     customSpecCache[key] = new CachedEndpoints{ Endpoints = parsed, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) };
    }
   }
   return parsed;
  }

  static DnsEndpoint[] ParseCustomSpecInternal(string raw){
   string bootstrapPart=null;
   int hash=raw.IndexOf('#');
   if(hash>0){bootstrapPart=raw.Substring(hash+1).Trim();raw=raw.Substring(0,hash).Trim();}
   var bootstrapIps=new List<string>();
   if(!string.IsNullOrEmpty(bootstrapPart)){
    foreach(string part in bootstrapPart.Split(new[]{',',';',' '},StringSplitOptions.RemoveEmptyEntries)){
     IPAddress bip;if(IPAddress.TryParse(part.Trim(),out bip)&&!bootstrapIps.Contains(bip.ToString()))bootstrapIps.Add(bip.ToString());
    }
   }
   if(raw.StartsWith("https://",StringComparison.OrdinalIgnoreCase)){
    Uri uri;if(!Uri.TryCreate(raw,UriKind.Absolute,out uri)||string.IsNullOrEmpty(uri.Host))return null;
    string path=string.IsNullOrEmpty(uri.PathAndQuery)||uri.PathAndQuery=="/"?"/dns-query":uri.PathAndQuery;
    int port=uri.Port>0?uri.Port:443;
    if(bootstrapIps.Count==0)bootstrapIps.AddRange(ResolveBootstrapIps(uri.Host));
    if(bootstrapIps.Count==0)return null;
    var list=new List<DnsEndpoint>();
    foreach(string ip in bootstrapIps)list.Add(new DnsEndpoint("custom-doh",ip,uri.Host,path,"HTTPS",port));
    return list.ToArray();
   }
   if(raw.StartsWith("tls://",StringComparison.OrdinalIgnoreCase)){
    Uri uri;if(!Uri.TryCreate(raw,UriKind.Absolute,out uri)||string.IsNullOrEmpty(uri.Host))return null;
    int port=uri.Port>0?uri.Port:853;
    if(bootstrapIps.Count==0)bootstrapIps.AddRange(ResolveBootstrapIps(uri.Host));
    if(bootstrapIps.Count==0)return null;
    var list=new List<DnsEndpoint>();
    foreach(string ip in bootstrapIps)list.Add(new DnsEndpoint("custom-dot",ip,uri.Host,"","TLS",port));
    return list.ToArray();
   }
   if(raw.StartsWith("udp://",StringComparison.OrdinalIgnoreCase)||raw.StartsWith("tcp://",StringComparison.OrdinalIgnoreCase)){
    string proto=raw.Substring(0,3).ToUpperInvariant();
    Uri uri;if(!Uri.TryCreate(raw,UriKind.Absolute,out uri)||string.IsNullOrEmpty(uri.Host))return null;
    int port=uri.Port>0?uri.Port:53;
    if(bootstrapIps.Count==0)bootstrapIps.AddRange(ResolveBootstrapIps(uri.Host));
    if(bootstrapIps.Count==0)return null;
    var list=new List<DnsEndpoint>();
    foreach(string ip in bootstrapIps)list.Add(new DnsEndpoint("custom-"+proto.ToLowerInvariant(),ip,uri.Host,"",proto,port));
    return list.ToArray();
   }
   int lastColon=raw.LastIndexOf(':');
   if(lastColon>0&&raw.IndexOf(':')==lastColon){
    string hostPart=raw.Substring(0,lastColon);
    int customPort;
    IPAddress hostIp;
    if(int.TryParse(raw.Substring(lastColon+1),out customPort)&&customPort>0&&customPort<=65535&&IPAddress.TryParse(hostPart,out hostIp)){
     return new[]{new DnsEndpoint("custom-udp",hostIp.ToString(),hostIp.ToString(),"","UDP",customPort)};
    }
   }
   return null;
  }
  static string[] ResolveBootstrapIps(string host){
   if(string.IsNullOrWhiteSpace(host))return new string[0];
   string key = host.Trim();
   lock(cacheLock){
    CachedIps cached;
    if(bootstrapCache.TryGetValue(key, out cached) && DateTime.UtcNow < cached.ExpiresUtc){
     return cached.Ips;
    }
   }
   var list=new List<string>();
   IPAddress literal;
   if(IPAddress.TryParse(key,out literal)){list.Add(literal.ToString());return list.ToArray();}
   string lower=host.ToLowerInvariant();
   if(lower=="cloudflare-dns.com"||lower=="one.one.one.one")return new[]{"1.1.1.1","1.0.0.1"};
   if(lower=="dns.google")return new[]{"8.8.8.8","8.8.4.4"};
   if(lower=="dns.quad9.net")return new[]{"9.9.9.9","149.112.112.112"};
   if(lower=="dns.adguard-dns.com"||lower=="dns.adguard.com")return new[]{"94.140.14.14","94.140.15.15"};
   if(lower=="dns.comss.one"||lower=="router.comss.one")return new[]{"195.133.25.16","212.109.195.93","185.146.157.78","212.109.221.20"};
   if(lower=="dns.geohide.ru"||lower=="geohide.ru")return new[]{"159.194.200.33","193.233.112.67","193.233.112.68","193.233.112.88","37.230.192.51"};
   if(lower=="dns.malw.link"||lower=="malw.link")return new[]{"164.5.224.81","179.254.166.171"};
   if(lower=="xbox-dns.ru"||lower=="dns.xbox-dns.ru")return new[]{"111.88.96.56","111.88.96.57"};
   try{
    foreach(var ep in AllBuiltInEndpoints()){
     try{int ttl;byte[] q=DnsHttpsTransport.Question(host);var ip=ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(ep,q,2000),new byte[]{0,0},host,out ttl);if(!list.Contains(ip.ToString()))list.Add(ip.ToString());if(list.Count>=2)break;}catch{}
    }
   }catch{}
   if(list.Count==0){
    try{foreach(var ip in Dns.GetHostAddresses(host))if(ip.AddressFamily==AddressFamily.InterNetwork&&!list.Contains(ip.ToString()))list.Add(ip.ToString());}catch{}
   }
   string[] ips = list.ToArray();
   if(ips.Length > 0){
    lock(cacheLock){
     bootstrapCache[key] = new CachedIps{ Ips = ips, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) };
    }
   }
   return ips;
  }
  public static DnsEndpoint[] ParseCustomProfile(byte[] content){
   if(content==null||content.Length>131072)throw new InvalidDataException("DNS profile too large.");
   if(content.Length==0)throw new InvalidDataException("Empty DNS profile.");
   if(content[0]==0x30){var signed=new SignedCms();signed.Decode(content);signed.CheckSignature(true);content=signed.ContentInfo.Content;}
   if(content.Length>131072)throw new InvalidDataException("DNS profile payload too large.");
   var document=new XmlDocument{XmlResolver=null};
   using(var input=new MemoryStream(content))using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=131072}))document.Load(reader);
   var result=new List<DnsEndpoint>();
   foreach(XmlNode settings in document.SelectNodes("//key[text()='DNSSettings']/following-sibling::*[1][self::dict]")){
    var protocolNode=settings.SelectSingleNode("key[text()='DNSProtocol']/following-sibling::*[1][self::string]");
    var urlNode=settings.SelectSingleNode("key[text()='ServerURL']/following-sibling::*[1][self::string]");
    var serverNameNode=settings.SelectSingleNode("key[text()='ServerName']/following-sibling::*[1][self::string]");
    string protocol=protocolNode==null?"HTTPS":protocolNode.InnerText.Trim().ToUpperInvariant();
    string host=null,path="/dns-query";int port=protocol=="TLS"?853:443;
    if(protocol=="HTTPS"&&urlNode!=null){
     Uri uri;if(!Uri.TryCreate(urlNode.InnerText.Trim(),UriKind.Absolute,out uri)||uri.Scheme!="https")continue;
     host=uri.Host;path=string.IsNullOrEmpty(uri.PathAndQuery)?"/dns-query":uri.PathAndQuery;port=uri.Port>0?uri.Port:443;
    }else if(protocol=="TLS"&&serverNameNode!=null){
     host=serverNameNode.InnerText.Trim();port=853;
    }else continue;
    var addresses=new List<string>();
    foreach(XmlNode item in settings.SelectNodes("key[text()='ServerAddresses']/following-sibling::*[1][self::array]/string")){
     string value=item.InnerText.Trim();if(PublicAddress(value)&&!addresses.Contains(value))addresses.Add(IPAddress.Parse(value).ToString());
     if(addresses.Count>8)throw new InvalidDataException("Too many DNS profile addresses.");
    }
    if(addresses.Count==0)addresses.AddRange(ResolveBootstrapIps(host));
    foreach(string addr in addresses)result.Add(new DnsEndpoint("custom-profile",addr,host,path,protocol,port));
   }
   if(result.Count==0)throw new InvalidDataException("В профиле .mobileconfig не найдено подходящих DoH/DoT серверов.");
   return result.ToArray();
  }
  public static string[] Addresses(string server){
   var pool=ForServer(server);
   if(pool==null)return new[]{(server??"").Trim().TrimEnd('.')};
   var list=new List<string>();
   foreach(var ep in pool){
    if(!list.Contains(ep.Address))list.Add(ep.Address);
    if(list.Count>=16)break;
   }
   return list.ToArray();
  }
  public static string HostFor(string provider){return Hosts[provider];}
  public static string[] ParseProfile(string provider,byte[] content){
   var primaryAddresses=new HashSet<string>();
   return ParseOfficialProfile(provider,content,primaryAddresses);
  }
  static string[] ParseOfficialProfile(string provider,byte[] content,HashSet<string> primaryAddresses){
   if(content==null||content.Length>131072)throw new InvalidDataException("DNS profile too large.");
   if(content.Length==0)throw new InvalidDataException("Empty DNS profile.");
   if(content[0]==0x30){var signed=new SignedCms();signed.Decode(content);signed.CheckSignature(true);content=signed.ContentInfo.Content;}
   if(content.Length>131072)throw new InvalidDataException("DNS profile payload too large.");
   var document=new XmlDocument{XmlResolver=null};
   using(var input=new MemoryStream(content))using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=131072}))document.Load(reader);
   var addresses=new List<string>();
   foreach(XmlNode settings in document.SelectNodes("//key[text()='DNSSettings']/following-sibling::*[1][self::dict]")){
    var protocol=settings.SelectSingleNode("key[text()='DNSProtocol']/following-sibling::*[1][self::string]");
    var url=settings.SelectSingleNode("key[text()='ServerURL']/following-sibling::*[1][self::string]");
    if(protocol==null||protocol.InnerText!="HTTPS"||url==null)continue;
    bool primary=provider=="dagami"&&url.InnerText=="https://dns.dagami.ru/dns-query";
    if(!primary&&url.InnerText!=(provider=="magicdns"?MagicDnsAuthority:"https://"+Hosts[provider]+"/dns-query"))continue;
    foreach(XmlNode item in settings.SelectNodes("key[text()='ServerAddresses']/following-sibling::*[1][self::array]/string")){
     string value=item.InnerText.Trim();
     if(!PublicAddress(value))continue;
     value=IPAddress.Parse(value).ToString();
     if(!addresses.Contains(value))addresses.Add(value);
     if(primary)primaryAddresses.Add(value);
     if(addresses.Count>8)throw new InvalidDataException("Too many DNS profile addresses.");
    }
   }
   if(addresses.Count==0)throw new InvalidDataException("Official DNS profile has no matching HTTPS resolver.");return addresses.ToArray();
  }
  static byte[] DownloadProfile(string provider){
   try{return DownloadProfileFrom(Profiles[provider]);}
   catch(IOException){
    if(provider!="magicdns")throw;
    return DownloadProfileFrom(new Uri("https://magicdns.ru/profile.mobileconfig"));
   }
  }
  static byte[] DownloadProfileFrom(Uri source){
   var candidates=new List<string>();
   if(source.Host==MagicDnsHost)
    foreach(var endpoint in Snapshot())if(endpoint.Provider=="magicdns")candidates.Add(endpoint.Address);
   foreach(string address in candidates)try{return DnsHttpsTransport.Request(address,source.Host,source.PathAndQuery,"GET",null,null,131072,3500);}catch{}
   candidates.Clear();
   foreach(var endpoint in Snapshot()){
    try{int ttl;byte[] query=DnsHttpsTransport.Question(source.Host);var address=ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query),new byte[]{0,0},source.Host,out ttl);if(!candidates.Contains(address.ToString()))candidates.Add(address.ToString());if(candidates.Count>=2)break;}catch{}
   }
   if(candidates.Count==0)foreach(var endpoint in new[]{new DnsEndpoint("bootstrap","1.1.1.1","cloudflare-dns.com"),new DnsEndpoint("bootstrap","8.8.8.8","dns.google")}){
    try{int ttl;byte[] query=DnsHttpsTransport.Question(source.Host);var address=ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query),new byte[]{0,0},source.Host,out ttl);candidates.Add(address.ToString());break;}catch{}
   }
   if(candidates.Count==0)try{foreach(var ip in Dns.GetHostAddresses(source.Host))if(PublicAddress(ip.ToString())&&!candidates.Contains(ip.ToString()))candidates.Add(ip.ToString());}catch{}
   foreach(string address in candidates)try{return DnsHttpsTransport.Request(address,source.Host,source.PathAndQuery,"GET",null,null,131072,8000);}catch{}
   throw new IOException("Official DNS profile is unavailable over verified HTTPS.");
  }
  public static void Refresh(Func<string,byte[]> download=null,Func<DnsEndpoint,bool> verify=null){
   if(Interlocked.Exchange(ref refreshing,1)!=0)return;
   try{
    var errors=new List<string>();
    foreach(string provider in ProviderIds)try{
     var primaryAddresses=new HashSet<string>();
     var addresses=new List<string>(ParseOfficialProfile(provider,(download??DownloadProfile)(provider),primaryAddresses));
     if(provider=="dagami")IncludeDagamiSeeds(addresses);
     Func<string,DnsEndpoint> endpointFor=address=>primaryAddresses.Contains(address)
      ?new DnsEndpoint(provider,address,"dns.dagami.ru"):ProviderEndpoint(provider,address);
     var healthy=new List<DnsEndpoint>();
     if(verify!=null){
      foreach(string address in addresses){var candidate=endpointFor(address);if(verify(candidate))healthy.Add(candidate);else Logger.Log("[DNS catalogue] provider="+provider+" rejected="+address+" (DoH verification)");}
     }else{
      bool[] ok=new bool[addresses.Count];
      System.Threading.Tasks.Parallel.For(0,addresses.Count,i=>{try{ok[i]=VerifyEndpoint(endpointFor(addresses[i]));}catch{ok[i]=false;}});
      for(int i=0;i<addresses.Count;i++){var candidate=endpointFor(addresses[i]);if(ok[i])healthy.Add(candidate);else Logger.Log("[DNS catalogue] provider="+provider+" rejected="+addresses[i]+" (DoH verification)");}
     }
     if(healthy.Count==0)throw new IOException("No verified DoH endpoints in the official profile.");
     lock(Sync){var combined=new List<DnsEndpoint>();foreach(string id in ProviderIds)if(id==provider)combined.AddRange(healthy);else combined.AddRange(endpoints.FindAll(x=>x.Provider==id));endpoints=combined;}
    }catch(Exception error){
     errors.Add(provider+":"+error.GetType().Name);
     if(download==null&&verify==null){
      try{
       var provList=Array.FindAll(Snapshot(),x=>x.Provider==provider);
       if(provList.Length>1){
        bool[] ok=new bool[provList.Length];
        System.Threading.Tasks.Parallel.For(0,provList.Length,i=>{try{int ttl;byte[] q=DnsHttpsTransport.Question("example.com");ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(provList[i],q,2500),new byte[]{0,0},"example.com",out ttl);ok[i]=true;}catch{ok[i]=false;}});
        var kept=new List<DnsEndpoint>();for(int i=0;i<provList.Length;i++)if(ok[i])kept.Add(provList[i]);else Logger.Log("[DNS catalogue] provider="+provider+" rejected="+provList[i].Address+" (fallback seed verification)");
        if(kept.Count>0&&kept.Count<provList.Length)lock(Sync){var combined=new List<DnsEndpoint>();foreach(string id in ProviderIds)if(id==provider)combined.AddRange(kept);else combined.AddRange(endpoints.FindAll(x=>x.Provider==id));endpoints=combined;}
       }
      }catch{}
     }
    }
    if(download==null&&verify==null){
     RefreshNamedEndpoints(LookupNamedAddresses,VerifyEndpoint);
     RefreshSharedCatalogue();
     try{ProbeAllBuiltIn();}catch{}
    }else{
     lock(Sync){lastCheckedUtc=DateTime.UtcNow;lastError=errors.Count==0?null:string.Join(", ",errors.ToArray());}
    }
    Save();
   }finally{Interlocked.Exchange(ref refreshing,0);}
  }
  public static void Initialize(string path=null,bool background=true){
   lock(Sync){
    if(timer!=null)return;
    cachePath=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"devproxy","dns-endpoints.json");
    Load();
     DnsCatalogue.Initialize(Path.Combine(Path.GetDirectoryName(cachePath),"dns-shared-catalogue.json"));
    if(background)timer=new Timer(state=>{try{Refresh();}catch(Exception error){Logger.Log("[DNS catalogue] "+error.GetType().Name);}},null,0,600000);
   }
  }
  internal static void RefreshSharedCatalogue(){DnsCatalogue.Refresh(null,VerifyEndpoint);}
  static void Load(){try{
   if(!File.Exists(cachePath)||new FileInfo(cachePath).Length>32768)return;
   var json=new JavaScriptSerializer{MaxJsonLength=32768};var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(cachePath));
   if(Convert.ToInt32(saved["Version"])!=1)return;var values=new List<DnsEndpoint>();
   object rawPrimary;
   var primaryIps=new HashSet<string>();
   if(saved.TryGetValue("dagami-primary",out rawPrimary)&&rawPrimary is System.Collections.IEnumerable&&!(rawPrimary is string)){
    foreach(object item in (System.Collections.IEnumerable)rawPrimary)if(primaryIps.Count<8&&PublicAddress(Convert.ToString(item)))primaryIps.Add(Convert.ToString(item));
   }
   foreach(string provider in ProviderIds){
    object authority;
    if(provider=="magicdns"&&(!saved.TryGetValue("magicdns-authority",out authority)||Convert.ToString(authority)!=MagicDnsAuthority)){
     foreach(string ip in MagicDnsSeeds)values.Add(ProviderEndpoint(provider,ip));
     continue;
    }
    var list=(System.Collections.IEnumerable)saved[provider];
    var addresses=new List<string>();int count=0;
    foreach(object item in list){
     string address=Convert.ToString(item);
     if(!PublicAddress(address)||++count>8)throw new InvalidDataException("Invalid cached DNS endpoint.");
     if(!addresses.Contains(address))addresses.Add(address);
    }
    if(count==0)throw new InvalidDataException("Empty cached provider.");
    if(provider=="dagami")IncludeDagamiSeeds(addresses);
    foreach(string address in addresses)values.Add(provider=="dagami"&&primaryIps.Contains(address)
     ?new DnsEndpoint(provider,address,"dns.dagami.ru"):ProviderEndpoint(provider,address));
   }
   var named=new List<DnsEndpoint>();
   foreach(string provider in NamedProviderIds){
    object stored;if(!saved.TryGetValue(provider,out stored))continue;
    var list=stored as System.Collections.IEnumerable;if(list==null||stored is string)continue;
    var accepted=new List<DnsEndpoint>();int count=0;
    foreach(object item in list){
     string address=Convert.ToString(item);
     if(!PublicAddress(address)||++count>8){accepted.Clear();break;}
     address=IPAddress.Parse(address).ToString();
     if(!accepted.Exists(x=>x.Address==address))accepted.Add(NamedEndpoint(provider,address));
    }
    named.AddRange(accepted);
   }
   lock(Sync){endpoints=values;namedEndpoints=named;}
  }catch{}}
  static void Save(){if(cachePath==null)return;string temporary=null;try{
   Directory.CreateDirectory(Path.GetDirectoryName(cachePath));var data=new Dictionary<string,object>{{"Version",1}};foreach(string provider in ProviderIds){var values=new List<string>();foreach(var endpoint in Snapshot())if(endpoint.Provider==provider)values.Add(endpoint.Address);data[provider]=values.ToArray();}
   data["magicdns-authority"]=MagicDnsAuthority;
   data["dagami-primary"]=Array.ConvertAll(Array.FindAll(Snapshot(),x=>x.Provider=="dagami"&&x.Host=="dns.dagami.ru"),x=>x.Address);
   lock(Sync)foreach(string provider in NamedProviderIds)data[provider]=namedEndpoints.FindAll(x=>x.Provider==provider).ConvertAll(x=>x.Address).ToArray();
   temporary=cachePath+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(data),new UTF8Encoding(false));if(File.Exists(cachePath))File.Replace(temporary,cachePath,null);else File.Move(temporary,cachePath);
  }catch(Exception error){Logger.Log("[DNS catalogue cache] "+error.GetType().Name);}finally{if(temporary!=null&&File.Exists(temporary))try{File.Delete(temporary);}catch{}}}
 }
}
