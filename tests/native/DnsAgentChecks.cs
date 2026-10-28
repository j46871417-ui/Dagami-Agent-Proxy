using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using DevProxyApp;
class DnsAgentChecks {
 static int checks;static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static byte[] Read(Stream stream,int length){byte[] value=new byte[length];int offset=0;while(offset<length){int n=stream.Read(value,offset,length-offset);if(n==0)throw new EndOfStreamException();offset+=n;}return value;}
 static byte[] Reply(byte[] query){byte[] response=new byte[query.Length+16];Array.Copy(query,response,query.Length);response[2]=129;response[3]=128;response[7]=1;byte[] answer={192,12,0,1,0,1,0,0,0,30,0,4,8,8,4,4};Array.Copy(answer,0,response,query.Length,answer.Length);return response;}
 static byte[] Udp(LocalDnsAgent agent,byte[] query){using(var socket=new UdpClient()){socket.Client.ReceiveTimeout=3000;socket.Connect(IPAddress.Loopback,agent.Port);socket.Send(query,query.Length);IPEndPoint peer=null;return socket.Receive(ref peer);}}
 static byte[] Tcp(LocalDnsAgent agent,byte[] query){using(var socket=new TcpClient("127.0.0.1",agent.Port)){var stream=socket.GetStream();stream.ReadTimeout=3000;stream.WriteByte((byte)(query.Length>>8));stream.WriteByte((byte)query.Length);stream.Write(query,0,query.Length);byte[] length=Read(stream,2);return Read(stream,(length[0]<<8)|length[1]);}}
 sealed class Service:IDnsServiceControl {
  public bool Installed,Running,FailCreateAfterInstall,FailDelete;public int Starts,Stops,Deletes;public string Image;
  public bool Exists(){return Installed;}public string ImagePath(){return Image;}public void Create(string image){Installed=true;Image=image;if(FailCreateAfterInstall)throw new IOException("injected post-create failure");}public void Start(){Running=true;Starts++;}public void Stop(){Running=false;Stops++;}public void Delete(){if(FailDelete)throw new IOException("injected delete failure");Installed=false;Deletes++;}
 }
 static void Install(string folder,string source,Service service,Func<bool> probe){try{typeof(DnsServiceHost).GetMethod("EnsureInstalled",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{folder,source,service,probe});}catch(TargetInvocationException error){throw error.InnerException;}}
 static int Main(){string root=Path.Combine(Path.GetTempPath(),"Dagami-dns-agent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{
  byte[] question=DnsHttpsTransport.Question("example.com");question[0]=17;question[1]=25;
  using(var agent=new LocalDnsAgent(Reply)){agent.Start(0,false);byte[] response=Udp(agent,question);Assert(response[0]==17&&response[1]==25&&response[7]==1,"UDP response/transaction lost");Assert(Tcp(agent,question).Length==response.Length,"TCP response framing failed");}
  using(var agent=new LocalDnsAgent(query=>{var response=Reply(query);Array.Resize(ref response,1000);return response;})){agent.Start(0,false);byte[] udp=Udp(agent,question);Assert(udp.Length<=512&&(udp[2]&2)!=0&&udp[7]==0,"UDP truncation unsafe");Assert(Tcp(agent,question).Length==1000,"Full TCP DNS answer truncated");}
  using(var agent=new LocalDnsAgent(query=>{throw new IOException("Fixture resolver unavailable");})){agent.Start(0,false);Assert((Udp(agent,question)[3]&15)==2,"Resolver failure not SERVFAIL");}
  var occupied=new TcpListener(IPAddress.Loopback,0);occupied.Start();try{using(var agent=new LocalDnsAgent(Reply)){bool refused=false;try{agent.Start(((IPEndPoint)occupied.LocalEndpoint).Port,false);}catch(SocketException){refused=true;}Assert(refused,"Existing DNS listener displaced");}}finally{occupied.Stop();}
  string source=Path.Combine(root,"source.exe"),folder=Path.Combine(root,"service");File.WriteAllText(source,"owned fixture v1");var service=new Service();
  Install(folder,source,service,()=>true);Assert(service.Installed&&service.Running&&File.Exists(Path.Combine(folder,"owner.json")),"Service install transaction failed");
  Install(folder,source,service,()=>true);Assert(service.Stops==0,"Idempotent install restarted service");
  File.WriteAllText(source,"owned fixture v2");bool failed=false;try{Install(folder,source,service,()=>false);}catch(IOException){failed=true;}
  Assert(failed&&service.Running&&File.ReadAllText(Path.Combine(folder,"DagamiAgentProxy.exe"))=="owned fixture v1","Failed service upgrade did not restore previous binary");
  var foreign=new Service{Installed=true,Image="foreign.exe"};failed=false;try{Install(Path.Combine(root,"foreign"),source,foreign,()=>true);}catch(IOException){failed=true;}Assert(failed&&foreign.Stops==0&&foreign.Deletes==0,"Foreign service modified");
  var fresh=new Service();failed=false;try{Install(Path.Combine(root,"failed"),source,fresh,()=>false);}catch(IOException){failed=true;}Assert(failed&&!fresh.Installed&&!fresh.Running,"Failed initial service install remained active");
  var partial=new Service{FailCreateAfterInstall=true,FailDelete=true};string partialFolder=Path.Combine(root,"partial-create");string failureMessage=null;try{Install(partialFolder,source,partial,()=>true);}catch(IOException error){failureMessage=error.Message;}
  Assert(partial.Installed&&File.Exists(Path.Combine(partialFolder,"owner.json"))&&failureMessage!=null&&failureMessage.Contains("rollback requires attention"),"Failed service creation lost ownership marker or rollback error");
  Console.WriteLine("PASS "+checks+" loopback DNS UDP/TCP, truncation, SERVFAIL, port ownership and service transaction/rollback checks (SCM and real DNS unchanged)");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}finally{string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-dns-agent-"))throw new IOException("Unsafe fixture cleanup");Directory.Delete(root,true);}}
}
