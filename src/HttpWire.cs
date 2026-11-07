using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DevProxyApp {
    // Exactly one HTTP transaction per socket. Only a verified 101 becomes a raw
    // duplex stream. Never forward pipelined requests without proxy auth/routing.
    public static class HttpWire {
        static List<KeyValuePair<string,string>> Fields(string headers) {
            var result=new List<KeyValuePair<string,string>>();
            string[] lines=headers.Split(new[]{"\r\n"},StringSplitOptions.None);
            for(int i=1;i<lines.Length;i++) {
                if(lines[i].Length==0)break;
                int colon=lines[i].IndexOf(':');
                if(colon<=0 || lines[i][0]==' ' || lines[i][0]=='\t')throw new InvalidDataException("Invalid HTTP header.");
                string name=lines[i].Substring(0,colon);
                foreach(char c in name)if(!char.IsLetterOrDigit(c)&&"!#$%&'*+-.^_`|~".IndexOf(c)<0)throw new InvalidDataException("Invalid HTTP header name.");
                result.Add(new KeyValuePair<string,string>(name,lines[i].Substring(colon+1).Trim()));
            }
            return result;
        }
        static string Value(List<KeyValuePair<string,string>> fields,string name) {
            string value=null;
            foreach(var pair in fields)if(pair.Key.Equals(name,StringComparison.OrdinalIgnoreCase)){if(value!=null)throw new InvalidDataException("Duplicate HTTP framing header.");value=pair.Value;}
            return value;
        }
        static string First(string headers){return headers.Substring(0,headers.IndexOf("\r\n",StringComparison.Ordinal));}
        public static string RequestHeaders(string headers,Uri destination,bool absolute,string auth) {
            var fields=Fields(headers);Framing(fields);
            string[] first=First(headers).Split(' ');if(first.Length!=3)throw new InvalidDataException("Invalid HTTP request.");
            string expect=Value(fields,"expect");if(expect!=null&&!expect.Equals("100-continue",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsupported Expect header.");
            bool upgrade=Value(fields,"upgrade")!=null;
            var result=new StringBuilder(first[0]+" "+(absolute?destination.AbsoluteUri:destination.PathAndQuery)+" "+first[2]+"\r\nHost: "+destination.Authority+"\r\n");
            foreach(var pair in fields) {
                string name=pair.Key.ToLowerInvariant();
                if(name=="host"||name=="connection"||name=="proxy-connection"||name=="proxy-authorization"||name=="expect")continue;
                result.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }
            if(!string.IsNullOrEmpty(auth))result.Append("Proxy-Authorization: ").Append(auth).Append("\r\n");
            return result.Append(upgrade?"Connection: Upgrade\r\n\r\n":"Connection: close\r\n\r\n").ToString();
        }
        static long Framing(List<KeyValuePair<string,string>> fields) {
            string length=Value(fields,"content-length"),transfer=Value(fields,"transfer-encoding");
            if(transfer!=null){if(length!=null||!transfer.Equals("chunked",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Ambiguous HTTP body framing.");return -2;}
            long size;if(length==null)return -1;
            if(length.Length==0||!long.TryParse(length,NumberStyles.None,CultureInfo.InvariantCulture,out size)||size<0)throw new InvalidDataException("Invalid Content-Length.");
            return size;
        }
        static void Write(Stream output,string value){byte[] bytes=Encoding.ASCII.GetBytes(value);output.Write(bytes,0,bytes.Length);output.Flush();}
        static void Exact(Stream input,Stream output,long length) {
            byte[] buffer=new byte[16384];
            while(length>0){int n=input.Read(buffer,0,(int)Math.Min(buffer.Length,length));if(n==0)throw new EndOfStreamException("Truncated HTTP body.");output.Write(buffer,0,n);length-=n;}
        }
        static string Line(Stream input,int limit) {
            var result=new StringBuilder();
            while(result.Length<limit){int c=input.ReadByte();if(c<0)throw new EndOfStreamException();result.Append((char)c);if(result.Length>=2&&result[result.Length-2]=='\r'&&c=='\n')return result.ToString();}
            throw new InvalidDataException("HTTP line too large.");
        }
        static void Body(Stream input,Stream output,long framing,bool eof) {
            if(framing>=0){Exact(input,output,framing);return;}
            if(framing==-2) {
                while(true){string line=Line(input,8192);string number=line.TrimEnd('\r','\n').Split(';')[0];long size;
                    if(number.Length==0||number.Length>16||!long.TryParse(number,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out size)||size<0)throw new InvalidDataException("Invalid HTTP chunk.");
                    Write(output,line);
                    if(size==0){int total=0;while(true){string trailer=Line(input,8192);total+=trailer.Length;if(total>65536)throw new InvalidDataException("HTTP trailers too large.");Write(output,trailer);if(trailer=="\r\n")return;}}
                    Exact(input,output,size);if(Line(input,2)!="\r\n")throw new InvalidDataException("Invalid chunk terminator.");Write(output,"\r\n");
                }
            }
            if(eof){byte[] buffer=new byte[16384];int n;while((n=input.Read(buffer,0,buffer.Length))>0)output.Write(buffer,0,n);}
        }
        static string Header(Stream input) {
            var result=new StringBuilder();
            while(result.Length<65536){string line=Line(input,8192);result.Append(line);if(line=="\r\n")return result.ToString();}
            throw new InvalidDataException("HTTP headers too large.");
        }
        public static bool Exchange(Stream client,Stream upstream,string original,string forwarded,byte[] initial,Action<Stream,Stream> upgrade,Action started) {
            var requestFields=Fields(original);long requestBody=Framing(requestFields);
            Stream input=initial.Length==0?client:new InitialStream(client,initial);
            Write(upstream,forwarded);
            if(Value(requestFields,"expect")!=null){started();Write(client,"HTTP/1.1 100 Continue\r\n\r\n");}
            // A ConnectRPC client can wait for the response before finishing its
            // chunked request. Keep one framed upload concurrent with the reply.
            Thread sender=null;
            if(requestBody>0||requestBody==-2) {
                sender=new Thread(()=>{try{Body(input,upstream,requestBody,false);upstream.Flush();}catch{try{upstream.Dispose();}catch{}}});
                sender.IsBackground=true;sender.Start();
            }
            if(upstream.CanTimeout)upstream.ReadTimeout=System.Threading.Timeout.Infinite;
            for(int responses=0;responses<32;responses++) {
                string headers=Header(upstream);string[] status=First(headers).Split(' ');int code;
                if(status.Length<2||(status[0]!="HTTP/1.0"&&status[0]!="HTTP/1.1")||!int.TryParse(status[1],out code)||code<100||code>599)throw new InvalidDataException("Invalid HTTP response.");
                var fields=Fields(headers);long body=Framing(fields);
                if(code==101){if(Value(requestFields,"upgrade")==null||(sender!=null&&!sender.Join(5000)))throw new InvalidDataException("Invalid HTTP upgrade.");started();Write(client,headers);upgrade(input,upstream);return true;}
                var output=new StringBuilder(First(headers)+"\r\n");
                foreach(var pair in fields)if(!pair.Key.Equals("connection",StringComparison.OrdinalIgnoreCase)&&!pair.Key.Equals("proxy-connection",StringComparison.OrdinalIgnoreCase))output.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                started();Write(client,output.Append(code<200?"\r\n":"Connection: close\r\n\r\n").ToString());
                if(code<200)continue;
                if(!First(original).StartsWith("HEAD ",StringComparison.OrdinalIgnoreCase)&&code!=204&&code!=304)Body(upstream,client,body,true);
                client.Flush();return false;
            }
            throw new InvalidDataException("Too many informational responses.");
        }
        sealed class InitialStream:Stream {
            readonly Stream source;readonly byte[] initial;int offset;
            public InitialStream(Stream source,byte[] initial){this.source=source;this.initial=initial;}
            public override int Read(byte[] buffer,int index,int count){if(offset<initial.Length){int n=Math.Min(count,initial.Length-offset);Buffer.BlockCopy(initial,offset,buffer,index,n);offset+=n;return n;}return source.Read(buffer,index,count);}
            public override void Write(byte[] b,int o,int c){source.Write(b,o,c);}public override void Flush(){source.Flush();}
            public override bool CanRead{get{return true;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long v){throw new NotSupportedException();}
        }
    }
}
