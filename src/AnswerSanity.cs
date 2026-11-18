using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace DevProxyApp
{
    /// <summary>
    /// Checks whether resolved IP addresses belong to bogon, private, loopback,
    /// or reserved ranges that should never be returned for public services.
    /// In Russia, ISPs and DPI equipment often spoof DNS answers with stub IPs
    /// (e.g. 127.0.0.1, 0.0.0.0, 10.x.x.x, or ISP blockpage IPs).
    /// </summary>
    public static class AnswerSanity
    {
        public const string Code = "BOGUS_ANSWER";

        struct BogonRange
        {
            public byte[] Net;
            public int Bits;
            public BogonRange(string ip, int bits)
            {
                Net = IPAddress.Parse(ip).GetAddressBytes();
                Bits = bits;
            }
        }

        static readonly BogonRange[] V4Ranges = new[]
        {
            new BogonRange("0.0.0.0", 8),
            new BogonRange("127.0.0.0", 8),
            new BogonRange("169.254.0.0", 16),
            new BogonRange("10.0.0.0", 8),
            new BogonRange("172.16.0.0", 12),
            new BogonRange("192.168.0.0", 16),
            new BogonRange("100.64.0.0", 10),
            new BogonRange("224.0.0.0", 4),
            new BogonRange("240.0.0.0", 4),
            new BogonRange("255.255.255.255", 32)
        };

        static readonly BogonRange[] V6Ranges = new[]
        {
            new BogonRange("::", 128),
            new BogonRange("::1", 128),
            new BogonRange("fe80::", 10),
            new BogonRange("fc00::", 7),
            new BogonRange("ff00::", 8)
        };

        public static bool IsBogus(IPAddress ip)
        {
            if (ip == null) return false;
            if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
            if(ip.AddressFamily==AddressFamily.InterNetwork&&!DnsProviders.PublicAddress(ip.ToString()))return true;
            var ranges = ip.AddressFamily == AddressFamily.InterNetwork ? V4Ranges :
                         ip.AddressFamily == AddressFamily.InterNetworkV6 ? V6Ranges : null;
            if (ranges == null) return false;

            byte[] addr = ip.GetAddressBytes();
            for (int r = 0; r < ranges.Length; r++)
            {
                if (Matches(addr, ranges[r].Net, ranges[r].Bits)) return true;
            }
            return false;
        }

        public static bool IsBogus(string text)
        {
            IPAddress ip;
            if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out ip)) return false;
            return IsBogus(ip);
        }

        private static bool Matches(byte[] addr, byte[] net, int bits)
        {
            if (addr.Length != net.Length) return false;
            int fullBytes = bits / 8;
            for (int i = 0; i < fullBytes; i++)
            {
                if (addr[i] != net[i]) return false;
            }
            int rem = bits % 8;
            if (rem == 0) return true;
            int mask = (0xFF << (8 - rem)) & 0xFF;
            return (addr[fullBytes] & mask) == (net[fullBytes] & mask);
        }

        /// <summary>
        /// Safely extracts all IPv4 (A) and IPv6 (AAAA) addresses from a standard DNS response packet.
        /// </summary>
        public static List<IPAddress> ExtractAddresses(byte[] packet)
        {
            var result = new List<IPAddress>();
            if (packet == null || packet.Length < 12) return result;

            try
            {
                int qCount = (packet[4] << 8) | packet[5];
                int anCount = (packet[6] << 8) | packet[7];
                if (anCount <= 0) return result;

                int offset = 12;
                // Skip Questions
                for (int q = 0; q < qCount; q++)
                {
                    offset = SkipDnsName(packet, offset);
                    if (offset + 4 > packet.Length) return result;
                    offset += 4; // qtype (2) + qclass (2)
                }

                // Parse Answers
                for (int a = 0; a < anCount; a++)
                {
                    if (offset >= packet.Length) break;
                    offset = SkipDnsName(packet, offset);
                    if (offset + 10 > packet.Length) break;

                    int type = (packet[offset] << 8) | packet[offset + 1];
                    int cls = (packet[offset + 2] << 8) | packet[offset + 3];
                    // ttl = 4 bytes at offset+4
                    int rdLen = (packet[offset + 8] << 8) | packet[offset + 9];
                    offset += 10;

                    if (offset + rdLen > packet.Length) break;

                    // Internet class (1)
                    if (cls == 1)
                    {
                        if (type == 1 && rdLen == 4) // A
                        {
                            byte[] ipBytes = new byte[4];
                            Buffer.BlockCopy(packet, offset, ipBytes, 0, 4);
                            result.Add(new IPAddress(ipBytes));
                        }
                        else if (type == 28 && rdLen == 16) // AAAA
                        {
                            byte[] ipBytes = new byte[16];
                            Buffer.BlockCopy(packet, offset, ipBytes, 0, 16);
                            result.Add(new IPAddress(ipBytes));
                        }
                    }

                    offset += rdLen;
                }
            }
            catch
            {
                // Return whatever addresses were safely extracted
            }

            return result;
        }

        private static int SkipDnsName(byte[] packet, int offset)
        {
            int hops = 0;
            while (offset < packet.Length && hops < 128)
            {
                byte len = packet[offset++];
                if (len == 0) return offset;
                if ((len & 0xC0) == 0xC0)
                {
                    // Pointer (2 bytes total)
                    return offset + 1;
                }
                offset += len;
                hops++;
            }
            return offset;
        }
    }
}
