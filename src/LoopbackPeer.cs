using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DevProxyApp {
    // TCP loopback does not isolate Windows users. Inspect the CLIENT-side tuple,
    // not the accepted socket (whose PID is always this proxy).
    public static class LoopbackPeer {
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int kind, uint reserved);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        static int Port(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }
        public static bool IsCurrentUser(TcpClient client) {
            try {
                var peer=(IPEndPoint)client.Client.RemoteEndPoint;
                var local=(IPEndPoint)client.Client.LocalEndPoint;
                if(peer.AddressFamily!=AddressFamily.InterNetwork || !IPAddress.IsLoopback(peer.Address)) return false;
                int size=0;uint result=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0);
                if(result!=122 || size<4 || size>16*1024*1024) return false;
                IntPtr table=Marshal.AllocHGlobal(size);
                try {
                    if(GetExtendedTcpTable(table,ref size,false,2,5,0)!=0)return false;
                    int count=Marshal.ReadInt32(table);
                    if(count<0 || count>(size-4)/24)return false;
                    for(int i=0;i<count;i++) {
                        IntPtr row=IntPtr.Add(table,4+i*24);
                        if(Port(unchecked((uint)Marshal.ReadInt32(row,8)))!=peer.Port || Port(unchecked((uint)Marshal.ReadInt32(row,16)))!=local.Port)continue;
                        if(!new IPAddress(unchecked((uint)Marshal.ReadInt32(row,4))).Equals(peer.Address) || !new IPAddress(unchecked((uint)Marshal.ReadInt32(row,12))).Equals(local.Address))continue;
                        int pid=Marshal.ReadInt32(row,20);
                        IntPtr process=OpenProcess(0x1000,false,pid);
                        if(process==IntPtr.Zero)return false;
                        try {
                            IntPtr token;
                            if(!OpenProcessToken(process,8,out token))return false;
                            try {using(var identity=new WindowsIdentity(token))using(var current=WindowsIdentity.GetCurrent())return identity.User.Equals(current.User);}
                            finally {CloseHandle(token);}
                        } finally {CloseHandle(process);}
                    }
                } finally {Marshal.FreeHGlobal(table);}
            } catch { }
            return false; // missing/inaccessible identity is never permission
        }
    }
}
