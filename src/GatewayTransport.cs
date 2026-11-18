using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;

namespace DevProxyApp {
 internal sealed class GatewayConnection {
  internal readonly TcpClient Client;internal readonly Stream Stream;
  internal GatewayConnection(TcpClient client,Stream stream){Client=client;Stream=stream;}
 }
 internal static class GatewayTransport {
  internal static string[] Candidates(Uri address) {
   // Control-plane may use a different country. Proxy data never falls back to RU.
   return address.Authority.Equals("test-dns.dagami.ru:9443",StringComparison.OrdinalIgnoreCase)
    ?new[]{address.IdnHost,"179.254.186.80","45.86.72.85","31.76.52.235","150.241.83.174"}:new[]{address.IdnHost};
  }
  internal static GatewayConnection TryCandidates(IEnumerable<string> hosts,Func<string,TcpClient> dial,Func<TcpClient,Stream> secure) {
   Exception last=null;
   foreach(string host in hosts) {
    TcpClient client=null;
    string phase="TCP";
    try {client=dial(host);phase="TLS";return new GatewayConnection(client,secure(client));}
    catch(Exception failure) {
     if(client!=null)client.Close();
     if(!(failure is IOException)&&!(failure is SocketException)&&!(failure is AuthenticationException)&&!(failure is TimeoutException))throw;
     Logger.Log("[Gateway] endpoint="+host+" phase="+phase+" error="+failure.GetType().Name);
     last=failure;
    }
   }
   throw new IOException("Не удалось подключиться к шлюзу прокси. Попробуйте другую сеть. Подробности записаны в журнале.",last);
  }
  internal static GatewayConnection Open(Uri address,string phase,int timeout) {
   var clock=Stopwatch.StartNew();
   Func<int> remaining=()=>{int value=timeout-(int)clock.ElapsedMilliseconds;if(value<=0)throw new TimeoutException("Истекло время подключения к шлюзу.");return value;};
   return TryCandidates(Candidates(address),host=> {
    var client=new TcpClient {NoDelay=true};
    try {
     var connect=client.BeginConnect(host,address.Port,null,null);
     using(connect.AsyncWaitHandle)if(!connect.AsyncWaitHandle.WaitOne(Math.Min(2000,remaining())))throw new TimeoutException("TCP шлюза не отвечает.");
     client.EndConnect(connect);return client;
    }catch{client.Close();throw;}
   },client=>TlsDiagnostics.Connect(client.GetStream(),address.IdnHost,phase,Math.Min(4000,remaining())));
  }
 }
}
