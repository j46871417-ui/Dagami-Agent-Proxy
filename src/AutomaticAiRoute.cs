using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DevProxyApp {
 internal sealed class AiRoute {
  internal readonly string Name, Proxy, Dns;
  internal readonly bool PublicProxy;

  internal AiRoute(string name,string proxy,string dns,bool publicProxy=false) {
   Name=name;Proxy=proxy;Dns=dns;PublicProxy=publicProxy;
  }

  internal bool SameRoute(AiRoute other) {
   return other!=null&&Proxy==other.Proxy&&Dns==other.Dns&&PublicProxy==other.PublicProxy;
  }

  internal string ProbeDns(string service) {
   // The Gemini desktop launcher uses a dedicated proxy-only bridge when an
   // upstream proxy is selected. Verify the same path that the launcher uses.
   return service=="Gemini"&&Proxy!=null&&Proxy!="direct"?null:Dns;
  }
 }

 internal sealed class AiRouteSelection {
  internal AiRoute Route;
  internal readonly List<string> Report=new List<string>();
 }

 internal static class AutomaticAiRoute {
  internal static readonly string[] Services={"Gemini","Antigravity","Codex","Claude","Cursor"};
  const int ProbeTimeout=6500;

  sealed class Check {
   internal readonly string Host, Path, Body, Headers;
   internal readonly Func<int,bool> Accept;
   internal readonly bool GoogleLogin;
   internal Check(string host,string path,Func<int,bool> accept,string body=null,string headers=null,bool googleLogin=false) {
    Host=host;Path=path;Accept=accept;Body=body;Headers=headers;GoogleLogin=googleLogin;
   }
  }

  static bool Ok(int status) {return status>=200&&status<300;}
  static bool ApiAvailable(int status) {return Ok(status)||status==400||status==401||status==404||status==405;}
  internal static bool IsGoogleLoginRedirect(int status,string location) {
   Uri url;
   return (status==302||status==303||status==307||status==308)&&Uri.TryCreate(location,UriKind.Absolute,out url)&&
    url.Scheme=="https"&&url.IsDefaultPort&&url.UserInfo.Length==0&&url.IdnHost=="accounts.google.com";
  }

  static Check[] Checks(string service) {
   switch(service) {
    case "Gemini": return new[]{
     new Check("gemini.google.com","/app",Ok,googleLogin:true),
     new Check("geminiweb-pa.clients6.google.com","/",ApiAvailable)
    };
    case "Antigravity": return new[]{
     new Check("www.googleapis.com","/oauth2/v2/userinfo",status=>status==401),
     new Check("cloudcode-pa.googleapis.com","/v1internal:loadCodeAssist",status=>status==401,"{}"),
     new Check("daily-cloudcode-pa.googleapis.com","/v1internal:loadCodeAssist",status=>status==401,"{}")
    };
    case "Codex": return new[]{new Check("api.openai.com","/v1/models",status=>status==401)};
    case "Claude": return new[]{new Check("api.anthropic.com","/v1/models",status=>status==401,null,"anthropic-version: 2023-06-01\r\n")};
    case "Cursor": return new[]{new Check("api2.cursor.sh","/",ApiAvailable)};
    default: throw new ArgumentException("Выберите ИИ-сервис для проверки.");
   }
  }

  internal static bool SupportsDns(string service) {return service=="Gemini"||service=="Antigravity";}

  internal static List<AiRoute> Candidates(string service,AiRoute current,bool hasKey,string custom,string dns,bool allowPublic) {
   var routes=new List<AiRoute>();
   Action<AiRoute> add=route=>{if(!routes.Exists(route.SameRoute))routes.Add(route);};
   bool dnsSupported=SupportsDns(service);
   if(current!=null&&(!current.PublicProxy||allowPublic)&&
      (current.Proxy!=null||dnsSupported&&current.Dns!=null))add(current);
   if(hasKey)add(new AiRoute("Dagami по ключу",ManagedAccess.Profile,null));
   if(custom!=null&&custom!="direct")add(new AiRoute("Свой прокси",custom,null));
   if(dnsSupported) {
    if(dns!=null)add(new AiRoute("Выбранный DNS",null,dns));
    add(new AiRoute("Dagami DNS / MagicDNS",null,"dns.dagami.ru"));
    add(new AiRoute("Comss.one",null,"dns.comss.one"));
    add(new AiRoute("GeoHide",null,"dns.geohide.ru"));
    if(service=="Antigravity"&&custom!=null&&custom!="direct")
     add(new AiRoute("Smart DNS + свой прокси",custom,dns??"dns.dagami.ru"));
   }
   if(custom=="direct")add(new AiRoute("Прямой маршрут / системный VPN","direct",null));
   return routes;
  }

  internal static string Probe(AiRoute route,string service) {
   using(var bridge=new LocalProxyBridge()) {
    if(!bridge.Start(0,route.Proxy??"direct",route.ProbeDns(service)))return "временный мост не запустился";
    return VerifyRunning(bridge.BoundPort,service);
   }
  }

  internal static string VerifyRunning(int port,string service) {
    if(port<=0)return "локальный мост не запустился";
    var checks=Checks(service);
    var errors=new string[checks.Length];
    Parallel.For(0,checks.Length,index=>{
     var check=checks[index];
     try {
      string location;
      int status=AntigravityDiagnostics.ProbeHttpStatus(check.Host,check.Path,check.Body,port,check.Headers,ProbeTimeout,out location);
      if(!check.Accept(status)&&!(check.GoogleLogin&&IsGoogleLoginRedirect(status,location)))errors[index]=check.Host+" — HTTP "+status;
     } catch(Exception error) {
      errors[index]=check.Host+" — "+error.GetType().Name;
     }
    });
    return Array.Exists(errors,error=>error!=null)?string.Join("; ",Array.FindAll(errors,error=>error!=null)):null;
  }

  internal static async Task StartAndVerify(Func<int> start,Func<int,Task<string>> verify,Action persist) {
   int port=start();
   if(port<=0)throw new IOException("Новый локальный мост не запустился.");
   string failure=await verify(port);
   if(failure!=null)throw new IOException("Проверка запущенного маршрута не прошла. "+failure+".");
   persist();
  }

  internal static AiRouteSelection Select(IList<AiRoute> routes,Func<AiRoute,string> probe,Func<string> findPublic) {
   var result=new AiRouteSelection();
   if(routes.Count>0) {
    string initial=probe(routes[0]);
    if(initial==null) {result.Route=routes[0];return result;}
    result.Report.Add(routes[0].Name+" — "+initial);
   }
   var errors=new string[routes.Count];
   if(routes.Count>1)Parallel.For(1,routes.Count,new ParallelOptions{MaxDegreeOfParallelism=4},i=>errors[i]=probe(routes[i]));
   for(int i=1;i<routes.Count;i++) {
    if(errors[i]==null&&result.Route==null)result.Route=routes[i];
    else if(errors[i]!=null)result.Report.Add(routes[i].Name+" — "+errors[i]);
   }
   if(result.Route!=null||findPublic==null)return result;
   try {
    string proxy=findPublic();
    if(proxy!=null)result.Route=new AiRoute("Публичный прокси",proxy,null,true);
    else result.Report.Add("Публичный прокси — ни один адрес не прошёл проверку сервиса");
   } catch(IOException) {
    result.Report.Add("Публичный прокси — список адресов недоступен");
   }
   return result;
  }
 }
}
