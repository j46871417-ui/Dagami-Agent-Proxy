using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DevProxyApp {
 public static class DnsHttpsTransport {
  static readonly object PoolLock=new object();
  static readonly Dictionary<string,IdleDoh> DohPool=new Dictionary<string,IdleDoh>(StringComparer.Ordinal);
  static int poolGeneration;
  const int MaxIdleDoh=8;
  const int MaxDohIdleMs=10000;
  static readonly long MaxDohIdleTicks=Stopwatch.Frequency*MaxDohIdleMs/1000;
  static readonly Timer DohIdleTimer=new Timer(_=>ExpireIdleDoh(),null,Timeout.Infinite,Timeout.Infinite);

  sealed class IdleDoh {
   public Stream Stream;
   public long ReturnedAt;
  }

  static DnsHttpsTransport() {
   try { NetworkChange.NetworkAddressChanged += (sender,args)=>ClearDohPool(); }
   catch(NetworkInformationException) { }
   catch(PlatformNotSupportedException) { }
  }

  internal static void ClearDohPool() {
   List<IdleDoh> idle;
   lock(PoolLock) {
    poolGeneration++;
    idle=new List<IdleDoh>(DohPool.Values);
    DohPool.Clear();
    DohIdleTimer.Change(Timeout.Infinite,Timeout.Infinite);
   }
   foreach(var entry in idle)CloseDohStream(entry.Stream);
  }

  static void ExpireIdleDoh() {
   var expired=new List<KeyValuePair<string,IdleDoh>>();
   lock(PoolLock) {
    long now=Stopwatch.GetTimestamp();
    foreach(var entry in DohPool)
     if(now-entry.Value.ReturnedAt>=MaxDohIdleTicks)expired.Add(entry);
    foreach(var entry in expired)DohPool.Remove(entry.Key);
    if(DohPool.Count==0)DohIdleTimer.Change(Timeout.Infinite,Timeout.Infinite);
   }
   foreach(var entry in expired)CloseDohStream(entry.Value.Stream);
  }

  static void CloseDohStream(Stream stream) {
   if(stream==null)return;
   try { stream.Dispose(); }
   catch(IOException) { }
   catch(SocketException) { }
   catch(ObjectDisposedException) { }
  }
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
  public static byte[] Exchange(DnsEndpoint endpoint,byte[] query,int timeout=4000,bool? obfuscationOverride=null) {
   QuestionEnd(query);if(query.Length>65535)throw new InvalidDataException("DNS query too large.");
   bool obfuscate=obfuscationOverride ?? ConfigManager.Instance.DnsObfuscation;
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
   byte[] response;
   if(obfuscate) {
    response=Request(endpoint.Address,endpoint.Host,path,"POST",wire,"application/dns-message",65535,timeout,port,true);
    ValidateDohResponse(wire,response);
   } else {
    var ip=ValidateHttpsTarget(endpoint.Address,endpoint.Host,path);
    response=ExchangeDoh(ip.ToString(),endpoint.Host,port,path,wire,timeout,
     left=>OpenHttps(ip,endpoint.Host,port,Stopwatch.StartNew(),left,false));
   }
   response[0]=query[0];response[1]=query[1];return response;
  }

  static void ValidateDohResponse(byte[] wire,byte[] response) {
   if(response.Length<12||response[0]!=0||response[1]!=0||(response[2]&128)==0||(response[2]&120)!=0||!SameQuestion(wire,response))
    throw new InvalidDataException("DoH response does not match the DNS question.");
  }

  internal static byte[] ExchangeDoh(string address,string host,int port,string path,byte[] query,int timeout,Func<int,Stream> connect) {
   string key=address+"|"+host.ToLowerInvariant()+"|"+port+"|"+path;
   var watch=Stopwatch.StartNew();
   IdleDoh idle;
   int generation;
   lock(PoolLock) {
    generation=poolGeneration;
    if(DohPool.TryGetValue(key,out idle)) {
     DohPool.Remove(key);
     if(DohPool.Count==0)DohIdleTimer.Change(Timeout.Infinite,Timeout.Infinite);
    }
   }
   if(idle!=null && Stopwatch.GetTimestamp()-idle.ReturnedAt>=MaxDohIdleTicks) {
    CloseDohStream(idle.Stream);
    idle=null;
   }
   Stream stream=idle==null?null:idle.Stream;
   try {
    while(true) {
     if(stream==null)stream=connect(Remaining(watch,timeout));
     byte[] response;
     bool reusable;
     try {
      WriteHttpsRequest(stream,host,port,path,"POST",query,"Dagami-Agent-Proxy",true,watch,timeout);
      response=ReadResponse(stream,65535,"application/dns-message",watch,timeout,out reusable);
     } catch(IOException error) {
      if(idle==null || error is HttpsStatusException)throw;
      CloseDohStream(stream);
      stream=null;
      idle=null;
      continue;
     }
     ValidateDohResponse(query,response);
     if(reusable) {
      lock(PoolLock) {
       if(generation==poolGeneration && DohPool.Count<MaxIdleDoh && !DohPool.ContainsKey(key)) {
        if(DohPool.Count==0)DohIdleTimer.Change(MaxDohIdleMs,1000);
        DohPool.Add(key,new IdleDoh{Stream=stream,ReturnedAt=Stopwatch.GetTimestamp()});
        stream=null;
       }
      }
     }
     return response;
    }
   } finally {
    CloseDohStream(stream);
   }
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
  sealed class HttpsStatusException:IOException {
   public HttpsStatusException():base("HTTPS resolver/source did not return HTTP 200."){}
  }
  static byte[] Exact(Stream stream,int count,Stopwatch watch,int timeout){byte[] bytes=new byte[count];int offset=0;while(offset<count){int remaining=Remaining(watch,timeout);if(stream.CanTimeout)stream.ReadTimeout=remaining;int n=stream.Read(bytes,offset,count-offset);if(n==0)throw new EndOfStreamException();offset+=n;}return bytes;}
  static string Line(Stream stream,byte[] data,int max,Stopwatch watch,int timeout) {
   for(int count=0;count<max;count++) {
    int remaining=Remaining(watch,timeout);
    if(stream.CanTimeout)stream.ReadTimeout=remaining;
    if(stream.Read(data,count,1)==0)throw new EndOfStreamException();
    if(count>0&&data[count-1]==13) {
     if(data[count]==10)return Encoding.ASCII.GetString(data,0,count-1);
     throw new InvalidDataException("Invalid HTTPS line ending.");
    }
    if(data[count]==10)throw new InvalidDataException("Invalid HTTPS line ending.");
   }
   throw new InvalidDataException("HTTPS header too large.");
  }

  static bool HttpToken(string value) {
   foreach(char c in value) {
    if((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||
       "!#$%&'*+-.^`_|~".IndexOf(c)>=0)continue;
    return false;
   }
   return true;
  }

  static bool HasConnectionOption(string options,string option) {
   return Array.Exists(options.Split(','),token=>token.Trim(' ','\t').Equals(option,StringComparison.OrdinalIgnoreCase));
  }

  internal static byte[] ReadResponse(Stream stream,int limit,string expectedType,Stopwatch watch,int timeout,out bool reusable) {
   reusable=false;
   var lineBuffer=new byte[8192];
   string status=Line(stream,lineBuffer,4096,watch,timeout);if(!status.StartsWith("HTTP/1.1 200 ",StringComparison.Ordinal)&&!status.StartsWith("HTTP/1.0 200 ",StringComparison.Ordinal))throw new HttpsStatusException();
   var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int total=status.Length;
   while(true) {
    string line=Line(stream,lineBuffer,lineBuffer.Length,watch,timeout);
    total+=line.Length+2;
    if(total>16384)throw new InvalidDataException("HTTPS headers too large.");
    if(line.Length==0)break;
    int colon=line.IndexOf(':');
    if(colon<1)throw new InvalidDataException("Invalid HTTPS header.");
    string name=line.Substring(0,colon),value=line.Substring(colon+1).Trim(' ','\t');
    if(!HttpToken(name)||Array.Exists(value.ToCharArray(),c=>(c<32&&c!='\t')||c==127))
     throw new InvalidDataException("Invalid HTTPS header.");
    if(!headers.ContainsKey(name)) {
     headers.Add(name,value);
    } else if(name.Equals("Connection",StringComparison.OrdinalIgnoreCase)) {
     headers[name]+=","+value;
    } else if(name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)||
              name.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase)||
              name.Equals("Content-Type",StringComparison.OrdinalIgnoreCase)||
              name.Equals("Content-Encoding",StringComparison.OrdinalIgnoreCase)) {
     throw new InvalidDataException("Duplicate HTTPS framing header.");
    }
   }
   string type;if(expectedType!=null&&(!headers.TryGetValue("Content-Type",out type)||!type.Split(';')[0].Trim().Equals(expectedType,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Unexpected DoH content type.");
   string encoding;if(headers.TryGetValue("Content-Encoding",out encoding)&&!encoding.Equals("identity",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected HTTPS content encoding.");
   string length,transfer;bool hasLength=headers.TryGetValue("Content-Length",out length),hasTransfer=headers.TryGetValue("Transfer-Encoding",out transfer);
   if(hasLength&&hasTransfer)throw new InvalidDataException("Ambiguous HTTPS body framing.");
   if(hasLength){int count;if(!int.TryParse(length,NumberStyles.None,CultureInfo.InvariantCulture,out count)||count>limit)throw new InvalidDataException("Invalid HTTPS body length.");
    byte[] response=Exact(stream,count,watch,timeout);
    string connection;
    reusable=status.StartsWith("HTTP/1.1 ",StringComparison.Ordinal)&&
     (!headers.TryGetValue("Connection",out connection)||
      (!HasConnectionOption(connection,"close")&&!HasConnectionOption(connection,"upgrade")));
    return response;
   }
   using(var output=new MemoryStream()) {
    if(hasTransfer){
     if(!transfer.Equals("chunked",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsupported HTTPS framing.");
     while(true){string chunk=Line(stream,lineBuffer,128,watch,timeout);int size;if(!int.TryParse(chunk.Split(';')[0],NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out size)||size<0||size>limit-output.Length)throw new InvalidDataException("Invalid HTTPS chunk.");if(size==0){int trailerBytes=0;while(true){string trailer=Line(stream,lineBuffer,lineBuffer.Length,watch,timeout);trailerBytes+=trailer.Length+2;if(trailerBytes>16384)throw new InvalidDataException("HTTPS trailers too large.");if(trailer.Length==0)break;}break;}byte[] bytes=Exact(stream,size,watch,timeout);output.Write(bytes,0,bytes.Length);byte[] end=Exact(stream,2,watch,timeout);if(end[0]!=13||end[1]!=10)throw new InvalidDataException("Invalid HTTPS chunk ending.");}
    }else{
     byte[] buffer=new byte[4096];while(true){int remaining=Remaining(watch,timeout);if(stream.CanTimeout)stream.ReadTimeout=remaining;int count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,limit-output.Length+1));if(count==0)break;if(output.Length+count>limit)throw new InvalidDataException("HTTPS body too large.");output.Write(buffer,0,count);}
    }
    return output.ToArray();
   }
  }
  public static byte[] Request(string address,string host,string path,string method,byte[] body,string expectedType,int limit,int timeout=6000,int port=443,bool? obfuscationOverride=null) {
   IPAddress ip=ValidateHttpsTarget(address,host,path);
   var watch=Stopwatch.StartNew();
   var config=ConfigManager.Instance;
   using(var tls=OpenHttps(ip,host,port,watch,timeout,obfuscationOverride??config.DnsObfuscation)) {
    string ua=config.DnsObfuscation?"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36":"Dagami-Agent-Proxy";
    WriteHttpsRequest(tls,host,port,path,method,body,ua,false,watch,timeout);
    bool reusable;
    return ReadResponse(tls,limit,expectedType,watch,timeout,out reusable);
   }
  }

  static IPAddress ValidateHttpsTarget(string address,string host,string path) {
   IPAddress ip;if(!IPAddress.TryParse(address,out ip))throw new ArgumentException("HTTPS endpoint must be a literal IP.");
   if(Uri.CheckHostName(host)==UriHostNameType.Unknown||!path.StartsWith("/",StringComparison.Ordinal)||path.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("Invalid HTTPS authority/path.");
   return ip;
  }

  static void WriteHttpsRequest(Stream stream,string host,int port,string path,string method,byte[] body,string ua,bool keepAlive,Stopwatch watch,int timeout) {
   string authority=Uri.CheckHostName(host)==UriHostNameType.IPv6?"["+host.Trim('[',']')+"]":host;
   if(port>0&&port!=443)authority+=":"+port.ToString(CultureInfo.InvariantCulture);
   string header=method+" "+path+" HTTP/1.1\r\nHost: "+authority+"\r\nConnection: "+(keepAlive?"keep-alive":"close")+"\r\nUser-Agent: "+ua+"\r\nAccept-Encoding: identity\r\n";
   if(body!=null)header+="Content-Type: application/dns-message\r\nAccept: application/dns-message\r\nContent-Length: "+body.Length+"\r\n";
   byte[] bytes=Encoding.ASCII.GetBytes(header+"\r\n");
   stream.WriteTimeout=Remaining(watch,timeout);
   stream.Write(bytes,0,bytes.Length);
   if(body!=null)stream.Write(body,0,body.Length);
   stream.Flush();
  }

  static Stream OpenHttps(IPAddress ip,string host,int port,Stopwatch watch,int timeout,bool obfuscate) {
   var client=new TcpClient(ip.AddressFamily);
   try {
    client.NoDelay=true;
    var pending=client.BeginConnect(ip,port>0?port:443,null,null);using(pending.AsyncWaitHandle){if(!pending.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException("HTTPS connection timeout.");client.EndConnect(pending);}
    Stream net=client.GetStream();
    if(obfuscate){
     var dpiMode=DpiDesyncStream.ParseMode(ConfigManager.Instance.DpiDesyncMode);
     if(dpiMode!=DpiDesyncMode.Off&&dpiMode!=DpiDesyncMode.ExternalEngine)net=new DpiDesyncStream(net,client.Client,dpiMode);
    }
    // Online CRL retrieval itself may require this DNS service. Keep OS trust,
    // certificate dates and hostname validation, without recursive online CRL.
    return TlsDiagnostics.Connect(net,host,"dns-https",Remaining(watch,timeout),false);
   } catch {
    client.Close();
    throw;
   }
  }
 }
}
