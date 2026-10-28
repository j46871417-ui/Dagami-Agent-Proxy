using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DevProxyApp {
 public sealed class LocalDnsAgent : IDisposable {
  readonly Func<byte[],byte[]> exchange;
  readonly List<UdpClient> udp=new List<UdpClient>();readonly List<TcpListener> tcp=new List<TcpListener>();
  readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();readonly object sync=new object();
  readonly SemaphoreSlim slots=new SemaphoreSlim(48);volatile bool running;
  readonly Dictionary<string,DateTime> cooldown=new Dictionary<string,DateTime>();
  public int Port {get;private set;}
  public LocalDnsAgent(Func<byte[],byte[]> resolver=null){exchange=resolver??Forward;}
  byte[] Forward(byte[] query){
   var preferred=new List<DnsEndpoint>();var delayed=new List<DnsEndpoint>();
   foreach(var endpoint in DnsProviders.Snapshot()){bool cooling;lock(sync){DateTime until;cooling=cooldown.TryGetValue(endpoint.Address,out until)&&until>DateTime.UtcNow;}(cooling?delayed:preferred).Add(endpoint);}preferred.AddRange(delayed);
   var elapsed=Stopwatch.StartNew();
   int remainingAttempts=preferred.Count;
   foreach(var endpoint in preferred){int remaining=6500-(int)elapsed.ElapsedMilliseconds;if(remaining<=0)break;
    int timeout=Math.Min(2400,Math.Max(250,remaining/Math.Max(1,remainingAttempts--)));
    try{byte[] response=DnsHttpsTransport.Exchange(endpoint,query,Math.Min(timeout,remaining));if((response[3]&15)==2)throw new IOException("Resolver SERVFAIL.");lock(sync)cooldown.Remove(endpoint.Address);return response;}
    catch(Exception error){if(!(error is IOException)&&!(error is InvalidDataException)&&!(error is SocketException)&&!(error is System.TimeoutException)&&!(error is System.Security.Authentication.AuthenticationException))throw;lock(sync)cooldown[endpoint.Address]=DateTime.UtcNow.AddSeconds(30);}
   }
   throw new IOException("All encrypted DNS resolvers are unavailable.");
  }
  public void Start(int port=53,bool ipv6=true){
   if(running)throw new InvalidOperationException("DNS agent already running.");running=true;
   try{Bind(IPAddress.Loopback,port);Port=((IPEndPoint)tcp[0].LocalEndpoint).Port;if(ipv6&&Socket.OSSupportsIPv6)try{Bind(IPAddress.IPv6Loopback,Port);}catch(SocketException error){if(error.SocketErrorCode!=SocketError.AddressNotAvailable&&error.SocketErrorCode!=SocketError.AddressFamilyNotSupported)throw;}
    foreach(var socket in udp){UdpClient listener=socket;new Thread(()=>ReceiveUdp(listener)){IsBackground=true}.Start();}
    foreach(var socket in tcp){TcpListener listener=socket;new Thread(()=>ReceiveTcp(listener)){IsBackground=true}.Start();}
   }catch{Dispose();throw;}
  }
  void Bind(IPAddress address,int port){
   var stream=new TcpListener(address,port);stream.Server.ExclusiveAddressUse=true;if(address.AddressFamily==AddressFamily.InterNetworkV6)stream.Server.DualMode=false;stream.Start();
   UdpClient datagrams=null;try{datagrams=new UdpClient(address.AddressFamily);datagrams.Client.ExclusiveAddressUse=true;if(address.AddressFamily==AddressFamily.InterNetworkV6)datagrams.Client.DualMode=false;datagrams.Client.Bind(new IPEndPoint(address,((IPEndPoint)stream.LocalEndpoint).Port));tcp.Add(stream);udp.Add(datagrams);}catch{stream.Stop();if(datagrams!=null)datagrams.Close();throw;}
  }
  static byte[] Failure(byte[] query){if(query==null||query.Length<12)return null;int length;try{length=DnsHttpsTransport.QuestionLength(query);}catch{length=12;}byte[] response=new byte[length];Array.Copy(query,response,length);response[2]=(byte)(128|(query[2]&1));response[3]=130;if(length==12)response[4]=response[5]=0;for(int i=6;i<12;i++)response[i]=0;return response;}
  byte[] Answer(byte[] query){try{if(query.Length>65535||query.Length<12||(query[2]&248)!=0)throw new InvalidDataException();byte[] response=exchange(query);if(response.Length<12||response.Length>65535||response[0]!=query[0]||response[1]!=query[1]||!DnsHttpsTransport.SameQuestion(query,response))throw new InvalidDataException();return response;}catch{return Failure(query);}}
  void ReceiveUdp(UdpClient listener){while(running)try{IPEndPoint peer=null;byte[] query=listener.Receive(ref peer);IPEndPoint destination=peer;if(!IPAddress.IsLoopback(peer.Address))continue;if(!slots.Wait(0)){byte[] busy=Failure(query);if(busy!=null)listener.Send(busy,busy.Length,destination);continue;}
    ThreadPool.QueueUserWorkItem(state=>{try{byte[] response=Answer(query);if(response!=null){if(response.Length>512){byte code=(byte)(response[3]&143);response=Failure(query);response[2]|=2;response[3]=code;}listener.Send(response,response.Length,destination);}}catch{}finally{slots.Release();}});
   }catch(SocketException){if(!running)return;}catch(ObjectDisposedException){return;}}
  static byte[] Read(Stream stream,int length){byte[] bytes=new byte[length];int offset=0;while(offset<length){int count=stream.Read(bytes,offset,length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}return bytes;}
  void ReceiveTcp(TcpListener listener){while(running)try{var client=listener.AcceptTcpClient();if(!slots.Wait(0)){client.Close();continue;}lock(sync){if(!running){client.Close();slots.Release();return;}clients.Add(client);}
    ThreadPool.QueueUserWorkItem(state=>{try{using(client){var stream=client.GetStream();stream.ReadTimeout=8000;stream.WriteTimeout=8000;for(int queries=0;queries<16&&running;queries++){byte[] prefix=Read(stream,2);int length=(prefix[0]<<8)|prefix[1];if(length<12)break;byte[] response=Answer(Read(stream,length));if(response==null)break;stream.WriteByte((byte)(response.Length>>8));stream.WriteByte((byte)response.Length);stream.Write(response,0,response.Length);stream.Flush();}}}catch{}finally{lock(sync)clients.Remove(client);slots.Release();}});
   }catch(SocketException){if(!running)return;}catch(ObjectDisposedException){return;}}
  public void Dispose(){running=false;foreach(var socket in udp)socket.Close();foreach(var socket in tcp)socket.Stop();lock(sync){foreach(var client in clients)client.Close();clients.Clear();}udp.Clear();tcp.Clear();}
 }
 public sealed class DagamiDnsWindowsService : ServiceBase {
  LocalDnsAgent agent;
  public DagamiDnsWindowsService(){ServiceName=DnsServiceHost.ServiceName;CanStop=true;AutoLog=true;}
  protected override void OnStart(string[] args){DnsProviders.Initialize(Path.Combine(DnsServiceHost.DirectoryPath,"dns-endpoints.json"));agent=new LocalDnsAgent();agent.Start();}
  protected override void OnStop(){if(agent!=null){agent.Dispose();agent=null;}}
 }
 public interface IDnsServiceControl {
  bool Exists();string ImagePath();void Create(string imagePath);void Start();void Stop();void Delete();
 }
 public sealed class WindowsDnsServiceControl : IDnsServiceControl {
  public bool Exists(){using(var service=new ServiceController(DnsServiceHost.ServiceName))try{var status=service.Status;return true;}catch(InvalidOperationException error){var cause=error.InnerException as System.ComponentModel.Win32Exception;if(cause!=null&&cause.NativeErrorCode==1060)return false;throw;}}
  public string ImagePath(){using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+DnsServiceHost.ServiceName))return key==null?null:Convert.ToString(key.GetValue("ImagePath"));}
  static void Command(string arguments){var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"sc.exe"),arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};using(var process=Process.Start(start)){var output=System.Threading.Tasks.Task.Factory.StartNew(()=>process.StandardOutput.ReadToEnd());var error=System.Threading.Tasks.Task.Factory.StartNew(()=>process.StandardError.ReadToEnd());if(!process.WaitForExit(20000)){process.Kill();throw new IOException("DNS service control timeout.");}System.Threading.Tasks.Task.WaitAll(output,error);if(process.ExitCode!=0)throw new IOException("DNS service control failed; code="+process.ExitCode);}}
  public void Create(string imagePath){Command("create "+DnsServiceHost.ServiceName+" binPath= "+NativeShortcuts.Quote(imagePath)+" start= auto DisplayName= "+NativeShortcuts.Quote("Dagami encrypted DNS"));try{Command("failure "+DnsServiceHost.ServiceName+" reset= 86400 actions= restart/5000/restart/15000/restart/60000");}catch{try{Command("delete "+DnsServiceHost.ServiceName);}catch{}throw;}}
  public void Start(){Command("config "+DnsServiceHost.ServiceName+" start= auto");using(var service=new ServiceController(DnsServiceHost.ServiceName)){if(service.Status==ServiceControllerStatus.Running)return;service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20));}}
  public void Stop(){using(var service=new ServiceController(DnsServiceHost.ServiceName)){if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(20));}}Command("config "+DnsServiceHost.ServiceName+" start= demand");}
  public void Delete(){Command("delete "+DnsServiceHost.ServiceName);}
 }
 public static class DnsServiceHost {
  public const string ServiceName="DagamiEncryptedDns";
  public static string DirectoryPath {get{return Path.Combine(SystemDnsManager.StoreRoot,"service");}}
  public static void Run(){ServiceBase.Run(new DagamiDnsWindowsService());}
  static string Hash(string path){using(var input=File.OpenRead(path))using(var hash=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input));}
  public static bool Probe(){return Probe(IPAddress.Loopback);}
  public static bool IsRunning(){try{var control=new WindowsDnsServiceControl();string executable=Path.Combine(DirectoryPath,"DagamiAgentProxy.exe"),marker=Path.Combine(DirectoryPath,"owner.json"),image=NativeShortcuts.Quote(executable)+" --dns-service";if(!control.Exists()||!File.Exists(marker)||control.ImagePath()!=image)return false;using(var service=new ServiceController(ServiceName))return service.Status==ServiceControllerStatus.Running;}catch{return false;}}
  public static bool Probe(IPAddress address){try{byte[] question=DnsHttpsTransport.Question("example.com");using(var client=new UdpClient(address.AddressFamily)){client.Client.ReceiveTimeout=10000;client.Connect(address,53);client.Send(question,question.Length);IPEndPoint peer=null;byte[] answer=client.Receive(ref peer);int ttl;ScopedDnsResolver.ParseAnswer(answer,new byte[]{0,0},"example.com",out ttl);return true;}}catch{return false;}}
  public static void EnsureInstalled(){SystemDnsManager.ProtectServiceDirectory(DirectoryPath);EnsureInstalled(DirectoryPath,Assembly.GetExecutingAssembly().Location,new WindowsDnsServiceControl(),Probe);}
  public static void StopOwned(){var control=new WindowsDnsServiceControl();if(!control.Exists())return;string executable=Path.Combine(DirectoryPath,"DagamiAgentProxy.exe"),marker=Path.Combine(DirectoryPath,"owner.json"),image=NativeShortcuts.Quote(executable)+" --dns-service";if(!File.Exists(marker)||control.ImagePath()!=image)throw new IOException("Foreign DNS service was not stopped.");var owner=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(marker));if(Convert.ToString(owner["ImagePath"])!=image)throw new IOException("DNS service ownership mismatch.");control.Stop();}
  internal static void EnsureInstalled(string directory,string source,IDnsServiceControl control,Func<bool> probe){
   directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("DNS service directory is a reparse point.");
   string executable=Path.Combine(directory,"DagamiAgentProxy.exe"),marker=Path.Combine(directory,"owner.json"),image=NativeShortcuts.Quote(executable)+" --dns-service";
   bool existed=control.Exists();
   if(existed){if(!File.Exists(marker)||control.ImagePath()!=image)throw new IOException("A foreign DNS service already uses this name; it was not changed.");var owner=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(marker));if(Convert.ToString(owner["ImagePath"])!=image)throw new IOException("DNS service ownership record does not match.");}
   string backup=null,staging=executable+"."+Guid.NewGuid().ToString("N")+".tmp";bool created=false,replaced=false;
   try{
    if(!File.Exists(executable)||Hash(source)!=Hash(executable)){
     File.Copy(source,staging,false);if(Hash(source)!=Hash(staging))throw new IOException("DNS service executable copy failed verification.");
     if(existed)control.Stop();
     if(File.Exists(executable)){backup=executable+"."+Guid.NewGuid().ToString("N")+".bak";File.Replace(staging,executable,backup);}else File.Move(staging,executable);replaced=true;
    }
    if(!existed){
     File.WriteAllText(marker,new JavaScriptSerializer().Serialize(new{ImagePath=image,Version=1}));
     try{control.Create(image);created=true;}
     catch{if(control.Exists()&&control.ImagePath()==image)created=true;else if(File.Exists(marker))File.Delete(marker);throw;}
    }
    control.Start();if(!probe())throw new IOException("Encrypted DNS service did not pass a resolver check. System DNS was not changed.");
    if(backup!=null)File.Delete(backup);
   }catch(Exception original){
    var errors=new List<string>();
    if(created){try{control.Stop();}catch(Exception error){errors.Add("stop:"+error.GetType().Name);}bool deleted=false;try{control.Delete();deleted=!control.Exists();}catch(Exception error){errors.Add("delete:"+error.GetType().Name);}if(deleted&&File.Exists(marker))File.Delete(marker);}
    else if(existed&&replaced){
     try{control.Stop();}catch(Exception error){errors.Add("stop:"+error.GetType().Name);}
     bool restored=backup==null;
     if(backup!=null&&File.Exists(backup))try{File.Copy(backup,executable,true);restored=Hash(backup)==Hash(executable);if(!restored)throw new IOException("Restored service hash mismatch.");}catch(Exception error){errors.Add("restore:"+error.GetType().Name);}
     if(restored)try{control.Start();if(backup!=null)File.Delete(backup);}catch(Exception error){errors.Add("restart:"+error.GetType().Name);}
    }
    if(errors.Count>0)throw new IOException("DNS service operation failed; rollback requires attention. "+string.Join(", ",errors.ToArray())+". Backup: "+(backup??"not created"),original);
    throw;
   }finally{if(File.Exists(staging))File.Delete(staging);}
  }
 }
}
