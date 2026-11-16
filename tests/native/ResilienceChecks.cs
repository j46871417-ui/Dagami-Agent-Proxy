// Review-only fixture. Loopback sockets and a dedicated temporary config only.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using DevProxyApp;

class ResilienceChecks {
 static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
 static void Require(bool ok,string message) { if(!ok)throw new Exception(message); }
 static ConfigManager FixtureConfig(string root) {
  Directory.CreateDirectory(root);
  var config=new ConfigManager();
  foreach(string name in new[]{"configDir","localConfigDir"})typeof(ConfigManager).GetField(name,Private).SetValue(config,root);
  foreach(string name in new[]{"configFile","localConfigFile"})typeof(ConfigManager).GetField(name,Private).SetValue(config,Path.Combine(root,"config.json"));
  config.LastProxy="direct";config.UseDagamiDns=false;config.DnsObfuscation=false;
  return config;
 }
 static string ReadHeader(Stream stream) {
  var bytes=new MemoryStream();
  while(bytes.Length<16384) {
   int value=stream.ReadByte();if(value<0)throw new EndOfStreamException();bytes.WriteByte((byte)value);
   string text=Encoding.ASCII.GetString(bytes.ToArray());if(text.EndsWith("\r\n\r\n"))return text;
  }
  throw new IOException("Oversize fixture header");
 }
 static void UnsavedBaseline(string root) {
  var config=FixtureConfig(Path.Combine(root,"locked-config"));Require(config.Save(),"Initial fixture save failed");
  string path=Path.Combine(root,"locked-config","config.json"), before=File.ReadAllText(path);
  using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
   // Reads only a deliberately absent environment name; does not write registry/environment.
   bool rejected=false;try {config.CaptureEnvironmentBackup(new[]{"DAGAMI_REVIEW_ABSENT_"+Guid.NewGuid().ToString("N")});}catch(IOException){rejected=true;}
   bool failed=!string.IsNullOrEmpty(config.LastSaveError);
   Require(failed&&rejected&&config.EnvironmentBackup.Count==0&&File.ReadAllText(path)==before,"Unsaved-baseline scenario changed unexpectedly");
   Console.WriteLine("PASS: failed durable backup prevents the environment operation.");
  }
 }
 static void HalfCloseLease() {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception workerError=null;
  var worker=new Thread(()=>{
   try {using(var peer=server.AcceptTcpClient()) {
    peer.GetStream().ReadTimeout=5000;ReadHeader(peer.GetStream());
    byte[] reply=Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");peer.GetStream().Write(reply,0,reply.Length);
    // A completed upstream connection. The client keeps its upload side open.
   }}catch(Exception error){workerError=error;}
  });worker.IsBackground=true;worker.Start();
  try {using(var bridge=new LocalProxyBridge()) {
   Require(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"Fixture bridge failed");
   using(var client=new TcpClient()) {
    client.Connect(IPAddress.Loopback,bridge.BoundPort);client.GetStream().ReadTimeout=15000;
    byte[] request=Encoding.ASCII.GetBytes("CONNECT fixture.invalid:443 HTTP/1.1\r\nHost: fixture.invalid:443\r\n\r\n");client.GetStream().Write(request,0,request.Length);
    Require(ReadHeader(client.GetStream()).StartsWith("HTTP/1.1 200"),"Fixture CONNECT failed");
    Require(client.GetStream().ReadByte()==-1,"Upstream completion not forwarded");
    Require(worker.Join(5000)&&workerError==null,"Fixture upstream did not finish");
    Thread.Sleep(6500);
    int held=bridge.ActiveConnections;
    Require(held==0,"Completed upstream leaked a connection slot: "+held);
    Console.WriteLine("PASS: half-closed tunnel releases its slot within the drain deadline.");
   }
   var deadline=DateTime.UtcNow.AddSeconds(3);while(bridge.ActiveConnections>0&&DateTime.UtcNow<deadline)Thread.Sleep(20);
   Require(bridge.ActiveConnections==0,"Fixture connection failed to clean up after client disposal");
  }}finally{server.Stop();}
 }
 static void HalfClosedUploadStillStreams() {
  var server=new TcpListener(IPAddress.Loopback,0);server.Start();Exception error=null;
  var worker=new Thread(()=>{try{using(var peer=server.AcceptTcpClient()) {
   var upstream=peer.GetStream();upstream.ReadTimeout=10000;ReadHeader(peer.GetStream());byte[] established=Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");upstream.Write(established,0,established.Length);
   Require(upstream.ReadByte()==-1,"Client half-close not forwarded");Thread.Sleep(6500);byte[] late=Encoding.ASCII.GetBytes("late response");upstream.Write(late,0,late.Length);
  }}catch(Exception failure){error=failure;}});worker.IsBackground=true;worker.Start();
  try {using(var bridge=new LocalProxyBridge()) {
   Require(bridge.Start(0,"http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port),"Half-close stream fixture failed");
   using(var client=new TcpClient("127.0.0.1",bridge.BoundPort)) {
    var transport=client.GetStream();transport.ReadTimeout=12000;byte[] request=Encoding.ASCII.GetBytes("CONNECT fixture.invalid:443 HTTP/1.1\r\nHost: fixture.invalid:443\r\n\r\n");transport.Write(request,0,request.Length);ReadHeader(transport);client.Client.Shutdown(SocketShutdown.Send);
    Require(transport.ReadByte()=='l',"Legitimate delayed response was cut off after client upload EOF");
   }
   Require(worker.Join(3000)&&error==null,"Half-close stream fixture failed: "+error);
  }}finally{server.Stop();}
  Console.WriteLine("PASS: delayed streaming response survives client upload half-close");
 }
 sealed class FakeEnvironment:IProxyEnvironmentStore {
  public readonly System.Collections.Generic.Dictionary<string,string> Values=new System.Collections.Generic.Dictionary<string,string>();
  public int Writes;public bool FailOnce;
  string Key(string name,EnvironmentVariableTarget target){return target+":"+name;}
  public string Read(string name,EnvironmentVariableTarget target){string value;return Values.TryGetValue(Key(name,target),out value)?value:null;}
  public void Write(string name,string value,EnvironmentVariableTarget target){Writes++;if(FailOnce){FailOnce=false;throw new IOException("fixture write error");}Values[Key(name,target)]=value;}
  public void Notify(){}
 }
 sealed class FakeService:IDnsServiceControl {
  public string Image;public bool Removed,Stopped;
  public bool Exists(){return !Removed;}public string ImagePath(){return Image;}
  public void Create(string image){Image=image;}public void Start(){}public void Stop(){Stopped=true;}public void Delete(){Removed=true;}
 }
 static void ServiceRemoval(string root) {
  string folder=Path.Combine(root,"service");Directory.CreateDirectory(folder);
  string image=NativeShortcuts.Quote(Path.Combine(folder,"DagamiAgentProxy.exe"))+" --dns-service";
  File.WriteAllText(Path.Combine(folder,"owner.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{ImagePath=image}));
  var remove=typeof(DnsServiceHost).GetMethod("RemoveOwned",BindingFlags.Static|BindingFlags.NonPublic);
  var service=new FakeService{Image=image};bool denied=false;
  try{remove.Invoke(null,new object[]{folder,service,(Func<bool>)(()=>false)});}catch(TargetInvocationException error){denied=error.InnerException is IOException;}
  Require(denied&&!service.Removed&&!service.Stopped,"DNS service removed while still referenced");
  service.Image="foreign";denied=false;try{remove.Invoke(null,new object[]{folder,service,(Func<bool>)(()=>true)});}catch(TargetInvocationException error){denied=error.InnerException is IOException;}
  Require(denied&&!service.Removed,"Foreign service removed");service.Image=image;
  remove.Invoke(null,new object[]{folder,service,(Func<bool>)(()=>true)});Require(service.Removed&&service.Stopped,"Owned unused service not removed");
  Console.WriteLine("PASS: owned DNS service cleanup rejects foreign or referenced services");
 }
 static void EnvironmentTransaction(string root) {
  var config=FixtureConfig(Path.Combine(root,"environment"));Require(config.Save(),"Fixture config save failed");var store=new FakeEnvironment();
  store.Write("HTTP_PROXY","original",EnvironmentVariableTarget.User);store.Writes=0;
  string path=Path.Combine(root,"environment","config.json");
  using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
   bool rejected=false;try{ProxyEnvironment.Apply(config,store,"http://fixture.invalid:8080");}catch(IOException){rejected=true;}
   Require(rejected&&store.Writes==0&&config.EnvironmentBackup.Count==0,"OS mutated without a durable journal");
  }
  store.FailOnce=true;bool rollback=false;try{ProxyEnvironment.Apply(config,store,"http://fixture.invalid:8080");}catch(IOException){rollback=true;}
  Require(rollback&&store.Read("HTTP_PROXY",EnvironmentVariableTarget.User)=="original"&&config.EnvironmentBackup.Count>0,"Failed apply lost baseline or did not roll back");
  store.FailOnce=true;bool partial=false;try{ProxyEnvironment.Restore(config,store);}catch(IOException){partial=true;}
  Require(partial&&config.EnvironmentBackup.Count>0,"Partial restore discarded journal");
  ProxyEnvironment.Restore(config,store);Require(config.EnvironmentBackup.Count==0&&store.Read("HTTP_PROXY",EnvironmentVariableTarget.User)=="original","Restore did not complete");
  config.EnvironmentBackup=new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
  config.EnvironmentBackup["HTTP_PROXY"]="original";config.EnvironmentBackup["http_proxy"]="original";
  ProxyEnvironment.Restore(config,store);Require(config.EnvironmentBackup.Count==0,"Legacy case-duplicate baseline was not restored");
  config.EnvironmentBackup=new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
  config.EnvironmentBackup["HTTP_PROXY"]="one";config.EnvironmentBackup["http_proxy"]="two";store.Writes=0;bool conflict=false;
  try{ProxyEnvironment.Restore(config,store);}catch(InvalidDataException){conflict=true;}
  Require(conflict&&store.Writes==0&&config.EnvironmentBackup.Count==2,"Conflicting legacy baseline was overwritten");
  int probes=0;var health=new RouteHealth();Require(!health.Check(()=>{probes++;return false;})&&!health.Check(()=>{probes++;return true;})&&probes==1,"Health cache accepted a listener as a healthy route or repeated probes");
  Console.WriteLine("PASS: transactional fake environment and shared route-health probe");
 }
 static void CorruptConfigRecovery(string root) {
  var instance=typeof(ConfigManager).GetField("instance",BindingFlags.Static|BindingFlags.NonPublic);object previous=instance.GetValue(null);
  var config=FixtureConfig(Path.Combine(root,"corrupt-config"));string path=Path.Combine(root,"corrupt-config","config.json");
  File.WriteAllText(path,"{\"last_proxy_protected\":\"invalid-base64\",\"use_proxy\":true}");string before=File.ReadAllText(path);config.Load();
  Require(!string.IsNullOrEmpty(config.LastLoadError),"Fixture failed to reproduce damaged configuration");
  try {
   instance.SetValue(null,config);var checks=RecoveryTool.Plan(false).FindAll(step=>step.Name=="Журнал настроек Dagami"||step.Name=="Прямой маршрут в Dagami");
   Require(checks.Count==2&&!RecoveryTool.Execute(checks).Success&&File.ReadAllText(path)==before,"Recovery overwrote a damaged recovery journal or allowed uninstall");
  }finally{instance.SetValue(null,previous);}
  Console.WriteLine("PASS: damaged configuration is preserved and prevents destructive cleanup");
 }
 static void UninstallFixture(string root) {
  string folder=Path.Combine(root,"uninstall");Directory.CreateDirectory(folder);
  string executable=Path.Combine(folder,"DagamiAgentProxy.exe"),kept=Path.Combine(folder,"keep.txt");
  foreach(string name in new[]{"DagamiAgentProxy.exe","DagamiRecovery.exe","DagamiUninstall.exe"})File.WriteAllText(Path.Combine(folder,name),"fixture package");
  File.WriteAllText(kept,"unrelated fixture");var plan=OwnedUninstaller.Create(executable);plan.ParentId=int.MaxValue;plan.ParentStartTicks=0;
  OwnedUninstaller.Start(plan);var wait=System.Diagnostics.Stopwatch.StartNew();while(plan.Files.Exists(file=>File.Exists(file.Path))&&wait.ElapsedMilliseconds<20000)Thread.Sleep(50);
  Require(!File.Exists(executable)&&!File.Exists(Path.Combine(folder,"DagamiRecovery.exe"))&&!File.Exists(Path.Combine(folder,"DagamiUninstall.exe"))&&File.Exists(kept),"Cleanup helper did not remove the exact package or touched unrelated files");
  Console.WriteLine("PASS: real cleanup helper deletes only three disposable package files");
 }
 static void AdditionalChecks(string root) {
  Require(RecoveryTool.Opens("DagamiRecovery",new string[0])&&RecoveryTool.Opens("DagamiAgentProxy",new[]{"--recover"}),"Recovery command missing");
  Require(!RecoveryTool.Opens("DagamiRecovery",new[]{"--system-dns-helper","Restore","fixture"})&&!RecoveryTool.Opens("DagamiUninstall",new[]{"--remove-dns-service"}),"Recovery alias intercepted a privileged helper");
  Require(typeof(MainForm).Assembly.EntryPoint.IsDefined(typeof(STAThreadAttribute),false),"Windows entry point lost its STA requirement");
  Require(typeof(MainForm).Assembly.GetType("DevProxyApp.LicenseManager")==null,"Application must not require licensing");
  string error;Require(!ProxyAccess.Connect("DAGAMI-TEST-9999-XVSF",out error),"Legacy app key granted proxy access");
  var order=new System.Collections.Generic.List<string>();
  var report=RecoveryTool.Execute(new[]{new RecoveryStep("one",()=>order.Add("one")),new RecoveryStep("failed",()=>{throw new IOException("fixture");}),new RecoveryStep("three",()=>order.Add("three"))});
  Require(!report.Success&&report.Errors.Count==1&&order.Count==2,"Partial recovery was reported as complete or stopped other steps");
  var plan=new UninstallPlan{Directory=root,Files=new System.Collections.Generic.List<UninstallFile>{new UninstallFile{Path=Path.Combine(root,"DagamiAgentProxy.exe"),Sha256=new string('a',64)}},ParentId=1};
  OwnedUninstaller.Validate(plan);plan.Files[0].Path=Path.Combine(root,"..","DagamiAgentProxy.exe");
  bool denied=false;try{OwnedUninstaller.Validate(plan);}catch(InvalidDataException){denied=true;}Require(denied,"Uninstaller accepted a foreign path");
  var transport=typeof(MainForm).Assembly.GetType("DevProxyApp.GatewayTransport");var flags=BindingFlags.Static|BindingFlags.NonPublic;
  var candidates=(string[])transport.GetMethod("Candidates",flags).Invoke(null,new object[]{new Uri(ManagedAccess.Bootstrap)});
  Require(Array.IndexOf(candidates,"48.102.213.179")<0&&candidates.Length>=2,"Proxy fallback contains RU or has no reserve");
  int attempts=0;Func<string,TcpClient> dial=host=>{attempts++;return new TcpClient();};
  Func<TcpClient,Stream> secure=client=>{if(attempts==1)throw new IOException("TLS failure fixture");return new MemoryStream();};
  var result=transport.GetMethod("TryCandidates",flags).Invoke(null,new object[]{new[]{"first.test","second.test"},dial,secure});
  Require(attempts==2,"TLS failure did not reach the reserve");
  ((TcpClient)result.GetType().GetField("Client",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(result)).Close();
  ((Stream)result.GetType().GetField("Stream",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(result)).Dispose();
  Console.WriteLine("PASS: unrestricted application, proxy-only keys, partial recovery, cleanup containment and TLS failover");
 }
 static void ControlTlsAndFallback() {
  const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
  var managed=typeof(ManagedAccess);
  var previous=ServicePointManager.SecurityProtocol;
  try {
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls;
   managed.GetMethod("ConfigureControlTls",flags).Invoke(null,null);
   Require(ServicePointManager.SecurityProtocol==SecurityProtocolType.Tls12,"Managed API depended on an earlier update check");
   ServicePointManager.SecurityProtocol=SecurityProtocolType.SystemDefault;
   managed.GetMethod("ConfigureControlTls",flags).Invoke(null,null);
   Require(ServicePointManager.SecurityProtocol==SecurityProtocolType.SystemDefault,"OS-default TLS policy was replaced");
  }finally{ServicePointManager.SecurityProtocol=previous;}
  var invoke=managed.GetMethod("SendThroughControl",flags);
  var builtin=new Uri(ManagedAccess.Bootstrap);
  Func<Uri,string,Func<Uri,Tuple<int,string>>,Tuple<int,string>> handle=
    (uri,method,request)=>(Tuple<int,string>)invoke.Invoke(null,new object[]{uri,method,request});
  foreach(var mode in new[]{"GET","POST"}) {
   var calls=new System.Collections.Generic.List<Uri>();
   var response=handle(builtin,mode,url=>{
    calls.Add(url);
    if(calls.Count==1)throw new WebException("fixture",WebExceptionStatus.SecureChannelFailure);
    return Tuple.Create(200,"ok");
   });
   Require(response.Item1==200&&calls.Count==2&&calls[0].Host=="dns.dagami.ru"&&calls[1]==builtin,"Managed TLS failure did not use the reserved control route");
  }
  int attempted=0;bool failed=false;
  try{handle(builtin,"POST",url=>{attempted++;throw new WebException("fixture",WebExceptionStatus.Timeout);});}
  catch(TargetInvocationException e){failed=e.InnerException is WebException;}
  Require(failed&&attempted==1,"Activation was sent twice when delivery was uncertain");
  attempted=0;
  Require(handle(builtin,"POST",url=>{attempted++;return Tuple.Create(409,"");}).Item1==409&&attempted==1,"Reached gateway response was ignored");
  var custom=new Uri("https://example.invalid:7443");
  Require(handle(custom,"GET",url=>Tuple.Create(200,url.AbsoluteUri)).Item2==custom.AbsoluteUri,"Custom endpoint was sent to Dagami");
  Console.WriteLine("PASS: explicit managed TLS, restricted fallback and no replay on an ambiguous activation result");
 }
 static void Main() {
  string root=Path.Combine(Path.GetTempPath(),"Dagami-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var instance=typeof(ConfigManager).GetField("instance",BindingFlags.NonPublic|BindingFlags.Static);object old=instance.GetValue(null);
  var log=typeof(Logger).GetField("_logFilePath",BindingFlags.NonPublic|BindingFlags.Static);object oldLog=log.GetValue(null);
  try {
   instance.SetValue(null,FixtureConfig(Path.Combine(root,"bridge-config")));log.SetValue(null,Path.Combine(root,"fixture.log"));
   UnsavedBaseline(root);EnvironmentTransaction(root);ServiceRemoval(root);HalfCloseLease();HalfClosedUploadStillStreams();AdditionalChecks(root);ControlTlsAndFallback();CorruptConfigRecovery(root);UninstallFixture(root);
   Console.WriteLine("Review fixtures completed; no real DNS, shortcuts, licenses or registry settings were changed.");
  }finally{instance.SetValue(null,old);log.SetValue(null,oldLog);}
 }
}
