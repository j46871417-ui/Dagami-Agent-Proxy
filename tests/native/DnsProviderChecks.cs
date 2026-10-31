using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using DevProxyApp;
class DnsProviderChecks {
 static int checks;static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 sealed class TrackingStream:MemoryStream {public int Writes;public override void Write(byte[] data,int offset,int count){if(count>0)Writes++;base.Write(data,offset,count);}}
 static void Reject(Action action,string message){bool failed=false;try{action();}catch{failed=true;}Assert(failed,message);}
 static byte[] Profile(string provider,params string[] addresses){var values=new StringBuilder();foreach(string address in addresses)values.Append("<string>"+address+"</string>");return Encoding.UTF8.GetBytes("<?xml version='1.0'?><!DOCTYPE plist PUBLIC '-//Apple//DTD PLIST 1.0//EN' 'http://www.apple.com/DTDs/PropertyList-1.0.dtd'><plist><dict><key>PayloadContent</key><array><dict><key>DNSSettings</key><dict><key>DNSProtocol</key><string>HTTPS</string><key>ServerURL</key><string>https://"+DnsProviders.HostFor(provider)+"/dns-query</string><key>ServerAddresses</key><array>"+values+"</array></dict></dict></array></dict></plist>");}
 static byte[] ReadHttp(string response){try{return (byte[])typeof(DnsHttpsTransport).GetMethod("ReadResponse",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{new MemoryStream(Encoding.ASCII.GetBytes(response)),32,"application/dns-message",Stopwatch.StartNew(),1000});}catch(TargetInvocationException error){throw error.InnerException;}}
 static int Main(){string root=Path.Combine(Path.GetTempPath(),"Dagami-provider-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{
  typeof(Logger).GetField("_logFilePath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(root,"proxy.log"));
  Assert(DnsProviders.ParseProfile("dagami",Profile("dagami","8.8.8.8","8.8.8.8","1.1.1.1")).Length==2,"Profile addresses not deduplicated");
  foreach(string address in new[]{"127.0.0.1","10.1.2.3","192.168.0.1","169.254.1.2","100.64.0.1","224.1.1.1","0.0.0.0"})Reject(()=>DnsProviders.ParseProfile("dagami",Profile("dagami",address)),"Non-public address accepted: "+address);
  Reject(()=>DnsProviders.ParseProfile("magicdns",Profile("dagami","8.8.8.8")),"Profile changed TLS authority");
  Reject(()=>DnsProviders.ParseProfile("dagami",new byte[131073]),"Oversized profile accepted");
  Reject(()=>DnsProviders.ParseProfile("dagami",new byte[]{0x30,0x82,0,0}),"Malformed signed profile accepted");
  DnsProviders.Initialize(Path.Combine(root,"cache.json"),false);
  DnsProviders.Refresh(provider=>Profile(provider,provider=="dagami"?"8.8.4.4":"9.9.9.9"),endpoint=>endpoint.Address=="8.8.4.4"||endpoint.Address=="9.9.9.9");
  Assert(DnsProviders.LastError==null,"Verified update failed");var current=DnsProviders.Snapshot();Assert(current.Length==2&&current[0].Provider=="dagami"&&current[1].Provider=="magicdns","Provider priority lost");
  Assert(DnsProviders.ForServer("45.147.31.32")[0].Address=="8.8.4.4","Old configured provider IP did not adopt current addresses");
  Assert(DnsProviders.Addresses("192.0.2.53").Length==1&&DnsProviders.Addresses("192.0.2.53")[0]=="192.0.2.53","Custom DNS replaced");
  DnsProviders.Refresh(provider=>{throw new IOException("Fixture source down");},endpoint=>true);
  Assert(DnsProviders.Snapshot()[0].Address=="8.8.4.4"&&DnsProviders.LastError!=null,"Last good list lost when sources failed");
  DnsProviders.Refresh(provider=>Profile(provider,provider=="dagami"?"8.8.4.4":"9.9.9.9"),endpoint=>endpoint.Address=="8.8.4.4"||endpoint.Address=="9.9.9.9"||endpoint.Address=="89.125.103.139");
  Assert(Array.Exists(DnsProviders.Snapshot(),x=>x.Provider=="dagami"&&x.Address=="89.125.103.139"),"Verified owner-provided reserve missing from updated official profile");
  DnsProviders.Refresh(provider=>Profile(provider,"1.0.0.1"),endpoint=>false);
  Assert(DnsProviders.Snapshot()[0].Address=="8.8.4.4","Unverified endpoints adopted");
  DnsProviders.Refresh(provider=>provider=="dagami"?Profile(provider,"8.8.1.1","8.8.1.2","8.8.1.3","8.8.1.4","8.8.1.5","8.8.1.6","8.8.1.7","8.8.1.8"):Profile(provider,"9.9.9.9"),endpoint=>true);
  current=DnsProviders.Snapshot();Assert(Array.FindAll(current,x=>x.Provider=="dagami").Length==8&&Array.Exists(current,x=>x.Address=="89.125.103.139"),"Large profile lost reserved nodes or exceeded provider limit");
  Assert(File.Exists(Path.Combine(root,"cache.json"))&&Directory.GetFiles(root,"*.tmp").Length==0,"Cache write not atomic/clean");
  Assert(Encoding.ASCII.GetString(ReadHttp("HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: 3\r\n\r\nabc"))=="abc","DoH length body failed");
  Assert(Encoding.ASCII.GetString(ReadHttp("HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r\n2\r\nbc\r\n0\r\n\r\n"))=="abc","DoH chunked body failed");
  foreach(string header in new[]{"Content-Length: 50","Content-Length: 3\r\nTransfer-Encoding: chunked","Content-Length: 3\r\nContent-Length: 3","Transfer-Encoding: gzip"})Reject(()=>ReadHttp("HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\n"+header+"\r\n\r\nabc"),"Unsafe HTTP framing accepted");
  Reject(()=>ReadHttp("HTTP/1.1 302 Redirect\r\nContent-Type: application/dns-message\r\nContent-Length: 0\r\n\r\n"),"DoH redirect accepted");
  byte[] question=DnsHttpsTransport.Question("www.googleapis.com"),same=(byte[])question.Clone();same[13]=(byte)'W';Assert(DnsHttpsTransport.SameQuestion(question,same),"DNS question case rejected");Assert(!DnsHttpsTransport.SameQuestion(question,DnsHttpsTransport.Question("other.googleapis.com")),"Wrong DNS question accepted");
  byte[] padded=EdnsPadding.PadQuery(question,128);Assert(padded.Length%128==0&&padded[11]==1&&DnsHttpsTransport.SameQuestion(question,padded)&&DnsHttpsTransport.QuestionName(padded)=="www.googleapis.com","EDNS0 RFC 8467 padding damaged question");
  var customDoh=DnsProviders.ParseCustomSpec("https://cloudflare-dns.com/dns-query");Assert(customDoh!=null&&customDoh.Length>=2&&customDoh[0].Protocol=="HTTPS"&&customDoh[0].Address=="1.1.1.1","Well-known DoH spec not parsed");
  Assert(DnsProviders.ForServer("dns.comss.one")!=null&&DnsProviders.ForServer("dns.comss.one")[0].Provider=="comss","Built-in Comss.one DNS missing");
  Assert(DnsProviders.ForServer("dns.geohide.ru")!=null&&DnsProviders.ForServer("dns.geohide.ru")[0].Provider=="geohide","Built-in GeoHide DNS missing");
  Assert(DnsProviders.ForServer("dns.malw.link")!=null&&DnsProviders.ForServer("dns.malw.link")[0].Provider=="malw"&&DnsProviders.ForServer("dns.malw.link")[0].Protocol=="TLS","Built-in Malw.link DoT DNS missing");
  Assert(Array.TrueForAll(DnsProviders.ExtendedPool("dns.dagami.ru"),ep=>ep.Provider=="dagami"||ep.Provider=="magicdns"),"Unselected provider silently added to Dagami/MagicDNS");
  var budget=typeof(DnsProviders).GetMethod("AttemptTimeout",BindingFlags.NonPublic|BindingFlags.Static);int remainingBudget=12000,attempts=6;for(int i=0;i<6;i++){int allowed=(int)budget.Invoke(null,new object[]{remainingBudget,attempts--,2400});Assert(allowed>0&&allowed<=2400,"Invalid DNS time budget");remainingBudget-=allowed;if(i==2)Assert(remainingBudget>=6000,"Primary consumed reserve DNS budget");}
  var ordered=DnsProviders.ExtendedPool("dns.dagami.ru");int lastDagami=Array.FindLastIndex(ordered,x=>x.Provider=="dagami"),firstMagic=Array.FindIndex(ordered,x=>x.Provider=="magicdns");
  Assert(lastDagami>=0&&firstMagic>lastDagami,"Dagami must precede all MagicDNS fallback nodes");
  Assert(DnsProviders.ExtendedPool("dns.magicdns.ru")[0].Provider=="magicdns","Explicitly selected provider must stay first");
  var probeMethod=typeof(DnsProviders).GetMethod("ProbeEndpoints",BindingFlags.Static|BindingFlags.NonPublic);
  var probeEp=DnsProviders.Snapshot()[0];var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);var finished=new ManualResetEvent(false);
  Func<DnsEndpoint,string> delayed=ep=>{entered.Set();release.WaitOne();finished.Set();return "late-fixture";};
  string incomplete=(string)probeMethod.Invoke(null,new object[]{new[]{probeEp},delayed,100});
  Assert(entered.WaitOne(1000)&&incomplete.Contains("Не дождались"),"Delayed DNS probe did not time out safely");
  Func<DnsEndpoint,string> immediate=ep=>"current-fixture";
  probeMethod.Invoke(null,new object[]{new[]{probeEp},immediate,2000});string expectedSummary=DnsProviders.LastProbeSummary;
  release.Set();Assert(finished.WaitOne(2000),"Delayed probe did not finish");Thread.Sleep(100);
  Assert(DnsProviders.LastProbeSummary==expectedSummary&&DnsProviders.FormatPoolStatus("dns.dagami.ru").Contains("current-fixture")&&!DnsProviders.FormatPoolStatus("dns.dagami.ru").Contains("late-fixture"),"Late probe overwrote a newer result");
  var emptyWatch=Stopwatch.StartNew();probeMethod.Invoke(null,new object[]{new DnsEndpoint[0],immediate,1000});Assert(emptyWatch.ElapsedMilliseconds<500,"Empty DNS probe waited for a nonexistent worker");
  entered.Close();release.Close();finished.Close();
  var customBoot=DnsProviders.ParseCustomSpec("https://dns.example.com:8443/custom-query#9.9.9.9,8.8.8.8");Assert(customBoot!=null&&customBoot.Length==2&&customBoot[0].Port==8443&&customBoot[0].Path=="/custom-query"&&customBoot[1].Address=="8.8.8.8","Custom DoH bootstrap spec failed");
  var customDot=DnsProviders.ParseCustomSpec("tls://dns.google:853");Assert(customDot!=null&&customDot[0].Protocol=="TLS"&&customDot[0].Port==853&&customDot[0].Address=="8.8.8.8","DoT spec failed");
  var customPort=DnsProviders.ParseCustomSpec("9.9.9.9:5353");Assert(customPort!=null&&customPort[0].Protocol=="UDP"&&customPort[0].Port==5353,"Custom UDP port spec failed");
  byte[] customProfile=Encoding.UTF8.GetBytes("<?xml version='1.0'?><plist><dict><key>PayloadContent</key><array><dict><key>DNSSettings</key><dict><key>DNSProtocol</key><string>HTTPS</string><key>ServerURL</key><string>https://dns.quad9.net/dns-query</string><key>ServerAddresses</key><array><string>9.9.9.9</string></dict></dict></array></dict></plist>".Replace("</string></dict>","</string></array></dict>"));
  var parsedCustom=DnsProviders.ParseCustomProfile(customProfile);Assert(parsedCustom!=null&&parsedCustom.Length==1&&parsedCustom[0].Host=="dns.quad9.net"&&parsedCustom[0].Address=="9.9.9.9","Third-party .mobileconfig profile rejected");
  byte[] sniName=Encoding.ASCII.GetBytes("cloudcode-pa.googleapis.com");
  var chBody=new List<byte>();chBody.AddRange(new byte[]{0x03,0x03});chBody.AddRange(new byte[32]);chBody.Add(0);chBody.AddRange(new byte[]{0x00,0x02,0x13,0x01,0x01,0x00});
  var ext=new List<byte>();int sniListLen=sniName.Length+3;int extLen=sniListLen+2;
  ext.AddRange(new byte[]{0x00,0x00,(byte)(extLen>>8),(byte)extLen,(byte)(sniListLen>>8),(byte)sniListLen,0x00,(byte)(sniName.Length>>8),(byte)sniName.Length});ext.AddRange(sniName);
  chBody.AddRange(new byte[]{(byte)(ext.Count>>8),(byte)ext.Count});chBody.AddRange(ext);
  var hs=new List<byte>{0x01,(byte)(chBody.Count>>16),(byte)(chBody.Count>>8),(byte)chBody.Count};hs.AddRange(chBody);
  var record=new List<byte>{0x16,0x03,0x01,(byte)(hs.Count>>8),(byte)hs.Count};record.AddRange(hs);
  byte[] clientHello=record.ToArray();string parsedHost;int sniOff,sniMid;
  Assert(TlsClientHelloInspector.TryFindSni(clientHello,0,clientHello.Length,out parsedHost,out sniOff,out sniMid)&&parsedHost=="cloudcode-pa.googleapis.com","TLS ClientHello SNI parser failed");
  byte[] splitRecords=TlsClientHelloInspector.SplitTlsRecordAtSni(clientHello,0,clientHello.Length);
  int rec1Len=(splitRecords[3]<<8)|splitRecords[4];int rec2Off=5+rec1Len;int rec2Len=(splitRecords[rec2Off+3]<<8)|splitRecords[rec2Off+4];
  Assert(splitRecords[0]==0x16&&splitRecords[rec2Off]==0x16&&rec1Len+rec2Len==hs.Count,"TLS Record Splitting produced invalid records");
  var external=new TrackingStream();using(var wrapped=new DpiDesyncStream(external,null,DpiDesyncMode.ExternalEngine)){wrapped.Write(clientHello,0,clientHello.Length);Assert(external.Writes==1,"External DPI mode unexpectedly split data");}
  byte[] combined=new byte[clientHello.Length+1];Array.Copy(clientHello,combined,clientHello.Length);combined[combined.Length-1]=0x17;
  var recordOnly=new TrackingStream();using(var wrapped=new DpiDesyncStream(recordOnly,null,DpiDesyncMode.TlsRecordSplit)){wrapped.Write(combined,0,combined.Length);Assert(recordOnly.Writes==1,"TLS-only mode unexpectedly fell back to TCP splitting");}
  var zeroFirst=new TrackingStream();using(var wrapped=new DpiDesyncStream(zeroFirst,null,DpiDesyncMode.TcpMultiSplit)){wrapped.Write(clientHello,0,0);wrapped.Write(clientHello,0,clientHello.Length);Assert(zeroFirst.Writes>1,"Empty write disabled later TLS handling");}
  Console.WriteLine("PASS "+checks+" official profile schema, fixed TLS identities, provider priority, verified updates/last-good cache and DoH framing checks (no external network)");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}finally{string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-provider-check-"))throw new IOException("Unsafe fixture cleanup");Directory.Delete(root,true);}}
}
