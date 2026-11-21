using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using DevProxyApp;

class ProxyFailureChecks {
 static int checks;
 static void Assert(bool ok,string message){checks++;if(!ok)throw new Exception(message);}

 sealed class CaptureProgress<T> : IProgress<T> {
  readonly Action<object> _capture;
  public CaptureProgress(Action<object> capture) {_capture=capture;}
  public void Report(T value) {_capture(value);}
 }

 static void CheckPublicProxyScan() {
  var assembly=typeof(LocalProxyBridge).Assembly;
  var scan=assembly.GetType("DevProxyApp.PublicProxyScan");
  var probeType=assembly.GetType("DevProxyApp.PublicProxyProbe");
  var search=assembly.GetType("DevProxyApp.PublicProxyCandidates").GetMethod("Scan",BindingFlags.Static|BindingFlags.NonPublic);
  var members=BindingFlags.Instance|BindingFlags.NonPublic;
  var snapshots=new List<object>();
  var progress=Activator.CreateInstance(typeof(CaptureProgress<>).MakeGenericType(scan),new object[]{new Action<object>(snapshots.Add)});
  var catalog=Enumerable.Range(1,15).Select(i=>new Uri("http://192.0.2."+i+":8080")).ToArray();
  Func<object,string,int> count=(value,name)=>(int)scan.GetField(name,members).GetValue(value);
  Func<bool,string,object> answer=(responded,failure)=>Activator.CreateInstance(probeType,members,null,new object[]{responded,failure},null);
  var candidate=Expression.Parameter(typeof(Uri));
  Func<Func<Uri,object>,Delegate> verifier=run=>Expression.Lambda(
   typeof(Func<,>).MakeGenericType(typeof(Uri),probeType),
   Expression.Convert(Expression.Invoke(Expression.Constant(run),candidate),probeType),candidate).Compile();
  var probe=verifier(uri=>{
   if(uri==catalog[12])throw new TimeoutException();
   return answer(true,uri==catalog[13]?null:"прокси ответил HTTP 403");
  });
  var result=search.Invoke(null,new object[]{catalog,12,probe,progress});
  Assert(snapshots.Select(s=>count(s,"Checked")).SequenceEqual(new[]{0,1,2,3}),"Concurrent progress overwrote an earlier result");
  Assert(count(result,"Total")==15&&count(result,"Checked")==3&&count(result,"Passed")==1&&count(result,"Responded")==2,
   "Skipped catalog entries counted as failures");
  Assert(count(result,"SelectedNumber")==14&&
   (string)scan.GetProperty("Proxy",members).GetValue(result,null)=="http://192.0.2.14:8080","Wrong number for later batch winner");
  string report=(string)scan.GetMethod("Details",members).Invoke(result,new object[]{14});
  Assert(report.Contains("№13 — истёк тайм-аут")&&report.Contains("№14 — прошёл, подключён")&&report.Contains("№15 — прокси ответил HTTP 403")&&
   !report.Contains("192.0.2."),"Report misstated failures or exposed addresses");
  var firstBatch=search.Invoke(null,new object[]{catalog,0,verifier(_=>answer(false,"нет ответа")),null});
  Assert(count(firstBatch,"Checked")==12&&count(firstBatch,"SelectedNumber")==0&&count(firstBatch,"Responded")==0,
   "Batch limit or empty result handling broke");
  var alternatives=search.Invoke(null,new object[]{catalog,12,verifier(_=>answer(true,null)),null});
  var other=(IEnumerable<int>)scan.GetMethod("OtherPassedNumbers",members).Invoke(alternatives,null);
  Assert(other.Concat(new[]{count(alternatives,"SelectedNumber")}).OrderBy(i=>i).SequenceEqual(new[]{13,14,15}),
   "Verified public alternatives were lost");
  var server=new TcpListener(IPAddress.Loopback,0);
  server.Start();
  var worker=new Thread(()=>{
   try {
    using(var peer=server.AcceptTcpClient()) {
     var stream=peer.GetStream();
     stream.ReadTimeout=3000;
     Header(stream);
     byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
     stream.Write(reply,0,reply.Length);
    }
   } catch(IOException) {}
   catch(SocketException) {}
  }){IsBackground=true};
  worker.Start();
  try {
   var upstream=new Uri("http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port);
   var check=assembly.GetType("DevProxyApp.AutomaticAiRoute").GetMethod("ProbePublic",BindingFlags.Static|BindingFlags.NonPublic);
   var refusal=check.Invoke(null,new object[]{upstream,"Codex"});
   Assert((bool)probeType.GetField("Responded",members).GetValue(refusal)&&probeType.GetField("Failure",members).GetValue(refusal)!=null,
    "The bridge reported an actual upstream 403 as no response or a usable route");
  } finally {server.Stop();worker.Join(1000);}
 }

 static void CheckAntigravityModelFeedback() {
  var assembly=typeof(LocalProxyBridge).Assembly;
  var parser=assembly.GetType("DevProxyApp.AntigravityModelLog");
  var flags=BindingFlags.Instance|BindingFlags.NonPublic;
  string root=Path.Combine(Path.GetTempPath(),"antigravity-feedback-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  string first=Path.Combine(root,"first.log"),second=Path.Combine(root,"second.log");
  DateTime since=DateTime.Now.AddSeconds(-10);
  var stamp=since;
  Func<string,string> line=message=>{
   stamp=stamp.AddMilliseconds(10);
   return "I"+stamp.ToString("MMdd HH:mm:ss.ffffff",System.Globalization.CultureInfo.InvariantCulture)+
    "   3 executor.go:41] "+message+"\n";
  };
  const string region="FAILED_PRECONDITION (code 400): User location is not supported for the API use.";
  const string reply="URL: https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse Trace: test ResponseID: fixture";
  try {
   File.WriteAllText(first,line(region)+line(reply).TrimEnd('\n'));
   var reader=parser.GetConstructor(flags,null,new[]{typeof(Func<IEnumerable<string>>),typeof(DateTime)},null)
    .Invoke(new object[]{new Func<IEnumerable<string>>(()=>Directory.EnumerateFiles(root)),since});
   var poll=parser.GetMethod("ReadNew",flags);
   Func<string> outcome=()=>poll.Invoke(reader,null).ToString();
   Assert(outcome()=="None","Pre-launch model events were replayed");
   File.AppendAllText(first,"\n"+line("FAILED_PRECONDITION (code 400): invalid request")+line(
     "URL: https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist Trace: test ResponseID: fixture"));
   Assert(outcome()=="None","Partial pre-launch event or non-generation endpoint became evidence");
   File.AppendAllText(first,line(region).TrimEnd('\n'));
   Assert(outcome()=="None","A partial new line triggered recovery");
   File.AppendAllText(first,"\n");
   Assert(outcome()=="RegionRejected","An explicit regional API rejection was missed");
   Assert(outcome()=="None","Rereading an unchanged file repeated a rejection");
   File.WriteAllText(second,line(reply));
   Assert(outcome()=="Streaming","A genuine generation start in a new log was missed");
   File.AppendAllText(second,line("Your current account is not eligible for Antigravity, because it is not currently available in your location."));
   Assert(outcome()=="AccountRejected","Account eligibility was treated as a regional route outcome");
   File.WriteAllText(first,line(reply));
   Assert(outcome()=="Streaming","A truncated log lost the new model result");
   File.AppendAllText(first,line("403 PERMISSION_DENIED {\"reason\": \"UNSUPPORTED_LOCATION\"}"));
   Assert(outcome()=="RegionRejected","An explicit 403 region rejection was missed");
   File.AppendAllText(first,line("403 PERMISSION_DENIED {\"reason\": \"PERMISSION_DENIED\"}"));
   Assert(outcome()=="None","Generic permissions error triggered route recovery");
   File.AppendAllText(first,line("Your location is not eligible for this service at this time. Detected location: RU"));
   Assert(outcome()=="RegionRejected","Current Google regional denial was not recognized");
  } finally {Directory.Delete(root,true);}
  using(var bridge=new LocalProxyBridge()) {
   var note=typeof(LocalProxyBridge).GetMethod("NoteAntigravityApiConnection",flags);
   var peers=typeof(LocalProxyBridge).GetMethod("AntigravityApiPeers",flags);
   var relay=IPAddress.Parse("192.0.2.12");
   var source=new DnsEndpoint("dagami","192.0.2.53","dns.dagami.ru");
   note.Invoke(bridge,new object[]{"daily-cloudcode-pa.googleapis.com:443",101,null,null});
   note.Invoke(bridge,new object[]{"accounts.google.com:443",202,relay,source});
   note.Invoke(bridge,new object[]{"cloudcode-pa.googleapis.com:443",303,relay,source});
   Assert(((IList)peers.Invoke(bridge,new object[]{1L,new HashSet<int>{101,202}})).Count==0,
    "Unrelated or pre-launch connections were attributed to the active session");
   var owned=(IList)peers.Invoke(bridge,new object[]{1L,new HashSet<int>{303}});
   Assert(owned.Count==1&&relay.Equals(owned[0].GetType().GetField("SmartDnsAddress",flags).GetValue(owned[0])),
    "The session's own model relay address was not attributed to it");
   Assert(ReferenceEquals(source,owned[0].GetType().GetField("DnsSource",flags).GetValue(owned[0])),
    "Provider provenance was lost after CONNECT");
  }
 }

 static void CheckAutomaticAiRoutes() {
  var assembly=typeof(LocalProxyBridge).Assembly;
  var route=assembly.GetType("DevProxyApp.AiRoute");
  var chooser=assembly.GetType("DevProxyApp.AutomaticAiRoute");
  var flags=BindingFlags.Static|BindingFlags.NonPublic;
  var fields=BindingFlags.Instance|BindingFlags.NonPublic;
  var candidates=chooser.GetMethod("Candidates",flags);
  var select=chooser.GetMethod("Select",flags);
  var gate=chooser.GetMethod("UnexpectedDnsProvider",flags);
  var actual=new[]{new DnsEndpoint("dagami","192.0.2.53","dns.dagami.ru")};
  Assert((string)gate.Invoke(null,new object[]{"dagami",actual})==null,"Selected DNS provider was rejected");
  Assert(((string)gate.Invoke(null,new object[]{"geohide",actual})).Contains("Dagami"),"Backup DNS was credited to the selected provider");
  Assert(gate.Invoke(null,new object[]{"geohide",new DnsEndpoint[]{null}})!=null,"Upstream fallback was credited to selected Smart DNS");
  var name=route.GetField("Name",fields);
  var dns=route.GetField("Dns",fields);
  var proxy=route.GetField("Proxy",fields);
  var publicFlag=route.GetField("PublicProxy",fields);
  var parameter=Expression.Parameter(route);
  Func<Func<object,string>,Delegate> probe=behavior=>Expression.Lambda(
   typeof(Func<,>).MakeGenericType(route,typeof(string)),
   Expression.Invoke(Expression.Constant(behavior),Expression.Convert(parameter,typeof(object))),parameter).Compile();
  var plan=(IList)candidates.Invoke(null,new object[]{"Antigravity",null,true,"http://127.0.0.1:1234","dns.dagami.ru",true});
  Assert((string)proxy.GetValue(plan[0])==ManagedAccess.Profile,"Automatic routing did not prefer the linked gateway");
  int dnsDefaults=0;
  object combo=null;
  foreach(object item in plan) {
   if((string)dns.GetValue(item)=="dns.dagami.ru"&&proxy.GetValue(item)==null)dnsDefaults++;
   if(proxy.GetValue(item)!=null&&dns.GetValue(item)!=null)combo=item;
  }
  Assert(dnsDefaults==1&&combo!=null,"DNS deduplication or combined route is missing");
  var background=chooser.GetMethod("BackgroundAntigravityCandidates",flags);
  var proxyRecovery=(IList)background.Invoke(null,new object[]{plan,plan[0],true,false,"dns.dagami.ru"});
  Assert(proxyRecovery.Count==1&&(string)proxy.GetValue(proxyRecovery[0])=="http://127.0.0.1:1234",
   "Background recovery reused a refused route or enabled an unselected DNS or paid pool");
  var dnsRecovery=(IList)background.Invoke(null,new object[]{plan,null,false,true,"dns.geohide.ru"});
  Assert(dnsRecovery.Count==3&&dnsRecovery.Cast<object>().All(item=>proxy.GetValue(item)==null)&&
   dnsRecovery.Cast<object>().Any(item=>(string)dns.GetValue(item)=="dns.geohide.ru"),
   "Background recovery ignored the saved DNS or tried an unselected provider");
  var paid=plan.Cast<object>().Single(item=>(string)proxy.GetValue(item)==ManagedAccess.PaidProfile);
  Assert(((IList)background.Invoke(null,new object[]{plan,paid,true,true,"dns.dagami.ru"})).Count==0,
   "Background recovery escaped the selected paid pool");
  var routeHistoryType=assembly.GetType("DevProxyApp.AntigravityRouteHistory");
  var modelOutcome=assembly.GetType("DevProxyApp.AntigravityModelOutcome");
  using(var session=new NativeSession()) {
   var sessionType=session.GetType();
   sessionType.GetField("ModelRoute",fields).SetValue(session,combo);
   sessionType.GetField("ModelRouteObserved",fields).SetValue(session,true);
   sessionType.GetField("ModelOutcome",fields).SetValue(session,Enum.Parse(modelOutcome,"RegionRejected"));
   var summary=(string)routeHistoryType.GetMethod("DiagnosticSummary",flags).Invoke(null,new object[]{session,false});
   Assert(summary.Contains("Подготовка клиента")&&summary.Contains("Маршрут API: свой прокси")&&
    summary.Contains("региональный отказ")&&!summary.Contains("127.0.0.1:1234"),"Diagnostic summary conflated the outcome and route or leaked the proxy address");
  }
  var history=Activator.CreateInstance(routeHistoryType,true);
  var record=routeHistoryType.GetMethod("Record",fields);
  var prioritize=routeHistoryType.GetMethod("Prioritize",fields);
  var refusedAddresses=routeHistoryType.GetMethod("RefusedSmartDnsAddresses",fields);
  DateTime now=DateTime.UtcNow;
  var sameRelay=IPAddress.Parse("192.0.2.85");
  var newRelay=IPAddress.Parse("198.51.100.9");
  record.Invoke(history,new[]{plan[0],Enum.Parse(modelOutcome,"RegionRejected"),now,new[]{sameRelay}});
  record.Invoke(history,new[]{combo,Enum.Parse(modelOutcome,"Streaming"),now,new[]{newRelay}});
  record.Invoke(history,new[]{combo,Enum.Parse(modelOutcome,"AccountRejected"),now,new[]{newRelay}});
  var refused=(HashSet<IPAddress>)refusedAddresses.Invoke(history,new object[]{now});
  Assert(refused.SetEquals(new[]{sameRelay}),"Recovery did not remember the actual rejected Smart DNS relay");
  var refusal=chooser.GetMethod("RefusedSmartDnsAddress",flags);
  Assert(((string)refusal.Invoke(null,new object[]{new[]{sameRelay},refused})).Contains(sameRelay.ToString()),
   "A differently named DNS profile on the same relay passed recovery");
  Assert(refusal.Invoke(null,new object[]{new[]{newRelay,null},refused})==null,"A distinct relay or direct connection was rejected");
  Assert(((HashSet<IPAddress>)refusedAddresses.Invoke(history,new object[]{now.AddMinutes(6)})).Count==0,
   "A relay remained banned after the cooldown");
  var excluded=new List<string>();
  var recovery=(IList)prioritize.Invoke(history,new object[]{plan,null,now,excluded});
  Assert(recovery[0]==combo&&!recovery.Contains(plan[0])&&excluded.Count==1,
   "Successful model route was not preferred or recent rejection was retried");
  recovery=(IList)prioritize.Invoke(history,new object[]{plan,null,now.AddMinutes(6),new List<string>()});
  Assert(recovery.Contains(plan[0]),"A regional rejection permanently disabled the route");
  recovery=(IList)prioritize.Invoke(history,new object[]{plan,plan[0],now.AddMinutes(6),new List<string>()});
  Assert(!recovery.Contains(plan[0]),"Explicit retry returned to the reported route");
  Assert(plan.Cast<object>().Count(item=>(string)dns.GetValue(item)=="https://test-dns.dagami.ru/dns-query"&&proxy.GetValue(item)==null)==1,
   "Antigravity needs an independent Dagami DoH candidate");
  var nonGoogle=(IList)candidates.Invoke(null,new object[]{"Codex",null,false,"http://127.0.0.1:1234","dns.dagami.ru",false});
  Assert(nonGoogle.Count==1&&dns.GetValue(nonGoogle[0])==null,"Codex was credited with an unrelated DNS route");
  Assert(route.GetMethod("ProbeDns",fields).Invoke(combo,new object[]{"Gemini"})==null,"Gemini check ignores its proxy-only launcher");
  var chosen=select.Invoke(null,new object[]{plan,probe(candidate=>Object.ReferenceEquals(candidate,combo)?null:"HTTP 403"),
    new Func<string>(()=>{throw new Exception("Public source contacted with a verified private route");}),null});
  var winner=chosen.GetType().GetField("Route",fields);
  Assert(Object.ReferenceEquals(winner.GetValue(chosen),combo),"Failed route was chosen before verified alternative");
   var none=select.Invoke(null,new object[]{plan,probe(candidate=>"HTTP 429"),null,null});
  Assert(winner.GetValue(none)==null,"A throttled route was chosen with public fallback disabled");
  int fetches=0;
   var fallback=select.Invoke(null,new object[]{plan,probe(candidate=>"HTTP 502"),new Func<string>(()=>{fetches++;return "http://192.0.2.4:8080";}),null});
  Assert(fetches==1&&(bool)publicFlag.GetValue(winner.GetValue(fallback)),"Public fallback failed or ran more than once");
   var ready=select.Invoke(null,new object[]{plan,probe(candidate=>ReferenceEquals(candidate,plan[0])?"HTTP 403":null),null,null});
   var alternates=(IList)ready.GetType().GetField("Alternatives",fields).GetValue(ready);
   Assert(ReferenceEquals(winner.GetValue(ready),plan[1])&&alternates.Cast<object>().SequenceEqual(plan.Cast<object>().Skip(2)),
    "Verified alternatives were lost or ranked by completion order");
   var interrupted=select.Invoke(null,new object[]{plan,probe(candidate=>{
    if(ReferenceEquals(candidate,plan[0]))throw new IOException();
    return null;
   }),null,null});
   Assert(ReferenceEquals(winner.GetValue(interrupted),plan[1]),"One transient I/O failure stopped route selection");
   var fast=select.Invoke(null,new object[]{plan,probe(candidate=>{
    if(!ReferenceEquals(candidate,plan[0]))throw new Exception("Unnecessary route probe");
    return null;
   }),null,null});
   var fastProgress=fast.GetType().GetField("Progress",fields).GetValue(fast);
   Assert((int)fastProgress.GetType().GetField("Checked",fields).GetValue(fastProgress)==1,"First healthy route failed to skip other probes");
  var antigravityChecks=(Array)chooser.GetMethod("Checks",flags).Invoke(null,new object[]{"Antigravity"});
  var check=antigravityChecks.GetType().GetElementType();
  var byHost=antigravityChecks.Cast<object>().ToDictionary(item=>(string)check.GetField("Host",fields).GetValue(item));
  object login,token;
  Assert(byHost.TryGetValue("accounts.google.com",out login)&&
   ((string)check.GetField("Path",fields).GetValue(login)).StartsWith("/o/oauth2/v2/auth?"),"The browser OAuth endpoint is missing from preflight");
  Assert(byHost.TryGetValue("oauth2.googleapis.com",out token)&&
   (string)check.GetField("Path",fields).GetValue(token)=="/token"&&
   (string)check.GetField("ContentType",fields).GetValue(token)=="application/x-www-form-urlencoded"&&
   ((string)check.GetField("Body",fields).GetValue(token)).Contains("code=devproxy-invalid-diagnostic"),"The token endpoint must receive a synthetic form POST");
  var tokenStatus=(Func<int,bool>)check.GetField("Accept",fields).GetValue(token);
  Assert(tokenStatus(400)&&tokenStatus(401)&&!tokenStatus(403)&&!tokenStatus(502),"Unexpected OAuth token responses passed preflight");
  foreach(string host in new[]{"cloudcode-pa.googleapis.com","daily-cloudcode-pa.googleapis.com"}) {
   var model=byHost[host];
   var modelStatus=(Func<int,bool>)check.GetField("Accept",fields).GetValue(model);
   Assert((string)check.GetField("Path",fields).GetValue(model)=="/v1internal:streamGenerateContent?alt=sse"&&
    (string)check.GetField("Body",fields).GetValue(model)=="{}"&&modelStatus(401)&&!modelStatus(400)&&!modelStatus(403)&&!modelStatus(502),
    "Antigravity streaming preflight accepted a server error or omitted the synthetic model request");
  }
  object aiCode;
  Assert(byHost.TryGetValue("aicode.googleapis.com",out aiCode)&&(string)check.GetField("Path",fields).GetValue(aiCode)=="/",
   "The current CLI API host is missing from the transport preflight");
  var apiCodeStatus=(Func<int,bool>)check.GetField("Accept",fields).GetValue(aiCode);
  Assert(apiCodeStatus(404)&&!apiCodeStatus(400)&&!apiCodeStatus(429)&&!apiCodeStatus(502),"The aicode transport check accepted an API rejection");
  var redirect=chooser.GetMethod("IsGoogleLoginRedirect",flags);
  Assert((bool)redirect.Invoke(null,new object[]{302,"https://accounts.google.com/ServiceLogin"})&&
   !(bool)redirect.Invoke(null,new object[]{302,"https://accounts.google.com.example.test/"})&&
   !(bool)redirect.Invoke(null,new object[]{403,"https://accounts.google.com/ServiceLogin"}),"Region/error responses were accepted as Google login");
  var activate=chooser.GetMethod("StartAndVerify",flags);
  var steps=new List<string>();
  Func<int> start=()=>{steps.Add("start");return 53125;};
  Action save=()=>steps.Add("save");
  Func<int,Task<string>> verify=port=>{
   Assert(port==53125,"Activation tested a different bridge from the one it started");
   steps.Add("verify");
   return Task.FromResult<string>(null);
  };
  ((Task)activate.Invoke(null,new object[]{start,verify,save})).GetAwaiter().GetResult();
  Assert(string.Join(",",steps)=="start,verify,save","Automatic setup did not save only after starting and verifying the active bridge");
  steps.Clear();
  bool rejected=false;
  try {((Task)activate.Invoke(null,new object[]{start,new Func<int,Task<string>>(_=>Task.FromResult("HTTP 403")),save})).GetAwaiter().GetResult();}
  catch(IOException){rejected=true;}
  Assert(rejected&&!steps.Contains("save"),"A provisional bridge with an HTTP error was persisted");
  rejected=false;
  try {((Task)activate.Invoke(null,new object[]{new Func<int>(()=>0),verify,save})).GetAwaiter().GetResult();}
  catch(IOException){rejected=true;}
  Assert(rejected&&!steps.Contains("verify")&&!steps.Contains("save"),"A failed local bind was treated as an activated route");
 }
 static string Header(Stream stream){var result=new MemoryStream();int state=0;while(state<4){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();result.WriteByte((byte)b);state=(state==0||state==2)?(b==13?state+1:0):(b==10?state+1:0);if(result.Length>16384)throw new IOException("Header overflow");}return Encoding.ASCII.GetString(result.ToArray());}
 static void Probe(int status) {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception error=null;string request=null;int after=-2;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){peer.GetStream().ReadTimeout=4000;request=Header(peer.GetStream());byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" Fixture\r\nContent-Length: 0\r\n\r\n");peer.GetStream().Write(reply,0,reply.Length);after=peer.GetStream().ReadByte();}}catch(Exception e){error=e;}});worker.IsBackground=true;worker.Start();
  try {
   var result=UpstreamProxyDiagnostics.Check("http://fixture%40user:fixture%23password@127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port,3000);
   Assert(result.Status==status&&result.Available==(status==200),"TCP-only false positive or wrong CONNECT status");
   Assert(worker.Join(5000)&&error==null,"Proxy fixture failed");
   Assert(request.StartsWith("CONNECT www.googleapis.com:443 HTTP/1.1"),"Probe bypassed upstream");
   Assert(request.Contains("Proxy-Authorization: Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture@user:fixture#password"))),"Escaped credentials encoded incorrectly");
   Assert(after==-1,"Probe sent account/application bytes after CONNECT");
   Assert(!result.Message.Contains("fixture#password")&&!result.Message.Contains("fixture@user"),"Credentials exposed by diagnostics");
   if(status==407)Assert(result.Message.Contains("407")&&result.Message.Contains("пароль"),"407 has no recovery guidance");
   if(status==403||status==503)Assert(result.Message.Contains("CONNECT")&&result.Message.Contains(status.ToString()),"Upstream refusal confused with model/account response");
  }finally{server.Stop();worker.Join(1000);}
 }
 static void MetadataHostIsExact(){
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();string received=null;Exception failure=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){peer.GetStream().ReadTimeout=5000;received=Header(peer.GetStream());byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");peer.GetStream().Write(response,0,response.Length);}}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"Metadata fixture bridge failed");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
    var stream=client.GetStream();stream.ReadTimeout=5000;
    byte[] request=Encoding.ASCII.GetBytes("GET "+SelectiveProxyRules.Path+" HTTP/1.1\r\nHost: 127.0.0.1:"+bridge.BoundPort+"\r\n\r\n");
    stream.Write(request,0,request.Length);
    Assert(Header(stream).Contains(" 200 "),"PAC unavailable for a user-selected direct/custom route");
   }
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes("GET http://metadata.google.internal/computeMetadata/v1/ HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");c.GetStream().Write(b,0,b.Length);Assert(Header(c.GetStream()).Contains(" 404 "),"Metadata endpoint not intercepted");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;var stream=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("CONNECT metadata.google.internal:80 HTTP/1.1\r\nHost: metadata.google.internal:80\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 200 "),"Metadata CONNECT tunnel not established");b=Encoding.ASCII.GetBytes("GET /computeMetadata/v1/ HTTP/1.1\r\nHost: metadata.google.internal\r\nMetadata-Flavor: Google\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 404 "),"Metadata CONNECT request did not receive 404");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;var stream=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("CONNECT metadata.google.internal:80 HTTP/1.1\r\nHost: metadata.google.internal:80\r\n\r\nGET / HTTP/1.1\r\nHost: metadata.google.internal\r\n\r\n");stream.Write(b,0,b.Length);Assert(Header(stream).Contains(" 200 ")&&Header(stream).Contains(" 404 "),"Pipelined metadata CONNECT request lost bytes");}
   using(var c=new TcpClient("127.0.0.1",bridge.BoundPort)){c.GetStream().ReadTimeout=5000;byte[] b=Encoding.ASCII.GetBytes("GET http://example.test/metadata.google.internal HTTP/1.1\r\nHost: example.test\r\n\r\n");c.GetStream().Write(b,0,b.Length);Assert(Header(c.GetStream()).Contains(" 204 "),"Metadata substring intercepted unrelated request");}
   Assert(worker.Join(5000)&&failure==null&&received.StartsWith("GET http://example.test/"),"Unrelated request did not reach configured proxy");
  }}finally{server.Stop();worker.Join(1000);}
 }
 static void SocksBridge(bool rejectAuth) {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception failure=null;string observedHost=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){var stream=peer.GetStream();stream.ReadTimeout=4000;
   byte[] greeting=new byte[3];for(int i=0;i<greeting.Length;i++)greeting[i]=(byte)stream.ReadByte();if(greeting[0]!=5||greeting[1]!=1||greeting[2]!=2)throw new Exception("SOCKS auth was not mandatory");
   stream.Write(new byte[]{5,2},0,2);int version=stream.ReadByte(),ulen=stream.ReadByte();byte[] u=new byte[ulen];for(int i=0;i<ulen;i++)u[i]=(byte)stream.ReadByte();int plen=stream.ReadByte();byte[] pass=new byte[plen];for(int i=0;i<plen;i++)pass[i]=(byte)stream.ReadByte();
   if(version!=1||Encoding.UTF8.GetString(u)!="fixture@user"||Encoding.UTF8.GetString(pass)!="fixture#password")throw new Exception("SOCKS credentials incorrect");
   stream.Write(new byte[]{1,(byte)(rejectAuth?1:0)},0,2);if(rejectAuth)return;
   byte[] h=new byte[4];for(int i=0;i<4;i++)h[i]=(byte)stream.ReadByte();if(h[0]!=5||h[1]!=1||h[3]!=3)throw new Exception("SOCKS request invalid");
   int n=stream.ReadByte();byte[] host=new byte[n];for(int i=0;i<n;i++)host[i]=(byte)stream.ReadByte();observedHost=Encoding.ASCII.GetString(host);int hi=stream.ReadByte(),lo=stream.ReadByte();if(hi*256+lo!=443)throw new Exception("Wrong SOCKS port");
   byte[] answer={5,0,0,1,127,0,0,1,0,1};stream.Write(answer,0,answer.Length);byte[] data=new byte[4];for(int i=0;i<4;i++)data[i]=(byte)stream.ReadByte();stream.Write(data,0,data.Length);
  }}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try{using(var bridge=new LocalProxyBridge()){Assert(bridge.Start(0,"socks5://fixture%40user:fixture%23password@127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"SOCKS bridge failed to start");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=5000;var stream=client.GetStream();byte[] req=Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");stream.Write(req,0,req.Length);string header=Header(stream);
    Assert(header.Contains(rejectAuth?" 502 ":" 200 "),"Unexpected SOCKS bridge response");if(!rejectAuth){byte[] ping=Encoding.ASCII.GetBytes("PING");stream.Write(ping,0,ping.Length);for(int i=0;i<4;i++)Assert(stream.ReadByte()==ping[i],"SOCKS relay lost data");}
   }
   Assert(worker.Join(5000)&&failure==null,"SOCKS fixture failed: "+failure);if(!rejectAuth)Assert(observedHost=="example.test","SOCKS hostname was resolved locally");
  }}finally{server.Stop();worker.Join(1000);}
 }
 sealed class SocksFixtureStream:Stream {
  readonly MemoryStream input;public readonly MemoryStream output=new MemoryStream();public SocksFixtureStream(byte[] response){input=new MemoryStream(response);}
  public override bool CanRead{get{return true;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}
  public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
  public override int Read(byte[] b,int o,int n){return input.Read(b,o,Math.Min(n,1));}public override void Write(byte[] b,int o,int n){output.Write(b,o,n);}public override void Flush(){}
  public override long Seek(long o,SeekOrigin p){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}
 }
 static void SocksProtocolFixtures(){
  var proxy=new Uri("socks5://127.0.0.1:1080");
  Uri parsed;
  Assert(UpstreamProxyDiagnostics.TryParse("socks4a://id@proxy.example",out parsed)&&parsed.Port==1080,"SOCKS4a default port not accepted");
  var socks4a=new SocksFixtureStream(new byte[]{0,90,0,0,0,0,0,0});
  var methods=BindingFlags.Static|BindingFlags.NonPublic;
  typeof(UpstreamProxyDiagnostics).GetMethod("ConnectSocks",methods).Invoke(null,new object[]{socks4a,parsed,"example.test",443});
  Assert(socks4a.output.ToArray().SequenceEqual(new byte[]{4,1,1,187,0,0,0,1}.Concat(Encoding.ASCII.GetBytes("id\0example.test\0"))),"SOCKS4a hostname or user ID not relayed correctly");
  Assert(!UpstreamProxyDiagnostics.TryParse("socks4://u:pass@proxy.example:1080",out parsed),"SOCKS4 accepted unsupported password");
  Assert(!UpstreamProxyDiagnostics.TryParse("socks5s://proxy.example:/",out parsed),"SOCKS5 accepted empty port");
  Assert(UpstreamProxyDiagnostics.TryParse("socks5s://proxy.example",out parsed)&&parsed.Port==1080&&
   (bool)typeof(UpstreamProxyDiagnostics).GetMethod("UsesTls",methods).Invoke(null,new object[]{parsed}),"SOCKS5 over TLS requires TLS and a valid default port");
  foreach(byte[] response in new[]{new byte[]{4,0},new byte[]{5,255},new byte[]{5,0,5,1,0,1},new byte[]{5,0,5,0,1,1},new byte[]{5,0,5,0,0,99},new byte[]{5,0,5,0,0,1,127}}){
   bool rejected=false;try{Socks5Transport.Connect(new SocksFixtureStream(response),proxy,"example.test",443);}catch(IOException){rejected=true;}Assert(rejected,"Malformed or failed SOCKS response accepted");
  }
  var ipv6=new SocksFixtureStream(new byte[]{5,0,5,0,0,1,127,0,0,1,0,1});Socks5Transport.Connect(ipv6,proxy,"2001:db8::1",443);byte[] sent=ipv6.output.ToArray();
  Assert(sent.Length==25&&sent[6]==4&&sent[7]==0x20&&sent[8]==0x01&&sent[23]==1&&sent[24]==0xbb,"IPv6 SOCKS address encoded incorrectly");
  var downgrade=new SocksFixtureStream(new byte[]{5,0});bool denied=false;try{Socks5Transport.Connect(downgrade,new Uri("socks5://user:pass@127.0.0.1:1080"),"example.test",443);}catch(IOException){denied=true;}Assert(denied,"SOCKS authentication downgrade accepted");
 }
 static void SocksHttp() {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception failure=null;string received=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()){var stream=peer.GetStream();stream.ReadTimeout=4000;
   byte[] greeting=new byte[3];for(int i=0;i<3;i++)greeting[i]=(byte)stream.ReadByte();if(greeting[0]!=5||greeting[1]!=1||greeting[2]!=0)throw new Exception("Unexpected no-auth greeting");
   stream.Write(new byte[]{5,0},0,2);byte[] h=new byte[4];for(int i=0;i<4;i++)h[i]=(byte)stream.ReadByte();if(h[0]!=5||h[1]!=1||h[3]!=3)throw new Exception("Bad request");
   int n=stream.ReadByte();byte[] host=new byte[n];for(int i=0;i<n;i++)host[i]=(byte)stream.ReadByte();int high=stream.ReadByte(),low=stream.ReadByte();if(Encoding.ASCII.GetString(host)!="example.test"||high*256+low!=8080)throw new Exception("Wrong destination");
   byte[] response={5,0,0,1,127,0,0,1,0,1};stream.Write(response,0,response.Length);received=Header(stream);byte[] ok=Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");stream.Write(ok,0,ok.Length);
  }}catch(Exception e){failure=e;}});worker.IsBackground=true;worker.Start();
  try {using(var bridge=new LocalProxyBridge()){Assert(bridge.Start(0,"socks5://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"SOCKS HTTP bridge failed");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){client.GetStream().ReadTimeout=5000;byte[] req=Encoding.ASCII.GetBytes("GET http://example.test:8080/hello?q=1 HTTP/1.1\r\nHost: example.test:8080\r\nProxy-Authorization: Basic should-not-leak\r\nConnection: close\r\n\r\n");client.GetStream().Write(req,0,req.Length);Assert(Header(client.GetStream()).Contains(" 204 "),"SOCKS HTTP request failed");}
   Assert(worker.Join(5000)&&failure==null,"SOCKS HTTP fixture failed: "+failure);Assert(received.StartsWith("GET /hello?q=1 HTTP/1.1")&&!received.Contains("should-not-leak")&&!received.Contains("Proxy-Authorization"),"SOCKS HTTP sent wrong request or leaked credentials");
  }}finally{server.Stop();worker.Join(1000);}
 }
 static int Main(){try {
  CheckAntigravityModelFeedback();
  CheckAutomaticAiRoutes();
  CheckPublicProxyScan();
  string builtInPac=SelectiveProxyRules.BuiltInPac(53200);
  Assert(builtInPac.Contains("gemini.google.com")&&builtInPac.Contains("ttvnw.net")&&!builtInPac.Contains("test-dns.dagami.ru"),"Packaged PAC rules are missing or route the gateway");
  Assert(!builtInPac.Contains("PROXY 127.0.0.1:53200; DIRECT"),"Browser fallback leaked into Windows PAC");
  string extension=Path.Combine(Path.GetTempPath(),"dagami-extension-"+Guid.NewGuid().ToString("N"));
  try {
   var export=typeof(SelectiveProxyRules).GetMethod("ExportBrowserExtension",BindingFlags.Static|BindingFlags.NonPublic);
   export.Invoke(null,new object[]{extension,54321});
   foreach(string name in new[]{"manifest.json","popup.html","popup.js","config.js","route.js","background.js"})
    Assert(File.Exists(Path.Combine(extension,name)),"Missing extension file: "+name);
   string config=File.ReadAllText(Path.Combine(extension,"config.js"));
   Assert(config.Contains("\"port\":54321")&&config.Contains("PROXY 127.0.0.1:54321; DIRECT")&&config.Contains("gemini.google.com"),
    "Exported extension lacks the selected port, rules or direct fallback");
   export.Invoke(null,new object[]{extension,54322});
   string updated=File.ReadAllText(Path.Combine(extension,"config.js"));
   Assert(updated.Contains("PROXY 127.0.0.1:54322; DIRECT")&&!updated.Contains("54321; DIRECT"),
    "Updating the exported extension retained the old bridge port");
  } finally { if(Directory.Exists(extension))Directory.Delete(extension,true); }
  string pac=SelectiveProxyRules.GeneratePac(new[]{"exact.example",".sub.example"},53200);
  Assert(pac.Contains("host === \"exact.example\"")&&pac.Contains("dnsDomainIs(host, \".sub.example\")")&&pac.Contains("PROXY 127.0.0.1:53200")&&pac.Contains("return \"DIRECT\""),"Selective PAC rules invalid");
  foreach(string bad in new[]{"evil.example\";alert(1)",".com/other","localhost\nreturn DIRECT"}){bool denied=false;try{SelectiveProxyRules.GeneratePac(new[]{bad},53200);}catch(InvalidDataException){denied=true;}Assert(denied,"Unsafe PAC input accepted");}
  FailedProxyMustNotConnectDirectly();
  SocksBridge(false);SocksBridge(true);SocksHttp();SocksProtocolFixtures();
  foreach(int status in new[]{200,407,403,503})Probe(status);
  MetadataHostIsExact();
  Assert(UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru:50128"),"Known DNS endpoint not identified");
  Assert(!UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru:pw@proxy.example:8080"),"Username mistaken for DNS proxy host");
  Assert(!UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy("http://dns.dagami.ru.evil.example:8080"),"DNS hostname substring matched");
  var destination=(Uri)typeof(LocalProxyBridge).GetMethod("HttpDestination",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"GET","http://example.test:8080/a?q=1","GET http://example.test:8080/a?q=1 HTTP/1.1\r\nHost: example.test\r\nProxy-Authorization: Basic secret\r\n\r\n"});
  string direct=HttpWire.RequestHeaders("GET http://example.test:8080/a?q=1 HTTP/1.1\r\nHost: example.test\r\nProxy-Authorization: Basic secret\r\n\r\n",destination,false,null);
  Assert(destination.Host=="example.test"&&destination.Port==8080&&direct.StartsWith("GET /a?q=1 HTTP/1.1")&&!direct.Contains("secret")&&!direct.Contains("Proxy-Authorization"),"Direct request leaked proxy credentials or used wrong target");
  foreach(string bad in new[]{"http://user:bad#password@127.0.0.1:1","http://127.0.0.1:1/path","http://127.0.0.1:1/?token=fixture"}) {
   var result=UpstreamProxyDiagnostics.Check(bad);Assert(!result.Available&&result.Status==0,"Malformed proxy URL accepted");
  }
  var occupied=new List<TcpListener>();int preferred=0;
  for(int candidate=54000;candidate<55500;candidate+=10) {
   try{for(int port=candidate;port<candidate+10;port++){var listener=new TcpListener(IPAddress.Loopback,port);listener.ExclusiveAddressUse=true;listener.Start();occupied.Add(listener);}preferred=candidate;break;}
   catch(SocketException){foreach(var listener in occupied)listener.Stop();occupied.Clear();}
  }
  Assert(preferred>0,"Cannot reserve fixture port range");
  try{using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(preferred,"http://127.0.0.1:1"),"Ten occupied ports prevent bridge startup");
   Assert(bridge.BoundPort>0&&(bridge.BoundPort<preferred||bridge.BoundPort>=preferred+10),"Ephemeral fallback not used");
  }}finally{foreach(var listener in occupied)listener.Stop();}
  using(var bridge=new LocalProxyBridge()){
   Assert(bridge.Start(preferred,"http://127.0.0.1:"+preferred),"Self-upstream fixture startup failed");
   Assert(bridge.BoundPort!=preferred,"Bridge selected itself as its upstream");
  }
  int calls=0;
  string report=AntigravityDiagnostics.TokenRouteReport(12345,true,null,"http://127.0.0.1:1",port=>{calls++;if(port==12345)throw new TimeoutException("fixture");return "synthetic POST expected 401";});
  Assert(calls==2&&report.Contains("upstream без Smart DNS")&&report.Contains("expected 401"),"OAuth timeout did not compare synthetic routes");
  calls=0;report=AntigravityDiagnostics.TokenRouteReport(12345,true,null,"",port=>{calls++;throw new TimeoutException("fixture");});
  Assert(calls==1&&report.Contains("Сравнение недоступно"),"Missing comparison settings not handled");
  Console.WriteLine("PASS "+checks+" CONNECT auth/status, URL validation, port fallback, self-upstream and synthetic OAuth comparison checks");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}

 sealed class NoDirectBridge:LocalProxyBridge {
  public int DirectCalls;
  protected override TcpClient ConnectDirect(string host,int port,out Stream stream){
   Interlocked.Increment(ref DirectCalls);stream=null;throw new IOException("Fixture intercepted forbidden direct route before DNS/network");
  }
 }
 static void FailedProxyMustNotConnectDirectly(){
  using(var unavailable=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)){
   unavailable.Bind(new IPEndPoint(IPAddress.Loopback,0));
   using(var bridge=new NoDirectBridge()){
    Assert(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)unavailable.LocalEndPoint).Port),"Failure fixture bridge did not start");
    // Resolver is enabled, but the unscoped host must never be resolved/dialled.
    typeof(LocalProxyBridge).GetField("_scopedDns",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bridge,new ScopedDnsResolver("192.0.2.53"));
    foreach(string method in new[]{"CONNECT","GET"})using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)){
     client.GetStream().ReadTimeout=5000;string authority="outside.fixture.invalid:443";
     string request=method+" "+(method=="CONNECT"?authority:"http://outside.fixture.invalid/fixture")+" HTTP/1.1\r\nHost: "+authority+"\r\n\r\n";
     byte[] bytes=Encoding.ASCII.GetBytes(request);client.GetStream().Write(bytes,0,bytes.Length);
     string reply=Header(client.GetStream());
     Assert(reply.Contains(" 502 ")&&reply.Contains("X-Dagami-Failure-Phase: upstream-connect"),"Proxy failure was not reported at the selected route");
     Assert(bridge.DirectCalls==0,"Failed configured proxy silently attempted direct "+method);
    }
   }
  }
 }
}
