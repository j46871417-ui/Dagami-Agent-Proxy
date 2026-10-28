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
   byte[] wire=(byte[])query.Clone();wire[0]=wire[1]=0;
   byte[] response=Request(endpoint.Address,endpoint.Host,"/dns-query","POST",wire,"application/dns-message",65535,timeout);
   if(response.Length<12||response[0]!=0||response[1]!=0||(response[2]&128)==0||(response[2]&120)!=0||!SameQuestion(wire,response))throw new InvalidDataException("DoH response does not match the DNS question.");
   response[0]=query[0];response[1]=query[1];return response;
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
  public static byte[] Request(string address,string host,string path,string method,byte[] body,string expectedType,int limit,int timeout=6000) {
   IPAddress ip;if(!IPAddress.TryParse(address,out ip))throw new ArgumentException("HTTPS endpoint must be a literal IP.");
   if(Uri.CheckHostName(host)==UriHostNameType.Unknown||!path.StartsWith("/",StringComparison.Ordinal)||path.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("Invalid HTTPS authority/path.");
   var watch=Stopwatch.StartNew();
   using(var client=new TcpClient(ip.AddressFamily)){
    var pending=client.BeginConnect(ip,443,null,null);using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException("HTTPS connection timeout.");client.EndConnect(pending);}
    // Online CRL retrieval itself may require this DNS service. Keep OS trust,
    // certificate dates and hostname validation, without recursive online CRL.
    using(var tls=TlsDiagnostics.Connect(client.GetStream(),host,"dns-https",Remaining(watch,timeout),false)){
     string header=method+" "+path+" HTTP/1.1\r\nHost: "+host+"\r\nConnection: close\r\nUser-Agent: Dagami-Agent-Proxy\r\nAccept-Encoding: identity\r\n";
     if(body!=null)header+="Content-Type: application/dns-message\r\nAccept: application/dns-message\r\nContent-Length: "+body.Length+"\r\n";
     byte[] bytes=Encoding.ASCII.GetBytes(header+"\r\n");tls.WriteTimeout=Remaining(watch,timeout);tls.Write(bytes,0,bytes.Length);if(body!=null)tls.Write(body,0,body.Length);tls.Flush();
     return ReadResponse(tls,limit,expectedType,watch,timeout);
    }
   }
  }
 }
}
