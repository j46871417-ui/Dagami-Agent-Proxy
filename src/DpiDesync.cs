using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DevProxyApp
{
    public enum DpiDesyncMode
    {
        Off = 0,
        AutoSplit = 1,
        TlsRecordSplit = 2,
        TcpMultiSplit = 3,
        ExternalEngine = 4
    }

    public static class TlsClientHelloInspector
    {
        public static bool TryFindSni(byte[] buffer, int offset, int count, out string hostName, out int sniHostOffset, out int sniMidOffset)
        {
            hostName = null;
            sniHostOffset = -1;
            sniMidOffset = -1;
            if (buffer == null || count < 43 || offset < 0 || offset + count > buffer.Length) return false;
            int end = offset + count;
            if (buffer[offset] != 0x16 || buffer[offset + 1] != 0x03 || buffer[offset + 2] > 0x04) return false;
            int recordLen = (buffer[offset + 3] << 8) | buffer[offset + 4];
            if (recordLen < 38 || offset + 5 + recordLen > end) return false;
            int hs = offset + 5;
            if (buffer[hs] != 0x01) return false; // ClientHello
            int hsLen = (buffer[hs + 1] << 16) | (buffer[hs + 2] << 8) | buffer[hs + 3];
            if (hs + 4 + hsLen > offset + 5 + recordLen) return false;
            int pos = hs + 4;
            if (pos + 34 > end) return false;
            pos += 34; // client_version (2) + random (32)
            if (pos >= end) return false;
            int sessionIdLen = buffer[pos++];
            pos += sessionIdLen;
            if (pos + 2 > end) return false;
            int cipherSuitesLen = (buffer[pos] << 8) | buffer[pos + 1];
            pos += 2 + cipherSuitesLen;
            if (pos >= end) return false;
            int compMethodsLen = buffer[pos++];
            pos += compMethodsLen;
            if (pos + 2 > end) return false;
            int extensionsLen = (buffer[pos] << 8) | buffer[pos + 1];
            pos += 2;
            int extEnd = Math.Min(end, pos + extensionsLen);
            while (pos + 4 <= extEnd) {
                int extType = (buffer[pos] << 8) | buffer[pos + 1];
                int extLen = (buffer[pos + 2] << 8) | buffer[pos + 3];
                pos += 4;
                if (pos + extLen > extEnd) break;
                if (extType == 0x0000 && extLen >= 5) {
                    int listLen = (buffer[pos] << 8) | buffer[pos + 1];
                    int itemPos = pos + 2;
                    int listEnd = Math.Min(pos + extLen, itemPos + listLen);
                    while (itemPos + 3 <= listEnd) {
                        byte nameType = buffer[itemPos];
                        int nameLen = (buffer[itemPos + 1] << 8) | buffer[itemPos + 2];
                        itemPos += 3;
                        if (itemPos + nameLen > listEnd) break;
                        if (nameType == 0 && nameLen > 0) {
                            hostName = Encoding.ASCII.GetString(buffer, itemPos, nameLen);
                            sniHostOffset = itemPos - offset;
                            int dot = hostName.LastIndexOf('.');
                            int mid = dot > 1 ? dot / 2 : Math.Max(1, nameLen / 2);
                            sniMidOffset = sniHostOffset + mid;
                            return true;
                        }
                        itemPos += nameLen;
                    }
                }
                pos += extLen;
            }
            return false;
        }

        public static byte[] SplitTlsRecordAtSni(byte[] buffer, int offset, int count)
        {
            string host; int sniStart, sniMid;
            if (!TryFindSni(buffer, offset, count, out host, out sniStart, out sniMid)) return null;
            int recordLen = (buffer[offset + 3] << 8) | buffer[offset + 4];
            if (5 + recordLen != count || sniMid <= 5 || sniMid >= count) return null;
            int firstPayloadLen = sniMid - 5;
            int secondPayloadLen = recordLen - firstPayloadLen;
            if (firstPayloadLen <= 0 || secondPayloadLen <= 0) return null;
            byte[] output = new byte[count + 5];
            output[0] = buffer[offset];
            output[1] = buffer[offset + 1];
            output[2] = buffer[offset + 2];
            output[3] = (byte)(firstPayloadLen >> 8);
            output[4] = (byte)firstPayloadLen;
            Buffer.BlockCopy(buffer, offset + 5, output, 5, firstPayloadLen);
            int secondHeader = 5 + firstPayloadLen;
            output[secondHeader] = buffer[offset];
            output[secondHeader + 1] = buffer[offset + 1];
            output[secondHeader + 2] = buffer[offset + 2];
            output[secondHeader + 3] = (byte)(secondPayloadLen >> 8);
            output[secondHeader + 4] = (byte)secondPayloadLen;
            Buffer.BlockCopy(buffer, offset + sniMid, output, secondHeader + 5, secondPayloadLen);
            return output;
        }
    }

    public sealed class DpiDesyncStream : Stream
    {
        private readonly Stream _inner;
        private readonly Socket _socket;
        private readonly DpiDesyncMode _mode;
        private bool _firstWrite = true;

        public DpiDesyncStream(Stream inner, Socket socket, DpiDesyncMode mode)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
            _socket = socket;
            _mode = mode;
        }

        public static DpiDesyncMode ParseMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DpiDesyncMode.AutoSplit;
            string v = value.Trim();
            if (v.Equals("Off", StringComparison.OrdinalIgnoreCase) || v.Equals("None", StringComparison.OrdinalIgnoreCase)) return DpiDesyncMode.Off;
            if (v.Equals("TlsRecordSplit", StringComparison.OrdinalIgnoreCase)) return DpiDesyncMode.TlsRecordSplit;
            if (v.Equals("TcpMultiSplit", StringComparison.OrdinalIgnoreCase)) return DpiDesyncMode.TcpMultiSplit;
            if (v.Equals("ExternalEngine", StringComparison.OrdinalIgnoreCase) || v.Equals("Zapret", StringComparison.OrdinalIgnoreCase) || v.Equals("GoodbyeDpi", StringComparison.OrdinalIgnoreCase)) return DpiDesyncMode.ExternalEngine;
            return DpiDesyncMode.AutoSplit;
        }

        public override bool CanRead { get { return _inner.CanRead; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return _inner.CanWrite; } }
        public override bool CanTimeout { get { return _inner.CanTimeout; } }
        public override int ReadTimeout { get { return _inner.ReadTimeout; } set { _inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return _inner.WriteTimeout; } set { _inner.WriteTimeout = value; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

        public override void Flush() { _inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { return _inner.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) { _inner.Write(buffer, offset, count); return; }
            if (!_firstWrite || _mode == DpiDesyncMode.Off || _mode == DpiDesyncMode.ExternalEngine || count <= 5) {
                _firstWrite = false;
                _inner.Write(buffer, offset, count);
                return;
            }
            _firstWrite = false;
            try {
                if (_socket != null) _socket.NoDelay = true;
            } catch { }

            string host; int sniStart, sniMid;
            if (TlsClientHelloInspector.TryFindSni(buffer, offset, count, out host, out sniStart, out sniMid)) {
                if (_mode == DpiDesyncMode.TlsRecordSplit || _mode == DpiDesyncMode.AutoSplit) {
                    byte[] splitRecords = TlsClientHelloInspector.SplitTlsRecordAtSni(buffer, offset, count);
                    if (splitRecords != null) {
                        if (_mode == DpiDesyncMode.AutoSplit && splitRecords.Length > 7) {
                            // Combine TLS record split + TCP multisplit (first 2 bytes, then up to second record, then rest)
                            int firstRecordEnd = 5 + ((splitRecords[3] << 8) | splitRecords[4]);
                            _inner.Write(splitRecords, 0, 2);
                            _inner.Flush();
                            _inner.Write(splitRecords, 2, firstRecordEnd - 2);
                            _inner.Flush();
                            _inner.Write(splitRecords, firstRecordEnd, splitRecords.Length - firstRecordEnd);
                            _inner.Flush();
                            return;
                        }
                        _inner.Write(splitRecords, 0, splitRecords.Length);
                        _inner.Flush();
                        return;
                    }
                }
                if ((_mode == DpiDesyncMode.TcpMultiSplit || _mode == DpiDesyncMode.AutoSplit) && sniMid > 2 && sniMid < count) {
                    // Zapret/GoodbyeDPI TCP multisplit at pos 2 and midsld
                    _inner.Write(buffer, offset, 2);
                    _inner.Flush();
                    _inner.Write(buffer, offset + 2, sniMid - 2);
                    _inner.Flush();
                    _inner.Write(buffer, offset + sniMid, count - sniMid);
                    _inner.Flush();
                    return;
                }
            }
            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public static class EdnsPadding
    {
        public static byte[] PadQuery(byte[] query, int blockSize = 128)
        {
            if (query == null || query.Length < 17 || blockSize < 16 || blockSize > 512) return query;
            // Only add OPT if ARCOUNT is currently 0
            if (query[10] != 0 || query[11] != 0) return query;
            int baseOptLen = 11 + 4; // Root(1) + Type OPT(2) + UDP size(2) + RCODE/Flags(4) + RDLEN(2) + OptionCode(2) + OptionLen(2)
            int totalUnpadded = query.Length + baseOptLen;
            int remainder = totalUnpadded % blockSize;
            int padBytes = remainder == 0 ? 0 : (blockSize - remainder);
            int rdLen = 4 + padBytes;
            byte[] padded = new byte[totalUnpadded + padBytes];
            Buffer.BlockCopy(query, 0, padded, 0, query.Length);
            padded[11] = 1; // ARCOUNT = 1
            int pos = query.Length;
            padded[pos++] = 0; // root domain
            padded[pos++] = 0; padded[pos++] = 41; // OPT record (41)
            padded[pos++] = 0x04; padded[pos++] = 0xd0; // UDP payload size 1232
            padded[pos++] = 0; padded[pos++] = 0; padded[pos++] = 0; padded[pos++] = 0; // ext RCODE & flags
            padded[pos++] = (byte)(rdLen >> 8);
            padded[pos++] = (byte)rdLen;
            padded[pos++] = 0; padded[pos++] = 12; // Option 12: EDNS0 Padding (RFC 8467)
            padded[pos++] = (byte)(padBytes >> 8);
            padded[pos++] = (byte)padBytes;
            return padded;
        }
    }

    public sealed class DpiStrategyProbeResult
    {
        public string RecommendedMode { get; set; }
        public string Summary { get; set; }
        public bool DirectTlsOk { get; set; }
        public bool AutoSplitOk { get; set; }
        public bool TlsRecordSplitOk { get; set; }
        public bool ExternalBinaryPresent { get; set; }
        public string ExternalBinaryPath { get; set; }
    }

    public static class DpiEngineManager
    {
        private static readonly object Sync = new object();
        private static Process _externalProcess;

        public static string DpiDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devproxy", "dpi"); }
        }

        public static string FindExternalEngine()
        {
            string[] roots = new[] {
                DpiDirectory,
                Path.Combine(SystemDnsManager.StoreRoot, "dpi"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dpi")
            };
            foreach (string root in roots) {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (string exe in new[] { "winws.exe", "goodbyedpi.exe" }) {
                    string candidate = Path.Combine(root, exe);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return null;
        }

        public static string WriteHostlist(string directory = null)
        {
            string targetDir = directory ?? DpiDirectory;
            Directory.CreateDirectory(targetDir);
            string path = Path.Combine(targetDir, "hostlist-dagami.txt");
            var hosts = new List<string>(ScopedDnsResolver.Hosts);
            hosts.AddRange(ScopedDnsResolver.GoogleDependencies);
            foreach (string extra in new[] {
                "chatgpt.com", "api.openai.com", "auth.openai.com", "claude.ai", "api.anthropic.com",
                "copilot.microsoft.com", "aistudio.google.com", "deepmind.google", "dns.dagami.ru", "dns.magicdns.ru"
            }) {
                if (!hosts.Contains(extra)) hosts.Add(extra);
            }
            File.WriteAllLines(path, hosts.ToArray(), new UTF8Encoding(false));
            return path;
        }

        public static string BuildEngineArguments(string enginePath, string hostlistPath, string preset = "multisplit")
        {
            string name = Path.GetFileName(enginePath ?? "").ToLowerInvariant();
            string quotedList = NativeShortcuts.Quote(hostlistPath);
            if (name == "goodbyedpi.exe") {
                if (preset == "wrongseq") return "-e 2 -f 2 --native-frag --wrong-seq --blacklist " + quotedList;
                return "-e 2 -f 2 --native-frag --reverse-frag --max-payload 1200 --blacklist " + quotedList;
            }
            // Zapret winws.exe
            if (preset == "fake")
                return "--wf-tcp=443 --dpi-desync=fake,multisplit --dpi-desync-split-pos=2,midsld --dpi-desync-autottl=2 --hostlist=" + quotedList;
            return "--wf-tcp=443 --dpi-desync=multisplit --dpi-desync-split-pos=2,midsld --dpi-desync-split-seqovl=1 --hostlist=" + quotedList;
        }

        public static bool IsExternalRunning()
        {
            lock (Sync) {
                try { return _externalProcess != null && !_externalProcess.HasExited; }
                catch { return false; }
            }
        }

        public static bool StartExternal(out string error)
        {
            error = null;
            lock (Sync) {
                if (IsExternalRunning()) return true;
                string engine = FindExternalEngine();
                if (string.IsNullOrEmpty(engine)) {
                    error = "Внешний движок (winws.exe или goodbyedpi.exe) не найден в папке dpi.";
                    return false;
                }
                try {
                    string hostlist = WriteHostlist();
                    string args = BuildEngineArguments(engine, hostlist);
                    var psi = new ProcessStartInfo(engine, args) {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    _externalProcess = Process.Start(psi);
                    return true;
                } catch (Exception ex) {
                    error = "Ошибка запуска внешнего DPI движка: " + ex.Message;
                    return false;
                }
            }
        }

        public static void StopExternal()
        {
            lock (Sync) {
                if (_externalProcess == null) return;
                try { if (!_externalProcess.HasExited) _externalProcess.Kill(); } catch { }
                try { _externalProcess.Dispose(); } catch { }
                _externalProcess = null;
            }
        }

        public static DpiStrategyProbeResult ProbeStrategies(string testHost = "www.googleapis.com", int timeoutMs = 3500)
        {
            var result = new DpiStrategyProbeResult();
            string ext = FindExternalEngine();
            result.ExternalBinaryPresent = ext != null;
            result.ExternalBinaryPath = ext;
            IPAddress ip = null;
            try {
                var resolver = new ScopedDnsResolver(ConfigManager.Instance.DnsHost);
                ip = resolver.Resolve(ScopedDnsResolver.IsScoped(testHost) ? testHost : "www.googleapis.com");
            } catch {
                try {
                    foreach (IPAddress cand in Dns.GetHostAddresses(testHost))
                        if (cand.AddressFamily == AddressFamily.InterNetwork) { ip = cand; break; }
                } catch { }
            }
            if (ip == null) {
                result.RecommendedMode = "AutoSplit";
                result.Summary = "Не удалось разрешить IP для пробного TLS-соединения; оставлен безопасный режим AutoSplit (TLS Split + Padding).";
                return result;
            }

            result.DirectTlsOk = ProbeTlsWithMode(ip, testHost, DpiDesyncMode.Off, timeoutMs);
            result.AutoSplitOk = ProbeTlsWithMode(ip, testHost, DpiDesyncMode.AutoSplit, timeoutMs);
            result.TlsRecordSplitOk = ProbeTlsWithMode(ip, testHost, DpiDesyncMode.TlsRecordSplit, timeoutMs);

            if (result.AutoSplitOk) result.RecommendedMode = "AutoSplit";
            else if (result.TlsRecordSplitOk) result.RecommendedMode = "TlsRecordSplit";
            else if (result.DirectTlsOk) result.RecommendedMode = "Off";
            else result.RecommendedMode = result.ExternalBinaryPresent ? "ExternalEngine" : "AutoSplit";

            result.Summary = string.Format(
                "Проверка обхода DPI ({0} → {1}): AutoSplit={2}, TlsRecordSplit={3}, Прямой TLS={4}. Рекомендован режим: {5}.{6}",
                testHost, ip,
                result.AutoSplitOk ? "ОК" : "сбой",
                result.TlsRecordSplitOk ? "ОК" : "сбой",
                result.DirectTlsOk ? "ОК" : "блок/сбой",
                result.RecommendedMode,
                result.ExternalBinaryPresent ? " Найден внешний движок: " + Path.GetFileName(ext) + "." : "");
            return result;
        }

        private static bool ProbeTlsWithMode(IPAddress ip, string host, DpiDesyncMode mode, int timeoutMs)
        {
            try {
                using (var client = new TcpClient(ip.AddressFamily)) {
                    client.NoDelay = true;
                    var pending = client.BeginConnect(ip, 443, null, null);
                    using (pending.AsyncWaitHandle) {
                        if (!pending.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                        client.EndConnect(pending);
                    }
                    Stream raw = client.GetStream();
                    raw.ReadTimeout = timeoutMs;
                    raw.WriteTimeout = timeoutMs;
                    if (mode != DpiDesyncMode.Off) raw = new DpiDesyncStream(raw, client.Client, mode);
                    using (var tls = TlsDiagnostics.Connect(raw, host, "dpi-probe", timeoutMs, false)) {
                        return tls.IsAuthenticated;
                    }
                }
            } catch {
                return false;
            }
        }
    }
}
