using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

namespace DevProxyApp {
 internal static class WinDivertPacketTest {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode,SetLastError=true)]
  static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
  [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
  static extern IntPtr GetProcAddress(IntPtr module,string name);
  [DllImport("kernel32.dll")]
  static extern bool FreeLibrary(IntPtr module);

  [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)]
  delegate IntPtr Open(string filter,int layer,short priority,ulong flags);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)]
  delegate bool Receive(IntPtr handle,[Out] byte[] packet,uint capacity,out uint length,[Out] byte[] address);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)]
  delegate bool Send(IntPtr handle,byte[] packet,uint length,out uint sent,byte[] address);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)]
  delegate bool Control(IntPtr handle,int mode);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  delegate bool Close(IntPtr handle);

  static T Export<T>(IntPtr module,string name) where T:class {
   IntPtr address=GetProcAddress(module,name);
   if(address==IntPtr.Zero)throw new Win32Exception();
   return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));
  }

  static bool CheckTls(IPAddress ip,int sourcePort,int port,string host) {
   try {
    using(var client=new TcpClient(AddressFamily.InterNetwork))
    using(var deadline=new Timer(_=>client.Close(),null,7000,Timeout.Infinite)) {
     client.Client.Bind(new IPEndPoint(IPAddress.Any,sourcePort));
     client.Connect(ip,port);
     using(var tls=TlsDiagnostics.Connect(client.GetStream(),host,"packet-test",4500,false)) {
      byte[] request=System.Text.Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: "+host+"\r\nConnection: close\r\n\r\n");
      tls.Write(request,0,request.Length);
      using(var reader=new StreamReader(tls)) {
       string line=reader.ReadLine();
       Console.WriteLine("HTTPS result: "+line);
       return line!=null&&line.StartsWith("HTTP/",StringComparison.Ordinal);
      }
     }
    }
   } catch(Exception e) {
    Console.Error.WriteLine("HTTPS probe: "+e.Message);
    return false;
   }
  }

  static void Report(bool dialog,bool success,string text) {
   if(!dialog){Console.Error.WriteLine(text);return;}
   System.Windows.Forms.MessageBox.Show(text,"WinDivert",
       System.Windows.Forms.MessageBoxButtons.OK,
       success?System.Windows.Forms.MessageBoxIcon.Information:System.Windows.Forms.MessageBoxIcon.Error);
  }

  internal static int Run(string[] args) {
   bool install=args[0]=="--install-windivert";
   bool selfTest=args[0]=="--packet-test-local";
   bool allowInstall=selfTest||install;
   if(allowInstall) {
    string dll=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"Downloads\WinDivert-2.2.2-A\WinDivert-2.2.2-A\x64\WinDivert.dll");
    args=new[]{"--packet-test",dll,"C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2",
        "1.1.1.1","55043","443","cloudflare-dns.com"};
   }
   int sourcePort,destinationPort;
   IPAddress ip;
   if(args.Length!=7 || !Path.IsPathRooted(args[1]) ||
      !IPAddress.TryParse(args[3],out ip) || ip.AddressFamily!=AddressFamily.InterNetwork ||
      !int.TryParse(args[4],NumberStyles.None,CultureInfo.InvariantCulture,out sourcePort) ||
      !int.TryParse(args[5],NumberStyles.None,CultureInfo.InvariantCulture,out destinationPort) ||
      sourcePort<1||sourcePort>65535||destinationPort<1||destinationPort>65535||
      Uri.CheckHostName(args[6])!=UriHostNameType.Dns) {
    Console.Error.WriteLine("Usage: --packet-test <absolute WinDivert.dll> <sha256> <IPv4> <source-port> <destination-port> <SNI>");
    return 2;
   }
   if(!Environment.Is64BitProcess || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) {
    Report(install,false,"Для WinDivert нужны права администратора и 64-разрядная сборка Dagami.");
    return 5;
   }
   try {
    using(var driver=allowInstall?File.Open(Path.Combine(Path.GetDirectoryName(args[1]),"WinDivert64.sys"),
        FileMode.Open,FileAccess.Read,FileShare.Read):null)
    using(var file=File.Open(args[1],FileMode.Open,FileAccess.Read,FileShare.Read))
    using(var sha=SHA256.Create()) {
     if(driver!=null && BitConverter.ToString(sha.ComputeHash(driver)).Replace("-","")!=
         "8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2")
      throw new IOException("WinDivert64.sys hash mismatch.");
     string actual=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
     if(!actual.Equals(args[2],StringComparison.OrdinalIgnoreCase))throw new IOException("WinDivert DLL hash mismatch.");
     IntPtr module=LoadLibraryEx(Path.GetFullPath(args[1]),IntPtr.Zero,0x800); // SYSTEM32 dependencies
     if(module==IntPtr.Zero)throw new Win32Exception();
     try {
      var open=Export<Open>(module,"WinDivertOpen");
      var receive=Export<Receive>(module,"WinDivertRecv");
      var send=Export<Send>(module,"WinDivertSend");
      var shutdown=Export<Control>(module,"WinDivertShutdown");
      var close=Export<Close>(module,"WinDivertClose");
      string filter="outbound and !loopback and !impostor and ip and tcp and tcp.PayloadLength > 0 and ip.DstAddr == "+ip+
          " and tcp.SrcPort == "+sourcePort+" and tcp.DstPort == "+destinationPort;
      // The explicitly launched local test may load the pinned driver on demand.
      // The general packet test requires a pre-existing service.
      IntPtr handle=open(install?"false":filter,0,0,allowInstall?0UL:16UL); // WINDIVERT_FLAG_NO_INSTALL
      if(handle==new IntPtr(-1)||handle==IntPtr.Zero)throw new Win32Exception();
      if(install) {
       if(!close(handle))throw new Win32Exception();
       Report(true,true,"WinDivert загружен. Можно запустить пакетный тест.");
       return 0;
      }
      bool closedByTimer=false;
      try {
       int split=0;
       System.Threading.Tasks.Task<bool> probe=null;
       using(var timer=new Timer(_=>{
        if(!shutdown(handle,1)){closedByTimer=true;close(handle);}
       },null,15000,Timeout.Infinite)) {
        try {
         Console.WriteLine("Capture active for 15 seconds.");
         if(selfTest)probe=System.Threading.Tasks.Task.Run(()=>CheckTls(ip,sourcePort,destinationPort,args[6]));
         byte[] buffer=new byte[65535], address=new byte[80];
         while(true) {
          uint size;
          if(!receive(handle,buffer,(uint)buffer.Length,out size,address)) {
           if(Marshal.GetLastWin32Error()==232)break;
           throw new Win32Exception();
          }
          byte[] packet=new byte[size];Buffer.BlockCopy(buffer,0,packet,0,(int)size);
          byte[][] segments;
          bool changed=DpiPacketSplitter.TrySplit(packet,destinationPort,args[6],out segments);
          if(!changed)segments=new[]{packet};
          else address[10]|=0x60; // IPv4 and TCP checksums were recalculated
          foreach(byte[] segment in segments) {
           uint sent;
           if(!send(handle,segment,(uint)segment.Length,out sent,address)||sent!=segment.Length)throw new Win32Exception();
          }
          if(changed)split++;
         }
        } finally {
         using(var stopped=new ManualResetEvent(false)) {timer.Dispose(stopped);stopped.WaitOne();}
        }
       }
       Console.WriteLine("ClientHello packets split and reinjected: "+split);
       return split>0&&(probe==null||probe.GetAwaiter().GetResult())?0:3;
      } finally {if(!closedByTimer)close(handle);}
     } finally {FreeLibrary(module);}
    }
   } catch(Exception error) {
    Report(install,false,error.Message);
    return 1;
   }
  }
 }
}
