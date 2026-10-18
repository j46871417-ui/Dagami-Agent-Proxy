using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using DevProxyApp;
class CliChecks {
 static int checks;
 static void Assert(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
 class Paths:ICliPathEnvironment {
  public string value="original;foreign",process="original;foreign";
  public bool Fail;
  public string UserPath {get{return value;}set{if(Fail){Fail=false;throw new IOException("fixture PATH write failure");}this.value=value;}}
  public string ProcessPath {get{return process;}set{process=value;}}
  public string MachinePath {get;set;}
  public void Notify(){}
 }
 static string Self {get{return Process.GetCurrentProcess().MainModule.FileName;}}
 static string Hash(string path){return AntigravityIntegration.Hash(File.ReadAllBytes(path));}
 static void Reject(Action action){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Assert(rejected,"Unsafe operation accepted");}
 static string Header(Stream stream){var s=new StringBuilder();while(s.Length<16384){int b=stream.ReadByte();if(b<0)throw new IOException("EOF");s.Append((char)b);if(s.ToString().EndsWith("\r\n\r\n"))return s.ToString();}throw new IOException("header too large");}
 static int Child(string[] args){
  Console.InputEncoding=Console.OutputEncoding=new UTF8Encoding(false);
  string input=Console.ReadLine(),proxy=Environment.GetEnvironmentVariable("HTTPS_PROXY");
  var uri=new Uri(proxy);
  if(uri.Host!="127.0.0.1"||!string.IsNullOrEmpty(uri.UserInfo))return 91;
  using(var tcp=new TcpClient(uri.Host,uri.Port)){
   var stream=tcp.GetStream();stream.ReadTimeout=5000;
   byte[] request=Encoding.ASCII.GetBytes("CONNECT cli-fixture.invalid:443 HTTP/1.1\r\nHost: cli-fixture.invalid:443\r\n\r\n");stream.Write(request,0,request.Length);
   if(!Header(stream).StartsWith("HTTP/1.1 200"))return 92;
   byte[] ping=Encoding.ASCII.GetBytes("ping");stream.Write(ping,0,4);var reply=new byte[4];int got=0;while(got<4){int n=stream.Read(reply,got,4-got);if(n==0)return 93;got+=n;}if(Encoding.ASCII.GetString(reply)!="pong")return 94;
  }
  Console.WriteLine(new JavaScriptSerializer().Serialize(new{args=args,input=input,cwd=Environment.CurrentDirectory,proxy=proxy,noProxy=Environment.GetEnvironmentVariable("NO_PROXY"),node=Environment.GetEnvironmentVariable("NODE_USE_ENV_PROXY")}));
  Console.Error.WriteLine("fixture-stderr");return 37;
 }
 static int Main(string[] args){
  if(args.Length>0&&args[0]=="child")return Child(args);
  if(args.Length>0&&args[0]=="driver"){
   string[] forwarded=new string[args.Length-2];Array.Copy(args,2,forwarded,0,forwarded.Length);
   return NativeCliApps.Run(Self,forwarded,args[1],null);
  }
  string root=Path.Combine(Path.GetTempPath(),"Dagami-cli-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   string originalDir=Path.Combine(root,"original space");Directory.CreateDirectory(originalDir);
   string original=Path.Combine(originalDir,"agy.exe");File.Copy(Self,original);string originalHash=Hash(original);
   Assert(NativeCliApps.IsCli(new AppTarget{ExePath=original}),"Console PE not recognized");
   Assert(!NativeCliApps.IsCli(new AppTarget{ExePath=typeof(MainForm).Assembly.Location}),"GUI mistaken for CLI");
   string manager=Path.Combine(root,"DagamiAgentProxy.exe");File.Copy(typeof(MainForm).Assembly.Location,manager);
   var paths=new Paths{MachinePath=""};var store=new NativeCliApps(Path.Combine(root,"managed"),paths);
   var target=new AppTarget{Id="test-agy",DisplayName="agy",ExePath=original};
   string shim=store.Enable(target,manager),issue;
   Assert(File.Exists(shim),"Command shim not installed");
   Assert(paths.UserPath.StartsWith(store.BinDirectory+";"),"User PATH not prepended");
   Assert(store.VerifiedCount(target.Id,out issue)==1&&issue==null,"CLI not verified");
   Assert(Hash(original)==originalHash,"Original CLI changed");
   store.Enable(target,manager);Assert(store.VerifiedCount(target.Id,out issue)==1,"Repeated patch duplicated command");
   paths.UserPath=originalDir+";"+paths.UserPath;
   Assert(store.VerifiedCount(target.Id,out issue)==1&&issue!=null,"Higher-priority command not reported");
   paths.UserPath=paths.UserPath.Substring(originalDir.Length+1);
   Assert(NativeCliApps.FindCommand("agy",paths.UserPath,store.BinDirectory)==null,"Discovery selected its own shim");
   paths.UserPath+=";foreign-added-later";
   store.Restore(target.Id);Assert(!File.Exists(shim)&&paths.UserPath=="original;foreign;foreign-added-later","Restore lost foreign PATH edits");
   paths.Fail=true;bool failed=false;try{store.Enable(target,manager);}catch(IOException){failed=true;}
   Assert(failed&&!File.Exists(shim)&&store.VerifiedCount(target.Id,out issue)==0,"Partial install did not roll back");
   Assert(paths.UserPath=="original;foreign;foreign-added-later","Failed patch changed PATH");
   shim=store.Enable(target,manager);File.AppendAllText(shim,"foreign-edit");Reject(()=>store.Restore(target.Id));
   Assert(File.Exists(shim),"Foreign shim deleted");
   // Restore fixture bytes to test ordinary removal; never touch real installations.
   File.Copy(manager,shim,true);store.Restore(target.Id);
   paths.MachinePath=originalDir;Reject(()=>store.Enable(target,manager));paths.MachinePath="";
   string cmd=Path.Combine(originalDir,"gemini.cmd");File.WriteAllText(cmd,"@echo off\r\n\"%dp0%\\node_modules\\fake\\cli.js\" %*\r\n");
   string nodeDir=Path.Combine(originalDir,"node_modules","fake");Directory.CreateDirectory(nodeDir);File.WriteAllText(Path.Combine(nodeDir,"cli.js"),"fixture");File.Copy(Self,Path.Combine(originalDir,"node.exe"));
   var start=NativeCliApps.CreateStart(cmd,new[]{"space value","","literal\"quote"},"http://127.0.0.1:1234",root);
   Assert(start.FileName==Path.Combine(originalDir,"node.exe"),"npm wrapper not resolved to Node");
   Assert(start.Arguments.Contains("cli.js")&&!start.FileName.EndsWith("cmd.exe"),"npm arguments passed through CMD");
   Assert(start.EnvironmentVariables["NO_PROXY"]=="localhost,127.0.0.1,::1","Loopback exclusions missing");
   Assert(start.EnvironmentVariables["NODE_USE_ENV_PROXY"]=="1","Node proxy mode missing");
   string unknown=Path.Combine(originalDir,"unknown.cmd");File.WriteAllText(unknown,"@echo unsupported\r\n");Reject(()=>NativeCliApps.CreateStart(unknown,new string[0],"http://127.0.0.1:1",root));
   string ps1=Path.Combine(originalDir,"script.ps1");File.WriteAllText(ps1,"param([string]$Value)\nWrite-Output $Value\n");
   var powershell=NativeCliApps.CreateStart(ps1,new[]{"space value"},"http://127.0.0.1:1234",root);
   Assert(powershell.FileName.EndsWith("powershell.exe")&&powershell.Arguments.Contains("-File")&&!powershell.Arguments.Contains("Bypass"),"PowerShell script ignores execution policy");

   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;string received=null,error=null;
   var worker=new Thread(()=>{try{using(var tcp=listener.AcceptTcpClient()){var stream=tcp.GetStream();stream.ReadTimeout=5000;received=Header(stream);byte[] ok=Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");stream.Write(ok,0,ok.Length);byte[] ping=new byte[4];int got=0;while(got<4){int n=stream.Read(ping,got,4-got);if(n==0)throw new IOException("No fixture payload");got+=n;}if(Encoding.ASCII.GetString(ping)!="ping")throw new IOException("Wrong fixture payload");byte[] pong=Encoding.ASCII.GetBytes("pong");stream.Write(pong,0,4);}}catch(Exception ex){error=ex.Message;}}){IsBackground=true};worker.Start();
   string[] expected={"child","space value","","literal\"quote","trailing\\","&|<>%USERPROFILE%!","русский"};
   var all=new List<string>{"driver","http://127.0.0.1:"+port};all.AddRange(expected);
   var psi=new ProcessStartInfo(Self,string.Join(" ",all.ConvertAll(NativeShortcuts.Quote).ToArray())){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=originalDir,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};psi.EnvironmentVariables["NO_PROXY"]="*";
   using(var process=Process.Start(psi)){
    process.StandardInput.WriteLine("stdin preserved");process.StandardInput.Close();string output=process.StandardOutput.ReadToEnd(),stderr=process.StandardError.ReadToEnd();Assert(process.WaitForExit(15000),"CLI runner hung");
    Assert(process.ExitCode==37,"Exit code lost: "+process.ExitCode+" "+stderr);
    var value=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(output.Trim());
    var actual=(System.Collections.ArrayList)value["args"];Assert(actual.Count==expected.Length,"Argument count changed");for(int i=0;i<expected.Length;i++)Assert(Convert.ToString(actual[i])==expected[i],"Argument changed at "+i);
    Assert(Convert.ToString(value["input"])=="stdin preserved","stdin lost");Assert(stderr.Contains("fixture-stderr"),"stderr lost");Assert(Convert.ToString(value["cwd"])==originalDir,"cwd changed");Assert(Convert.ToString(value["noProxy"])!="*","Wildcard NO_PROXY inherited");
   }
   Assert(worker.Join(5000)&&error==null,"Proxy fixture failed: "+error);listener.Stop();Assert(received.StartsWith("CONNECT cli-fixture.invalid:443"),"Child bypassed selected upstream");
   Console.WriteLine("PASS "+checks+" CLI patch, PATH rollback, conflict protection, npm resolution, arguments/stdin/stdout/stderr/exit/cwd and real bridge traffic checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{string absolute=Path.GetFullPath(root),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(absolute.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(absolute).StartsWith("Dagami-cli-check-"))try{Directory.Delete(absolute,true);}catch{}}
 }
}
