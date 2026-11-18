using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DevProxyApp
{
    /// <summary>
    /// Detects processes listening on local UDP or TCP port 53.
    /// Uses native iphlpapi.dll calls (GetExtendedTcpTable / GetExtendedUdpTable)
    /// without relying on WMI/CIM, so it responds reliably even when WMI is broken.
    /// </summary>
    public static class LocalPort53
    {
        public sealed class Holder
        {
            public string Protocol { get; set; }
            public string Address { get; set; }
            public int Port { get; set; }
            public int Pid { get; set; }
            public string ProcessName { get; set; }

            public override string ToString()
            {
                string host = Address.Contains(":") ? "[" + Address + "]" : Address;
                return ProcessName + " (PID " + Pid + "), " + Protocol + " " + host + ":" + Port;
            }
        }

        const int AF_INET = 2;
        const int AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        const int UDP_TABLE_OWNER_PID = 1;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        /// <summary>
        /// Finds all processes listening on port 53 (TCP/UDP, IPv4/IPv6).
        /// </summary>
        public static List<Holder> Find(int port = 53)
        {bool complete;return Find(port,out complete);}

        public static List<Holder> Find(int port,out bool complete)
        {
            complete=true;
            var found = new List<Holder>();
            int[] afList = new int[] { AF_INET, AF_INET6 };

            for (int i = 0; i < afList.Length; i++)
            {
                int af = afList[i];
                byte[] tcpTable = ReadTable(true, af);
                if(tcpTable==null)complete=false;
                if (tcpTable != null) found.AddRange(Parse(tcpTable, true, af, port));

                byte[] udpTable = ReadTable(false, af);
                if(udpTable==null)complete=false;
                if (udpTable != null) found.AddRange(Parse(udpTable, false, af, port));
            }

            var nameCache = new Dictionary<int, string>();
            foreach (var h in found)
            {
                string procName;
                if (!nameCache.TryGetValue(h.Pid, out procName))
                {
                    procName = ResolveProcessName(h.Pid);
                    nameCache[h.Pid] = procName;
                }
                h.ProcessName = procName;
            }

            return DistinctHolders(found);
        }

        public static string Describe(IEnumerable<Holder> holders)
        {
            var parts = new List<string>();
            foreach (var h in holders) parts.Add(h.ToString());
            return parts.Count > 0 ? string.Join("; ", parts.ToArray()) : "свободен";
        }

        private static byte[] ReadTable(bool tcp, int af)
        {
            int size = 0;
            IntPtr buf = IntPtr.Zero;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (buf != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buf);
                        buf = IntPtr.Zero;
                    }

                    buf = Marshal.AllocHGlobal(Math.Max(size, 4));
                    uint rc = tcp
                        ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0)
                        : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);

                    if (rc == 0)
                    {
                        byte[] bytes = new byte[size];
                        Marshal.Copy(buf, bytes, 0, size);
                        return bytes;
                    }

                    if (rc != ERROR_INSUFFICIENT_BUFFER) return null;
                }
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        public static List<Holder> Parse(byte[] table, bool tcp, int af, int port)
        {
            var result = new List<Holder>();
            if (table == null || table.Length < 4) return result;

            int count = BitConverter.ToInt32(table, 0);
            bool v4 = af == AF_INET;
            int rowSize = tcp ? (v4 ? 24 : 56) : (v4 ? 12 : 28);
            int addrOff = tcp ? (v4 ? 4 : 0) : 0;
            int portOff = tcp ? (v4 ? 8 : 20) : (v4 ? 4 : 20);
            int pidOff = tcp ? (v4 ? 20 : 52) : (v4 ? 8 : 24);
            int addrLen = v4 ? 4 : 16;

            for (int i = 0; i < count; i++)
            {
                int off = 4 + i * rowSize;
                if (off + rowSize > table.Length) break;

                int rawPort = BitConverter.ToInt32(table, off + portOff);
                int p = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                if (p != port) continue;

                byte[] addr = new byte[addrLen];
                Array.Copy(table, off + addrOff, addr, 0, addrLen);
                int pid = BitConverter.ToInt32(table, off + pidOff);

                result.Add(new Holder
                {
                    Protocol = tcp ? "TCP" : "UDP",
                    Address = new IPAddress(addr).ToString(),
                    Port = p,
                    Pid = pid
                });
            }

            return result;
        }

        private static string ResolveProcessName(int pid)
        {
            if (pid == 0) return "System Idle Process";
            if (pid == 4) return "System";
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    try
                    {
                        return p.MainModule != null ? p.MainModule.ModuleName : p.ProcessName;
                    }
                    catch
                    {
                        return p.ProcessName;
                    }
                }
            }
            catch
            {
                return "PID " + pid;
            }
        }

        private static List<Holder> DistinctHolders(List<Holder> list)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Holder>();
            foreach (var h in list)
            {
                string key = h.Protocol + "|" + h.Address + "|" + h.Port + "|" + h.Pid;
                if (set.Add(key)) result.Add(h);
            }
            return result;
        }
    }
}
