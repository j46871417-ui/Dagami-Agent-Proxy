using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using DevProxyApp;
class DnsProviderChecks {
 static int checks;static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
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
  Console.WriteLine("PASS "+checks+" official profile schema, fixed TLS identities, provider priority, verified updates/last-good cache and DoH framing checks (no external network)");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}finally{string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-provider-check-"))throw new IOException("Unsafe fixture cleanup");Directory.Delete(root,true);}}
}
