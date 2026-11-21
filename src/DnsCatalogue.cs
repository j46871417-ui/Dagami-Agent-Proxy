using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DevProxyApp {
 internal sealed class CatalogueProvider {
  public string Id {get;set;}
  public string Name {get;set;}
  public string Url {get;set;}
  public string[] Addresses {get;set;}
  public override string ToString() {return Name+" · "+Addresses.Length+" IPv4 · "+(Url.StartsWith("tls://")?"DoT":"DoH");}

  internal DnsEndpoint[] Endpoints() {
   var uri=new Uri(Url);
   bool dot=uri.Scheme=="tls";
   return Addresses.Select(address=>new DnsEndpoint("catalogue/"+Id,address,uri.IdnHost,uri.AbsolutePath,
    dot?"TLS":"HTTPS",dot&&uri.Port<0?853:uri.Port)).ToArray();
  }
 }

 internal sealed class CatalogueDocument {
  public int Version {get;set;}
  public CatalogueProvider[] Providers {get;set;}
 }

 internal static class DnsCatalogue {
  internal const string Source="https://dagami.ru/dns-catalogue.json";
  const int MaxBytes=32768;
  static readonly object Sync=new object();
  static CatalogueProvider[] providers=new CatalogueProvider[0];
  static string cachePath, status="Каталог ещё не проверен.";
  static int refreshing;

  internal static string Status {get {lock(Sync)return Volatile.Read(ref refreshing)!=0?"Каталог обновляется…":status;}}
  internal static CatalogueProvider[] Options() {
   lock(Sync)return providers.Where(provider=>provider.Addresses.Length>0).ToArray();
  }

  internal static DnsEndpoint[] ForUrl(string value) {
   Uri uri;
   if(!Uri.TryCreate(value,UriKind.Absolute,out uri))return null;
   lock(Sync) {
    var provider=Array.Find(providers,item=>item.Url==uri.AbsoluteUri&&item.Addresses.Length>0);
    return provider==null?null:provider.Endpoints();
   }
  }

  internal static string ProviderName(string id) {
   lock(Sync) {
    var provider=Array.Find(providers,item=>"catalogue/"+item.Id==id);
    return provider==null?null:provider.Name;
   }
  }

  static readonly System.Text.RegularExpressions.Regex IdPattern=
   new System.Text.RegularExpressions.Regex(@"\A[a-z0-9][a-z0-9-]{0,31}\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant);

  internal static CatalogueDocument Parse(byte[] bytes) {
   if(bytes.Length>MaxBytes)throw new InvalidDataException("Слишком большой DNS-каталог.");
   var serializer=new JavaScriptSerializer{MaxJsonLength=MaxBytes,RecursionLimit=12};
   var document=serializer.Deserialize<CatalogueDocument>(new UTF8Encoding(false,true).GetString(bytes));
   if(document==null||document.Version!=1||document.Providers==null||document.Providers.Length>16)
    throw new InvalidDataException("Неподдерживаемый формат DNS-каталога.");
   var ids=new HashSet<string>();
   var urls=new HashSet<string>();
   foreach(var provider in document.Providers) {
    Uri uri;
    if(provider==null||!IdPattern.IsMatch(provider.Id??"")||!ids.Add(provider.Id)||
       string.IsNullOrWhiteSpace(provider.Name)||provider.Name.Length>64||provider.Name.Any(char.IsControl)||
       !Uri.TryCreate(provider.Url,UriKind.Absolute,out uri)||provider.Url.Length>512||
       (uri.Scheme!="https"&&uri.Scheme!="tls")||uri.HostNameType!=UriHostNameType.Dns||!uri.IdnHost.Contains(".")||
       uri.IdnHost.EndsWith(".local",StringComparison.OrdinalIgnoreCase)||uri.IdnHost.EndsWith(".localhost",StringComparison.OrdinalIgnoreCase)||
       uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Query.Length!=0||
       (uri.Scheme=="https"&&uri.AbsolutePath=="/")||
       (uri.Scheme=="tls"&&uri.AbsolutePath.Length>0&&uri.AbsolutePath!="/")||uri.Port==0||
       provider.Addresses==null||provider.Addresses.Length>8)
     throw new InvalidDataException("Некорректная запись DNS-провайдера.");
    provider.Url=uri.AbsoluteUri;
    if(!urls.Add(provider.Url)||provider.Addresses.Any(address=>!IsGlobalIpv4(address)))
     throw new InvalidDataException("Повтор URL или непубличный адрес DNS-провайдера.");
    provider.Addresses=provider.Addresses.Distinct().ToArray();
   }
   return document;
  }

  static bool IsGlobalIpv4(string text) {
   IPAddress ip;
   if(!DnsProviders.PublicAddress(text)||!IPAddress.TryParse(text,out ip)||ip.ToString()!=text)return false;
   var b=ip.GetAddressBytes();
   return b[0]<224 && !(b[0]==192&&(b[1]==0||b[1]==88&&b[2]==99)) &&
    !(b[0]==198&&(b[1]==18||b[1]==19||b[1]==51&&b[2]==100)) &&
    !(b[0]==203&&b[1]==0&&b[2]==113);
  }

  internal static void Initialize(string path) {
   lock(Sync) {
    if(cachePath==path)return;
    cachePath=path;
    providers=new CatalogueProvider[0];
    status="Каталог ещё не проверен.";
    try {
     var info=new FileInfo(path);
     if(!info.Exists||info.Length>MaxBytes)return;
     providers=Parse(File.ReadAllBytes(path)).Providers;
     status="Загружена последняя копия каталога.";
    }catch(Exception error) {Logger.Trace("[DNS shared catalogue] cache: "+error.GetType().Name);}
   }
  }

  static byte[] Download() {
   ManagedAccess.ConfigureControlTls();
   var request=(HttpWebRequest)WebRequest.Create(Source);
   request.Proxy=null;
   request.AllowAutoRedirect=false;
   request.Timeout=7000;
   request.ReadWriteTimeout=3000;
   using(var deadline=new Timer(_=>request.Abort(),null,8000,Timeout.Infinite))
   using(var response=(HttpWebResponse)request.GetResponse())
   using(var stream=response.GetResponseStream())
   using(var output=new MemoryStream()) {
    if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Каталог недоступен.");
    var buffer=new byte[4096];
    int read;
    while((read=stream.Read(buffer,0,buffer.Length))>0) {
     if(output.Length+read>MaxBytes)throw new InvalidDataException("Слишком большой DNS-каталог.");
     output.Write(buffer,0,read);
    }
    return output.ToArray();
   }
  }

  internal static void Refresh(Func<byte[]> download,Func<DnsEndpoint,bool> verify) {
   if(Interlocked.Exchange(ref refreshing,1)!=0)return;
   try {
    var document=Parse((download??Download)());
    var candidates=document.Providers.SelectMany(provider=>provider.Endpoints()).ToArray();
    var passed=new bool[candidates.Length];
    Parallel.For(0,candidates.Length,new ParallelOptions{MaxDegreeOfParallelism=6},index=>{
     try {passed[index]=verify(candidates[index]);}
     catch(Exception error) {Logger.Trace("[DNS shared catalogue] "+candidates[index].Provider+": "+error.GetType().Name);}
    });
    int offset=0;
    foreach(var provider in document.Providers) {
     var addresses=provider.Addresses;
     provider.Addresses=addresses.Where((address,index)=>passed[offset+index]).ToArray();
     offset+=addresses.Length;
    }
    lock(Sync) {
     // An explicitly empty published list removes its entries. A transport outage
     // preserves locally verified addresses still listed by the publisher.
     foreach(var provider in document.Providers)if(provider.Addresses.Length==0) {
      var old=Array.Find(providers,item=>item.Id==provider.Id&&item.Url==provider.Url);
      int first=Array.FindIndex(candidates,ep=>ep.Provider=="catalogue/"+provider.Id);
      if(old!=null&&first>=0)
       provider.Addresses=old.Addresses.Where(address=>Array.Exists(candidates,ep=>ep.Provider=="catalogue/"+provider.Id&&ep.Address==address)).ToArray();
     }
     if(cachePath!=null)Save(document);
     providers=document.Providers;
     status="Провайдеров в каталоге: "+providers.Length+". С проверенными адресами: "+providers.Count(item=>item.Addresses.Length>0)+".";
    }
   }catch(Exception error) {
    lock(Sync)status=providers.Length==0?"Каталог недоступен.":"Не удалось обновить каталог. Используется последняя копия.";
    Logger.Trace("[DNS shared catalogue] refresh: "+error.GetType().Name);
   }finally {Interlocked.Exchange(ref refreshing,0);}
  }

  static void Save(CatalogueDocument document) {
   string temporary=cachePath+"."+Guid.NewGuid().ToString("N")+".tmp";
   try {
    Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
    File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(document),new UTF8Encoding(false));
    if(File.Exists(cachePath))File.Replace(temporary,cachePath,null);else File.Move(temporary,cachePath);
   } finally {
    if(File.Exists(temporary))try{File.Delete(temporary);}catch(IOException){}
   }
  }
 }
}
