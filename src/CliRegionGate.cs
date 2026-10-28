using System;
using System.IO;
using System.Text;

namespace DevProxyApp {
 // Match a single, independently checked local eligibility branch. Never patch
 // a generic conditional jump or subscription/authentication response handler.
 internal static class CliRegionGate {
  static bool Contains(byte[] data,string text){byte[] needle=Encoding.ASCII.GetBytes(text);for(int offset=0;offset<=data.Length-needle.Length;offset++){if(data[offset]!=needle[0])continue;int index=1;while(index<needle.Length&&data[offset+index]==needle[index])index++;if(index==needle.Length)return true;}return false;}
  internal static int? Find(byte[] data){
   if(data==null||data.Length<128||data[0]!=0x4d||data[1]!=0x5a)return null;
   if(!Contains(data,"prompt-interactive"))return null;
   if((!Contains(data,"Go build ID:")&&!Contains(data,"Go buildinf:"))||!Contains(data,"Eligibility check failed:"))throw new UnsupportedAntigravityBinaryException("CLI: неизвестные признаки сборки. Файл не изменён.");
   int pe=BitConverter.ToInt32(data,0x3c);
   if(pe<0||pe>data.Length-24||BitConverter.ToUInt32(data,pe)!=0x4550)throw new UnsupportedAntigravityBinaryException("CLI: повреждённый PE-заголовок.");
   if(BitConverter.ToUInt16(data,pe+4)!=0x8664)throw new UnsupportedAntigravityBinaryException("Для этой архитектуры CLI региональная проверка ещё не подтверждена. Файл не изменён.");
   int count=BitConverter.ToUInt16(data,pe+6),table=pe+24+BitConverter.ToUInt16(data,pe+20);
   if(count<1||count>96||table<0||table>data.Length-count*40)throw new UnsupportedAntigravityBinaryException("CLI: некорректная таблица секций.");
   int found=-1;
   for(int section=0;section<count;section++){
    int entry=table+section*40;if((BitConverter.ToUInt32(data,entry+36)&0x20000000)==0)continue;
    uint raw=BitConverter.ToUInt32(data,entry+20),size=BitConverter.ToUInt32(data,entry+16);if(raw>data.Length||size>data.Length-raw)throw new UnsupportedAntigravityBinaryException("CLI: секция выходит за границы файла.");
    int end=(int)(raw+size);
    for(int offset=(int)raw;offset<=end-30;offset++){
     if(data[offset]!=0x48||data[offset+1]!=0x85||data[offset+2]!=0xc0||data[offset+3]!=0x0f||data[offset+4]!=0x84||data[offset+9]!=0x80||data[offset+10]!=0x78||data[offset+11]!=8||data[offset+12]!=0||data[offset+13]!=0x0f||data[offset+14]!=0x85)continue;
     // The failure path reads the eligibility result's reason field (+0x50)
     // and addresses its formatter. A generic test/cmp pair is insufficient.
     if(data[offset+19]!=0x48||data[offset+20]!=0x8b||data[offset+21]!=0x50||data[offset+22]!=0x50||data[offset+23]!=0x4c||data[offset+24]!=0x8d||data[offset+25]!=0x1d)continue;
     long nilTarget=(long)offset+9+BitConverter.ToInt32(data,offset+5),eligibleTarget=(long)offset+19+BitConverter.ToInt32(data,offset+15);
     if(nilTarget!=eligibleTarget||nilTarget<offset+19||nilTarget>=end||nilTarget>offset+4096)continue;
     if(found>=0)throw new UnsupportedAntigravityBinaryException("CLI: неоднозначная региональная проверка. Файл не изменён.");found=offset+9;
    }
   }
   if(found<0)throw new UnsupportedAntigravityBinaryException("CLI: неизвестная или уже изменённая региональная проверка. Файл не изменён.");return found;
  }
 }
}
