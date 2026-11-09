using System;
using System.Collections.Generic;

namespace DevProxyApp {
 // Offline, deterministic packet transform. This type does not capture or send traffic.
 // Only complete, unfragmented IPv4/IPv6 TCP packets containing a complete first
 // ClientHello are eligible. Unsupported packets must be forwarded unchanged.
 public static class DpiPacketSplitter {
  static int U16(byte[] b,int p){return (b[p]<<8)|b[p+1];}
  static uint U32(byte[] b,int p){return ((uint)b[p]<<24)|((uint)b[p+1]<<16)|((uint)b[p+2]<<8)|b[p+3];}
  static void W16(byte[] b,int p,int x){b[p]=(byte)(x>>8);b[p+1]=(byte)x;}
  static void W32(byte[] b,int p,uint x){b[p]=(byte)(x>>24);b[p+1]=(byte)(x>>16);b[p+2]=(byte)(x>>8);b[p+3]=(byte)x;}
  static uint Sum(uint sum,byte[] bytes,int start,int length){int end=start+length;while(start+1<end){sum+=(uint)((bytes[start]<<8)|bytes[start+1]);start+=2;}if(start<end)sum+=(uint)(bytes[start]<<8);return sum;}
  static ushort Finish(uint n){while((n>>16)!=0)n=(n&65535)+(n>>16);return (ushort)~n;}
  // Splits at two TCP payload offsets, preserving TCP sequence space. TLS record
  // rewriting is intentionally not done here: it changes stream length and needs
  // bidirectional sequence translation, retransmission state and ACK adjustment.
  public static bool TrySplit(byte[] packet,int destinationPort,string expectedHost,out byte[][] segments){
   if(destinationPort<1||destinationPort>65535||string.IsNullOrEmpty(expectedHost)){
    segments=null;
    return false;
   }
   return TrySplitCore(packet,destinationPort,expectedHost,out segments);
  }

  static bool TrySplitCore(byte[] packet,int destinationPort,string expectedHost,out byte[][] segments){
   segments=null;if(packet==null||packet.Length<40)return false;
   int version=packet[0]>>4,ip,tcp,total;bool v6=version==6;
   if(version==4){
    ip=(packet[0]&15)*4;if(ip!=20||packet.Length<ip+20||packet[9]!=6||U16(packet,2)!=packet.Length)return false;
    if((U16(packet,6)&0x3fff)!=0)return false;total=packet.Length;tcp=ip;
   }else if(v6){
    if(packet.Length<60||packet[6]!=6||U16(packet,4)+40!=packet.Length)return false;
    ip=40;total=packet.Length;tcp=40;
   }else return false;
   if(U16(packet,tcp+2)!=destinationPort)return false;
   int th=(packet[tcp+12]>>4)*4;if(th<20||tcp+th>total)return false;
   // TCP MD5 and TCP-AO authenticate sequence numbers and payload. A split
   // would invalidate them. Allow only padding and the usual TCP timestamp.
   for(int option=tcp+20;option<tcp+th;){
    int kind=packet[option++];
    if(kind==0)break;
    if(kind==1)continue;
    if(option>=tcp+th||kind!=8||packet[option]!=10||option+9>tcp+th)return false;
    option+=9;
   }
   byte flags=packet[tcp+13];if((flags&0x27)!=0||(flags&0x10)==0)return false; // SYN/FIN/RST/URG or no ACK
   int data=tcp+th,length=total-data;if(length<6)return false;
   string host;int start,mid;if(!TlsClientHelloInspector.TryFindSni(packet,data,length,out host,out start,out mid))return false;
   if(!string.Equals(host,expectedHost,StringComparison.OrdinalIgnoreCase))return false;
   // An explicit payload-only split at byte 2 and within SNI, not TCP writes.
   if(mid<=2||mid>=length)return false;
   int[] cuts={0,2,mid,length};var result=new List<byte[]>();uint seq=U32(packet,tcp+4);
   for(int i=0;i<3;i++){
    int payload=cuts[i+1]-cuts[i];byte[] part=new byte[data+payload];Buffer.BlockCopy(packet,0,part,0,data);Buffer.BlockCopy(packet,data+cuts[i],part,data,payload);
    W32(part,tcp+4,unchecked(seq+(uint)cuts[i]));if(i<2)part[tcp+13]=(byte)(flags&~0x08);
    if(v6)W16(part,4,part.Length-40);else {W16(part,2,part.Length);W16(part,4,(U16(packet,4)+i)&65535);W16(part,10,0);W16(part,10,Finish(Sum(0,part,0,ip)));}
    W16(part,tcp+16,0);uint pseudo=0;int tcpLength=part.Length-tcp;
    if(v6){pseudo=Sum(0,part,8,32);pseudo+=(uint)(tcpLength>>16);pseudo+=(uint)(tcpLength&65535);pseudo+=6;}
    else {pseudo=Sum(0,part,12,8);pseudo+=6;pseudo+=(uint)tcpLength;}
    W16(part,tcp+16,Finish(Sum(pseudo,part,tcp,tcpLength)));result.Add(part);
   }
   segments=result.ToArray();return true;
  }
 }
}
