using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DevProxyApp {
 public static class DnsHttpsTransport {
  public static byte[] Question(string host) {
   if(string.IsNullOrEmpty(host)||host.Length>253||Uri.CheckHostName(host)!=UriHostNameType.Dns)throw new ArgumentException("Invalid DNS name.");
   using(var output=new MemoryStream()) {
    output.Write(new byte[]{0,0,1,0,0,1,0,0,0,0,0,0},0,12);
    foreach(string label in host.Split('.')){byte[] bytes=Encoding.ASCII.GetBytes(label);if(bytes.Length<1||bytes.Length>63)throw new ArgumentException("Invalid DNS label.");output.WriteByte((byte)bytes.Length);output.Write(bytes,0,bytes.Length);}
    output.Write(new byte[]{0,0,1,0,1},0,5);return output.ToArray();
   }
  }
  public static string QuestionName(byte[] packet) {
   try {
    int end=QuestionEnd(packet);
    int offset=12;var labels=new List<string>();
    while(offset<end&&packet[offset]!=0){
     int len=packet[offset++];
     if(len<1||len>63||offset+len>end)return null;
     labels.Add(Encoding.ASCII.GetString(packet,offset,len));
     offset+=len;
    }
    return labels.Count==0?null:string.Join(".",labels.ToArray());
   } catch { return null; }
  }
  internal static int QuestionLength(byte[] packet) {return QuestionEnd(packet);}
  static int QuestionEnd(byte[] packet) {
   if(packet==null||packet.Length<17||packet[4]!=0||packet[5]!=1)throw new InvalidDataException("Expected one DNS question.");
   int offset=12;
   for(int labels=0;labels<128;labels++){
    if(offset>=packet.Length)break;int length=packet[offset++];
    if(length==0){if(offset+4>packet.Length)break;return offset+4;}
    if(length>63||offset+length>packet.Length)break;offset+=length;
   }
   throw new InvalidDataException("Invalid DNS question.");
  }
  public static bool SameQuestion(byte[] query,byte[] response) {
   try {
    int end=QuestionEnd(query);if(QuestionEnd(response)!=end)return false;
    int at=12;while(query[at]!=0){int length=query[at];if(response[at++]!=length)return false;for(int index=0;index<length;index++,at++){byte a=query[at],b=response[at];if(a>=65&&a<=90)a+=32;if(b>=65&&b<=90)b+=32;if(a!=b)return false;}}for(;at<end;at++)if(query[at]!=response[at])return false;
    return true;
   }catch(InvalidDataException){return false;}
  }
  public static byte[] Exchange(DnsEndpoint endpoint,byte[] query,int timeout=4000) {
   QuestionEnd(query);if(query.Length>65535)throw new InvalidDataException("DNS query too large.");
   bool obfuscate=ConfigManager.Instance.DnsObfuscation;
   string proto=(endpoint.Protocol??"HTTPS").ToUpperInvariant();
   if(proto=="UDP"||proto=="TCP") {
    byte[] plainResp=ExchangeUdpTcp(endpoint,query,proto=="TCP",timeout,obfuscate);
    if(plainResp.Length<12||plainResp[0]!=query[0]||plainResp[1]!=query[1]||(plainResp[2]&128)==0||(plainResp[2]&120)!=0||!SameQuestion(query,plainResp))
     throw new InvalidDataException("DNS response does not match the DNS question.");
    return plainResp;
   }
   if(proto=="TLS") {
    byte[] dotWire=obfuscate?EdnsPadding.PadQuery((byte[])query.Clone()):(byte[])query.Clone();
    byte[] dotResp=ExchangeDot(endpoint,dotWire,timeout,obfuscate);
    if(dotResp.Length<12||dotResp[0]!=query[0]||dotResp[1]!=query[1]||(dotResp[2]&128)==0||(dotResp[2]&120)!=0||!SameQuestion(query,dotResp))
     throw new InvalidDataException("DoT response does not match the DNS question.");
    return dotResp;
   }
   byte[] wire=(byte[])query.Clone();wire[0]=wire[1]=0;
   if(obfuscate)wire=EdnsPadding.PadQuery(wire);
   string path=string.IsNullOrEmpty(endpoint.Path)?"/dns-query":endpoint.Path;
   int port=endpoint.Port>0?endpoint.Port:443;
   byte[] response=Request(endpoint.Address,endpoint.Host,path,"POST",wire,"application/dns-message",65535,timeout,port);
   if(response.Length<12||response[0]!=0||response[1]!=0||(response[2]&128)==0||(response[2]&120)!=0||!SameQuestion(wire,response))throw new InvalidDataException("DoH response does not match the DNS question.");
   response[0]=query[0];response[1]=query[1];return response;
  }
  public static byte[] ExchangeDot(DnsEndpoint endpoint,byte[] query,int timeout,bool obfuscate) {
   IPAddress ip;if(!IPAddress.TryParse(endpoint.Address,out ip))throw new ArgumentException("DoT endpoint must be a literal IP.");
   var watch=Stopwatch.StartNew();
   int port=endpoint.Port>0?endpoint.Port:853;
   using(var client=new TcpClient(ip.AddressFamily)){
    client.NoDelay=true;
    var pending=client.BeginConnect(ip,port,null,null);using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException("DoT connection timeout.");client.EndConnect(pending);}
    Stream net=client.GetStream();
    if(obfuscate){
     var mode=DpiDesyncStream.ParseMode(ConfigManager.Instance.DpiDesyncMode);
     if(mode!=DpiDesyncMode.Off&&mode!=DpiDesyncMode.ExternalEngine)net=new DpiDesyncStream(net,client.Client,mode);
    }
    using(var tls=TlsDiagnostics.Connect(net,endpoint.Host,"dns-tls",Remaining(watch,timeout),false)){
     tls.WriteTimeout=Remaining(watch,timeout);
     byte[] framed=new byte[query.Length+2];
     framed[0]=(byte)(query.Length>>8);framed[1]=(byte)query.Length;
     Buffer.BlockCopy(query,0,framed,2,query.Length);
     if(obfuscate){
      tls.Write(framed,0,1);tls.Flush();
      tls.Write(framed,1,framed.Length-1);tls.Flush();
     }else{
      tls.Write(framed,0,framed.Length);tls.Flush();
     }
     byte[] lenBytes=Exact(tls,2,watch,timeout);
     int respLen=(lenBytes[0]<<8)|lenBytes[1];
     if(respLen<12)throw new InvalidDataException("Invalid DoT response length.");
     return Exact(tls,respLen,watch,timeout);
    }
   }
  }
  public static byte[] ExchangeUdpTcp(DnsEndpoint endpoint,byte[] query,bool tcp,int timeout,bool obfuscate) {
   IPAddress ip;if(!IPAddress.TryParse(endpoint.Address,out ip))throw new ArgumentException("DNS endpoint must be a literal IP.");
   int port=endpoint.Port>0?endpoint.Port:53;
   var target=new IPEndPoint(ip,port);
   if(!tcp){
    using(var udp=new UdpClient(ip.AddressFamily)){
     udp.Client.ReceiveTimeout=timeout;udp.Client.SendTimeout=timeout;udp.Connect(target);
     udp.Send(query,query.Length);IPEndPoint remote=null;
     byte[] resp=udp.Receive(ref remote);
     if(resp.Length>=12&&(resp[2]&2)!=0)return ExchangeUdpTcp(endpoint,query,true,timeout,obfuscate);
     return resp;
    }
   }
   var watch=Stopwatch.StartNew();
   using(var client=new TcpClient(ip.AddressFamily)){
    client.NoDelay=true;
    var pending=client.BeginConnect(ip,port,null,null);using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException("DNS TCP connection timeout.");client.EndConnect(pending);}
    var stream=client.GetStream();stream.WriteTimeout=Remaining(watch,timeout);
    byte[] framed=new byte[query.Length+2];framed[0]=(byte)(query.Length>>8);framed[1]=(byte)query.Length;
    Buffer.BlockCopy(query,0,framed,2,query.Length);
    if(obfuscate){
     // Split 2-byte DNS-over-TCP length prefix from body to evade simple TCP payload DPI signatures
     stream.Write(framed,0,1);stream.Flush();
     stream.Write(framed,1,framed.Length-1);stream.Flush();
    }else{
     stream.Write(framed,0,framed.Length);stream.Flush();
    }
    byte[] lenBytes=Exact(stream,2,watch,timeout);
    int respLen=(lenBytes[0]<<8)|lenBytes[1];
    if(respLen<12)throw new InvalidDataException("Invalid DNS TCP response length.");
    return Exact(stream,respLen,watch,timeout);
   }
  }
  static int Remaining(Stopwatch watch,int timeout){int remaining=timeout-(int)watch.ElapsedMilliseconds;if(remaining<=0)throw new TimeoutException("HTTPS deadline exceeded.");return remaining;}
  static byte[] Exact(Stream stream,int count,Stopwatch watch,int timeout){byte[] bytes=new byte[count];int offset=0;while(offset<count){int remaining=Remaining(watch,timeout);if(stream.CanTimeout)stream.ReadTimeout=remaining;int n=stream.Read(bytes,offset,count-offset);if(n==0)throw new EndOfStreamException();offset+=n;}return bytes;}
  static string Line(Stream stream,int max,Stopwatch watch,int timeout){var data=new List<byte>();while(data.Count<max){byte[] value=Exact(stream,1,watch,timeout);data.Add(value[0]);if(data.Count>=2&&data[data.Count-2]==13&&value[0]==10)return Encoding.ASCII.GetString(data.ToArray(),0,data.Count-2);}throw new InvalidDataException("HTTPS header too large.");}
  internal static byte[] ReadResponse(Stream stream,int limit,string expectedType,Stopwatch watch,int timeout) {
   string status=Line(stream,4096,watch,timeout);if(!status.StartsWith("HTTP/1.1 200 ",StringComparison.Ordinal)&&!status.StartsWith("HTTP/1.0 200 ",StringComparison.Ordinal))throw new IOException("HTTPS resolver/source did not return HTTP 200.");
   var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int total=status.Length;
   while(true){string line=Line(stream,8192,watch,timeout);total+=line.Length+2;if(total>16384)throw new InvalidDataException("HTTPS headers too large.");if(line.Length==0)break;int colon=line.IndexOf(':');if(colon<1)throw new InvalidDataException("Invalid HTTPS header.");string name=line.Substring(0,colon),value=line.Substring(colon+1).Trim();if(headers.ContainsKey(name)){if(name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)||name.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase)||name.Equals("Content-Type",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Duplicate HTTPS framing header.");continue;}headers.Add(name,value);}
   string type;if(expectedType!=null&&(!headers.TryGetValue("Content-Type",out type)||!type.Split(';')[0].Trim().Equals(expectedType,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Unexpected DoH content type.");
   string encoding;if(headers.TryGetValue("Content-Encoding",out encoding)&&!encoding.Equals("identity",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected HTTPS content encoding.");
   string length,transfer;bool hasLength=headers.TryGetValue("Content-Length",out length),hasTransfer=headers.TryGetValue("Transfer-Encoding",out transfer);
   if(hasLength&&hasTransfer)throw new InvalidDataException("Ambiguous HTTPS body framing.");
   if(hasLength){int count;if(!int.TryParse(length,out count)||count<0||count>limit)throw new InvalidDataException("HTTPS body too large.");return Exact(stream,count,watch,timeout);}
   using(var output=new MemoryStream()) {
    if(hasTransfer){
     if(!transfer.Equals("chunked",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsupported HTTPS framing.");
     while(true){string chunk=Line(stream,128,watch,timeout);int size;if(!int.TryParse(chunk.Split(';')[0],System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out size)||size<0||size>limit-output.Length)throw new InvalidDataException("Invalid HTTPS chunk.");if(size==0){int trailerBytes=0;while(true){string trailer=Line(stream,8192,watch,timeout);trailerBytes+=trailer.Length+2;if(trailerBytes>16384)throw new InvalidDataException("HTTPS trailers too large.");if(trailer.Length==0)break;}break;}byte[] bytes=Exact(stream,size,watch,timeout);output.Write(bytes,0,bytes.Length);byte[] end=Exact(stream,2,watch,timeout);if(end[0]!=13||end[1]!=10)throw new InvalidDataException("Invalid HTTPS chunk ending.");}
    }else{
     byte[] buffer=new byte[4096];while(true){int remaining=Remaining(watch,timeout);if(stream.CanTimeout)stream.ReadTimeout=remaining;int count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,limit-output.Length+1));if(count==0)break;if(output.Length+count>limit)throw new InvalidDataException("HTTPS body too large.");output.Write(buffer,0,count);}
    }
    return output.ToArray();
   }
  }
  public static byte[] Request(string address,string host,string path,string method,byte[] body,string expectedType,int limit,int timeout=6000,int port=443) {
   IPAddress ip;if(!IPAddress.TryParse(address,out ip))throw new ArgumentException("HTTPS endpoint must be a literal IP.");
   if(Uri.CheckHostName(host)==UriHostNameType.Unknown||!path.StartsWith("/",StringComparison.Ordinal)||path.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("Invalid HTTPS authority/path.");
   var watch=Stopwatch.StartNew();
   using(var client=new TcpClient(ip.AddressFamily)){
    client.NoDelay=true;
    var pending=client.BeginConnect(ip,port>0?port:443,null,null);using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException("HTTPS connection timeout.");client.EndConnect(pending);}
    Stream net=client.GetStream();
    if(ConfigManager.Instance.DnsObfuscation){
     var dpiMode=DpiDesyncStream.ParseMode(ConfigManager.Instance.DpiDesyncMode);
     if(dpiMode!=DpiDesyncMode.Off&&dpiMode!=DpiDesyncMode.ExternalEngine)net=new DpiDesyncStream(net,client.Client,dpiMode);
    }
    // Online CRL retrieval itself may require this DNS service. Keep OS trust,
    // certificate dates and hostname validation, without recursive online CRL.
    using(var tls=TlsDiagnostics.Connect(net,host,"dns-https",Remaining(watch,timeout),false)){
     string ua=ConfigManager.Instance.DnsObfuscation?"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36":"Dagami-Agent-Proxy";
     string header=method+" "+path+" HTTP/1.1\r\nHost: "+host+"\r\nConnection: close\r\nUser-Agent: "+ua+"\r\nAccept-Encoding: identity\r\n";
     if(body!=null)header+="Content-Type: application/dns-message\r\nAccept: application/dns-message\r\nContent-Length: "+body.Length+"\r\n";
     byte[] bytes=Encoding.ASCII.GetBytes(header+"\r\n");tls.WriteTimeout=Remaining(watch,timeout);tls.Write(bytes,0,bytes.Length);if(body!=null)tls.Write(body,0,body.Length);tls.Flush();
     return ReadResponse(tls,limit,expectedType,watch,timeout);
    }
   }
  }
 }
}
