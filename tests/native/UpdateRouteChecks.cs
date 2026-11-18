using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using DevProxyApp;

class UpdateRouteChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static void Main(){try{
  var assembly=typeof(ManagedAccess).Assembly;
  var coordinator=assembly.GetType("DevProxyApp.AutomaticSystemRoute").GetMethod("Apply",BindingFlags.Static|BindingFlags.NonPublic);
  var steps=new List<string>();
  Func<Task<bool>> dns=()=>{steps.Add("dns");return Task.FromResult(true);};
  Func<bool> proxy=()=>{steps.Add("proxy");return true;};
  Func<Task> restoreDns=()=>{steps.Add("restore-dns");return Task.FromResult(0);};
  Action restoreProxy=()=>steps.Add("restore-proxy"),persist=()=>steps.Add("save");
  Action<Func<Task<string>>,Action> run=(verify,save)=>((Task)coordinator.Invoke(null,new object[]{dns,proxy,verify,restoreDns,restoreProxy,save})).GetAwaiter().GetResult();
  run(()=>{steps.Add("verify-system");return Task.FromResult<string>(null);},persist);
  Assert(string.Join(",",steps)=="dns,proxy,verify-system,save","Settings saved before system verification");
  steps.Clear();bool rejected=false;
  try{run(()=>Task.FromResult("HTTP 503"),persist);}catch(IOException){rejected=true;}
  Assert(rejected&&string.Join(",",steps)=="dns,proxy,restore-proxy,restore-dns","Failed route did not restore DNS and proxy");
  steps.Clear();rejected=false;
  try{run(()=>Task.FromResult<string>(null),()=>{throw new IOException("disk locked");});}catch(IOException){rejected=true;}
  Assert(rejected&&steps.Contains("restore-dns")&&!steps.Contains("save"),"Save failure left system settings behind");
  steps.Clear();rejected=false;
  try{((Task)coordinator.Invoke(null,new object[]{dns,proxy,new Func<Task<string>>(()=>Task.FromResult("failure")),restoreDns,new Action(()=>{throw new IOException("restore blocked");}),persist})).GetAwaiter().GetResult();}
  catch(IOException error){rejected=error.GetType().Name=="AutomaticRouteRollbackException";}
  Assert(rejected&&steps.Contains("restore-dns"),"Proxy rollback failure skipped DNS rollback or was hidden");

  Assert(ManagedAccess.IsProfile(ManagedAccess.PaidProfile)&&ManagedAccess.IsPaidProfile(ManagedAccess.PaidProfile),"Paid pool not a keyed managed route");
  var config=new ConfigManager{UseProxy=true,LastProxy=ManagedAccess.PaidProfile};
  Assert(config.EffectiveProxy==ManagedAccess.PaidProfile&&config.SelectedProxyMode=="paid","Paid mode collapsed to the ordinary keyed route");
  var chooser=assembly.GetType("DevProxyApp.AutomaticAiRoute");
  var candidates=(System.Collections.IList)chooser.GetMethod("Candidates",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"Codex",null,false,null,null,true});
  Assert(candidates.Count==0,"Keyed private pool was offered without an active key");
  Assert(UpdateVersion.Parse("v0.8.0-beta").CompareTo(UpdateVersion.Parse("v0.7.4-beta"))>0,"Previous release cannot recognise the new version");
  var device=(ManagedAccess)Activator.CreateInstance(typeof(ManagedAccess),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{new Uri(ManagedAccess.Bootstrap),Convert.FromBase64String(ManagedAccess.CreateKey())},null);
  typeof(ManagedAccess).GetProperty("PaidPool").GetSetMethod(true).Invoke(device,new object[]{true});
  string request=device.ConnectRequest("api.openai.com:443");
  Assert(request.Contains("Proxy-Authorization: Dagami-Paid v1.")&&!request.Contains("password@"),"Paid route did not use a device signature");

  var benchmark=assembly.GetType("DevProxyApp.DnsBenchmarkEngine");
  var valid=benchmark.GetMethod("ValidAddresses",BindingFlags.Static|BindingFlags.NonPublic);
  byte[] question=DnsHttpsTransport.Question("example.com");
  foreach(int rcode in new[]{0,2,3}){
   byte[] answer=(byte[])question.Clone();answer[2]=128;answer[3]=(byte)rcode;
   bool refused=false;try{valid.Invoke(null,new object[]{question,answer,"example.com"});}catch(TargetInvocationException error){refused=error.InnerException is InvalidDataException;}
   Assert(refused,"Empty/NXDOMAIN/SERVFAIL answer accepted by benchmark");
  }
  byte[] good=new byte[question.Length+16];Buffer.BlockCopy(question,0,good,0,question.Length);good[2]=128;good[3]=128;good[7]=1;
  byte[] record={192,12,0,1,0,1,0,0,0,30,0,4,8,8,8,8};Buffer.BlockCopy(record,0,good,question.Length,16);
  var addresses=(System.Collections.IList)valid.Invoke(null,new object[]{question,good,"example.com"});Assert(addresses.Count==1,"Valid public response rejected");
  good[good.Length-4]=127;good[good.Length-3]=0;good[good.Length-2]=0;good[good.Length-1]=1;
  bool privateRefused=false;try{valid.Invoke(null,new object[]{question,good,"example.com"});}catch(TargetInvocationException error){privateRefused=error.InnerException is InvalidDataException;}
  Assert(privateRefused,"Private address accepted by DNS benchmark");
  var sanity=assembly.GetType("DevProxyApp.AnswerSanity").GetMethod("IsBogus",new[]{typeof(IPAddress)});
  Assert((bool)sanity.Invoke(null,new object[]{IPAddress.Parse("::ffff:127.0.0.1")}),"Mapped private IPv4 accepted");
  Assert(!(bool)sanity.Invoke(null,new object[]{IPAddress.Parse("8.8.8.8")}),"Public IPv4 rejected");
  var browser=assembly.GetType("DevProxyApp.BrowserItem");
  var item=Activator.CreateInstance(browser);
  assembly.GetType("DevProxyApp.BrowserDiagnostics").GetMethod("ClassifyChromium",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{item,"automatic",null,false});
  Assert(browser.GetProperty("Status").GetValue(item,null).ToString()=="Unknown","Automatic browser DoH falsely certified as system DNS");
  Console.WriteLine("PASS "+checks+" automatic system transaction, rollback, keyed paid pool, invalid DNS and unknown browser state checks");
 }catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}}
}
