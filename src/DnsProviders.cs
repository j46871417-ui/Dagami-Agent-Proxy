using System;
using System.Collections.Generic;
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
  public DnsEndpoint(string provider,string address,string host){Provider=provider;Address=address;Host=host;}
 }
 public static class DnsProviders {
  static readonly object Sync=new object();
  static readonly string[] ProviderIds={"dagami","magicdns"};
  static readonly Dictionary<string,string> Hosts=new Dictionary<string,string>{{"dagami","dns.dagami.ru"},{"magicdns","dns.magicdns.ru"}};
  static readonly Dictionary<string,Uri> Profiles=new Dictionary<string,Uri>{{"dagami",new Uri("https://dns.dagami.ru/dagami.mobileconfig")},{"magicdns",new Uri("https://magicdns.ru/profile.mobileconfig")}};
  static List<DnsEndpoint> endpoints=Seeds();
  static string cachePath;static Timer timer;static int refreshing;
  static DateTime lastCheckedUtc;static string lastError;
  public static DateTime LastCheckedUtc {get{lock(Sync)return lastCheckedUtc;}}
  public static string LastError {get{lock(Sync)return lastError;}}
  static List<DnsEndpoint> Seeds(){var result=new List<DnsEndpoint>();foreach(string address in new[]{"45.147.31.32","46.29.165.108","89.125.103.139"})result.Add(new DnsEndpoint("dagami",address,Hosts["dagami"]));foreach(string address in new[]{"109.120.132.43","94.249.177.102","2.56.175.196"})result.Add(new DnsEndpoint("magicdns",address,Hosts["magicdns"]));return result;}
  public static bool PublicAddress(string text){IPAddress ip;if(!IPAddress.TryParse(text,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;byte[] b=ip.GetAddressBytes();return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127);}
  public static DnsEndpoint[] Snapshot(){lock(Sync)return endpoints.ToArray();}
  public static DnsEndpoint[] ForServer(string server){
   string value=(server??"").Trim().TrimEnd('.');string provider=null;
   foreach(string id in ProviderIds)if(value.Equals(Hosts[id],StringComparison.OrdinalIgnoreCase)||value.Equals(id=="dagami"?"dns.dagami.ru":"magicdns.ru",StringComparison.OrdinalIgnoreCase))provider=id;
   foreach(var endpoint in Seeds())if(value==endpoint.Address)provider=endpoint.Provider;
   foreach(var endpoint in Snapshot())if(value==endpoint.Address)provider=endpoint.Provider;
   if(provider==null)return null;
   var result=new List<DnsEndpoint>();
   foreach(var endpoint in Snapshot())if(endpoint.Provider==provider&&endpoint.Address==value)result.Add(endpoint);
   foreach(string id in ProviderIds)foreach(var endpoint in Snapshot())if((provider=="dagami"||id==provider)&&endpoint.Provider==id&&!result.Exists(x=>x.Address==endpoint.Address))result.Add(endpoint);
   return result.ToArray();
  }
  public static string[] Addresses(string server){var pool=ForServer(server);if(pool==null)return new[]{(server??"").Trim().TrimEnd('.')};return Array.ConvertAll(pool,x=>x.Address);}
  public static string HostFor(string provider){return Hosts[provider];}
  public static string[] ParseProfile(string provider,byte[] content){
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
    if(protocol==null||protocol.InnerText!="HTTPS"||url==null||url.InnerText!="https://"+Hosts[provider]+"/dns-query")continue;
    foreach(XmlNode item in settings.SelectNodes("key[text()='ServerAddresses']/following-sibling::*[1][self::array]/string")){
     string value=item.InnerText.Trim();if(PublicAddress(value)&&!addresses.Contains(value))addresses.Add(IPAddress.Parse(value).ToString());
     if(addresses.Count>8)throw new InvalidDataException("Too many DNS profile addresses.");
    }
   }
   if(addresses.Count==0)throw new InvalidDataException("Official DNS profile has no matching HTTPS resolver.");return addresses.ToArray();
  }
  static byte[] DownloadProfile(string provider){
   Uri source=Profiles[provider];var candidates=new List<string>();
   foreach(var endpoint in Snapshot()){
    try{int ttl;byte[] query=DnsHttpsTransport.Question(source.Host);var address=ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query),new byte[]{0,0},source.Host,out ttl);if(!candidates.Contains(address.ToString()))candidates.Add(address.ToString());if(candidates.Count>=2)break;}catch{}
   }
   if(candidates.Count==0)foreach(var endpoint in new[]{new DnsEndpoint("bootstrap","1.1.1.1","cloudflare-dns.com"),new DnsEndpoint("bootstrap","8.8.8.8","dns.google")}){
    try{int ttl;byte[] query=DnsHttpsTransport.Question(source.Host);var address=ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query),new byte[]{0,0},source.Host,out ttl);candidates.Add(address.ToString());break;}catch{}
   }
   foreach(string address in candidates)try{return DnsHttpsTransport.Request(address,source.Host,source.PathAndQuery,"GET",null,null,131072,8000);}catch{}
   throw new IOException("Official DNS profile is unavailable over verified HTTPS.");
  }
  public static void Refresh(Func<string,byte[]> download=null,Func<DnsEndpoint,bool> verify=null){
   if(Interlocked.Exchange(ref refreshing,1)!=0)return;
   try{
    var errors=new List<string>();
    foreach(string provider in ProviderIds)try{
     var addresses=new List<string>(ParseProfile(provider,(download??DownloadProfile)(provider)));
     // Dagami's owner also provided reserve nodes not always listed in the
     // published profile. Keep checking those trusted seeds, with the same TLS
     // authority and DoH validation, instead of silently discarding them.
     if(provider=="dagami"){
      var reserved=new HashSet<string>();foreach(var seed in Seeds())if(seed.Provider==provider){reserved.Add(seed.Address);if(!addresses.Contains(seed.Address))addresses.Add(seed.Address);}
      for(int index=addresses.Count-1;addresses.Count>8&&index>=0;index--)if(!reserved.Contains(addresses[index]))addresses.RemoveAt(index);
     }
     var healthy=new List<DnsEndpoint>();
     foreach(string address in addresses){var candidate=new DnsEndpoint(provider,address,Hosts[provider]);bool good;
      if(verify!=null)good=verify(candidate);else try{int ttl;byte[] query=DnsHttpsTransport.Question("example.com");ScopedDnsResolver.ParseAnswer(DnsHttpsTransport.Exchange(candidate,query),new byte[]{0,0},"example.com",out ttl);good=true;}catch{good=false;}
      if(good)healthy.Add(candidate);else Logger.Log("[DNS catalogue] provider="+provider+" rejected="+address+" (DoH verification)");
     }
     if(healthy.Count==0)throw new IOException("No verified DoH endpoints in the official profile.");
     lock(Sync){var combined=new List<DnsEndpoint>();foreach(string id in ProviderIds)if(id==provider)combined.AddRange(healthy);else combined.AddRange(endpoints.FindAll(x=>x.Provider==id));endpoints=combined;}
    }catch(Exception error){errors.Add(provider+":"+error.GetType().Name);}
    lock(Sync){lastCheckedUtc=DateTime.UtcNow;lastError=errors.Count==0?null:string.Join(", ",errors.ToArray());}Save();
   }finally{Interlocked.Exchange(ref refreshing,0);}
  }
  public static void Initialize(string path=null,bool background=true){
   lock(Sync){if(timer!=null)return;cachePath=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"devproxy","dns-endpoints.json");}
   Load();if(background)timer=new Timer(state=>{try{Refresh();}catch(Exception error){Logger.Log("[DNS catalogue] "+error.GetType().Name);}},null,0,600000);
  }
  static void Load(){try{
   if(!File.Exists(cachePath)||new FileInfo(cachePath).Length>32768)return;
   var json=new JavaScriptSerializer{MaxJsonLength=32768};var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(cachePath));
   if(Convert.ToInt32(saved["Version"])!=1)return;var values=new List<DnsEndpoint>();
   foreach(string provider in ProviderIds){var list=(System.Collections.IEnumerable)saved[provider];int count=0;foreach(object item in list){string address=Convert.ToString(item);if(!PublicAddress(address)||++count>8)throw new InvalidDataException("Invalid cached DNS endpoint.");if(!values.Exists(x=>x.Address==address))values.Add(new DnsEndpoint(provider,address,Hosts[provider]));}if(count==0)throw new InvalidDataException("Empty cached provider.");}
   lock(Sync)endpoints=values;
  }catch{}}
  static void Save(){if(cachePath==null)return;string temporary=null;try{
   Directory.CreateDirectory(Path.GetDirectoryName(cachePath));var data=new Dictionary<string,object>{{"Version",1}};foreach(string provider in ProviderIds){var values=new List<string>();foreach(var endpoint in Snapshot())if(endpoint.Provider==provider)values.Add(endpoint.Address);data[provider]=values.ToArray();}
   temporary=cachePath+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(data),new UTF8Encoding(false));if(File.Exists(cachePath))File.Replace(temporary,cachePath,null);else File.Move(temporary,cachePath);
  }catch(Exception error){Logger.Log("[DNS catalogue cache] "+error.GetType().Name);}finally{if(temporary!=null&&File.Exists(temporary))try{File.Delete(temporary);}catch{}}}
 }
}
