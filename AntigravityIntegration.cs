using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DevProxyApp
{
    // Independent implementation. Only protobuf descriptor/tag names are changed;
    // executable instructions, TLS verification and server authentication are preserved.
    public static class AntigravityIntegration
    {
        private const string BackupSuffix = ".devproxy-region.bak";
        private const string ManifestSuffix = ".devproxy-region.json";
        private static readonly string[] Patterns = new string[] {
            "\n\x10ineligible_tiers", "R\x0fineligibleTiers", "\n\nineligible\x18\x03",
            "R\nineligible", "name=ineligible,proto3", "name=ineligible_tiers,",
            "json=ineligibleTiers,", "json:\"ineligible_tiers,"
        };

        public static string BackendPath(string executable)
        {
            string candidate = Path.Combine(Path.GetDirectoryName(executable), "resources", "bin", "language_server.exe");
            if (!File.Exists(candidate))
                throw new InvalidOperationException("Не найден backend Antigravity Manager. Этот способ предназначен для Manager 2.x с resources/bin/language_server.exe.");
            return candidate;
        }

        public static string Hash(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static List<int> FindAll(byte[] data, byte[] needle)
        {
            var result = new List<int>();
            for (int i = 0; i <= data.Length - needle.Length; i++) {
                if (data[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && data[i + j] == needle[j]) j++;
                if (j == needle.Length) { result.Add(i); i += needle.Length - 1; }
            }
            return result;
        }

        public static byte[] PatchData(byte[] original, out int changes)
        {
            // Validate every signature before touching any byte. Every descriptor
            // and its matching Go tag must agree; partial or ambiguous builds fail.
            var offsets = new List<int>();
            var replacements = new List<byte[]>();
            foreach (string pattern in Patterns) {
                byte[] from = Encoding.ASCII.GetBytes(pattern);
                byte[] to = Encoding.ASCII.GetBytes(pattern.Replace("ineligible", "dpeligible"));
                List<int> matches = FindAll(original, from);
                if (matches.Count != 1)
                    throw new InvalidOperationException("Сигнатуры этой версии не поддерживаются или файл уже изменён. Подготовка отменена (совпадений: " + matches.Count + ").");
                offsets.Add(matches[0]); replacements.Add(to);
            }
            byte[] patched = (byte[])original.Clone();
            for (int i = 0; i < offsets.Count; i++) Array.Copy(replacements[i], 0, patched, offsets[i], replacements[i].Length);
            changes = offsets.Count;
            return patched;
        }

        private static Dictionary<string, object> Manifest(string backend)
        {
            string path = backend + ManifestSuffix;
            if (!File.Exists(path)) return null;
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (result == null || !result.ContainsKey("original_sha256") || !result.ContainsKey("patched_sha256"))
                throw new InvalidDataException("Повреждён манифест резервной копии Antigravity.");
            return result;
        }

        public static string Inspect(string executable)
        {
            string backend = BackendPath(executable);
            byte[] current = File.ReadAllBytes(backend);
            string hash = Hash(current);
            Dictionary<string, object> manifest = Manifest(backend);
            if (manifest != null && hash == Convert.ToString(manifest["patched_sha256"])) {
                if (!File.Exists(backend + BackupSuffix) || Hash(File.ReadAllBytes(backend + BackupSuffix)) != Convert.ToString(manifest["original_sha256"]))
                    throw new InvalidDataException("Подготовленный клиент найден, но резервная копия повреждена или отсутствует.");
                return "Клиент подготовлен; резервная копия проверена.";
            }
            int count; PatchData(current, out count);
            return "Antigravity " + FileVersionInfo.GetVersionInfo(executable).ProductVersion + ": найдено " + count + " сигнатур; требуется подготовка клиента.";
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static void EnsureNotRunning(string backend)
        {
            foreach (Process process in Process.GetProcessesByName("language_server")) {
                try {
                    if (string.Equals(process.MainModule.FileName, backend, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Закройте Antigravity перед подготовкой или восстановлением клиента.");
                } catch (System.ComponentModel.Win32Exception) {
                    throw new InvalidOperationException("Не удалось проверить запущенный language_server. Закройте Antigravity и повторите.");
                } finally { process.Dispose(); }
            }
        }

        public static string Prepare(string executable)
        {
            string backend = BackendPath(executable);
            using (var guard = new FileStream(backend + ".devproxy.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                byte[] original = File.ReadAllBytes(backend);
                string originalHash = Hash(original);
                Dictionary<string, object> previous = Manifest(backend);
                if (previous != null && originalHash == Convert.ToString(previous["patched_sha256"])) return Inspect(executable);
                EnsureNotRunning(backend);
                int changes;
                byte[] patched = PatchData(original, out changes);
                string backup = backend + BackupSuffix;
                if (File.Exists(backup) && Hash(File.ReadAllBytes(backup)) != originalHash)
                    throw new InvalidOperationException("Antigravity обновлён, а резервная копия относится к другой версии. Архивируйте старые .devproxy-region.bak/.json перед новой подготовкой.");
                if (!File.Exists(backup)) AtomicWrite(backup, original);
                if (Hash(File.ReadAllBytes(backup)) != originalHash) throw new InvalidDataException("Проверка резервной копии не пройдена.");
                var manifest = new Dictionary<string, object> {
                    { "schema", 1 }, { "original_sha256", originalHash }, { "patched_sha256", Hash(patched) },
                    { "length", original.Length }, { "changes", changes },
                    { "version", FileVersionInfo.GetVersionInfo(executable).ProductVersion }, { "created_utc", DateTime.UtcNow.ToString("o") }
                };
                AtomicWrite(backend + ManifestSuffix, Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(manifest)));
                if (Hash(File.ReadAllBytes(backend)) != originalHash) throw new InvalidOperationException("Клиент изменился во время подготовки. Повторите после завершения обновления.");
                AtomicWrite(backend, patched);
                if (Hash(File.ReadAllBytes(backend)) != Convert.ToString(manifest["patched_sha256"]))
                    throw new InvalidDataException("Проверка записанного клиента не пройдена; используйте восстановление.");
                return "Клиент подготовлен: " + changes + " согласованных полей. Оригинал сохранён и проверен по SHA-256.";
            }
        }

        public static string Restore(string executable)
        {
            string backend = BackendPath(executable);
            using (var guard = new FileStream(backend + ".devproxy.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                Dictionary<string, object> manifest = Manifest(backend);
                if (manifest == null) return "DevProxy не изменял этот клиент.";
                byte[] original = File.ReadAllBytes(backend + BackupSuffix);
                if (Hash(original) != Convert.ToString(manifest["original_sha256"])) throw new InvalidDataException("Резервная копия повреждена.");
                string currentHash = Hash(File.ReadAllBytes(backend));
                if (currentHash == Convert.ToString(manifest["original_sha256"])) return "Оригинальный клиент уже восстановлен.";
                if (currentHash != Convert.ToString(manifest["patched_sha256"]))
                    throw new InvalidOperationException("Клиент обновлён или изменён другим инструментом. Откат старой версии отменён.");
                EnsureNotRunning(backend); AtomicWrite(backend, original);
                if (Hash(File.ReadAllBytes(backend)) != Convert.ToString(manifest["original_sha256"])) throw new InvalidDataException("Проверка отката не пройдена.");
                return "Оригинальный клиент восстановлен, SHA-256 совпадает.";
            }
        }
    }

    public sealed class ScopedDnsResolver
    {
        public static readonly string[] Hosts = new string[] { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" };
        private readonly string _server;
        private readonly object _sync = new object();
        private readonly Dictionary<string, KeyValuePair<DateTime, IPAddress>> _cache = new Dictionary<string, KeyValuePair<DateTime, IPAddress>>();
        public ScopedDnsResolver(string server) { _server = server; }

        public static bool IsScoped(string host) { return Array.Exists(Hosts, x => x.Equals(host, StringComparison.OrdinalIgnoreCase)); }
        private static int Read16(byte[] data, int offset) { if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("Обрезанный DNS ответ."); return (data[offset] << 8) | data[offset + 1]; }
        private static int SkipName(byte[] data, int offset)
        {
            for (int labels = 0; labels < 128; labels++) {
                if (offset >= data.Length) throw new InvalidDataException("Обрезанное DNS имя.");
                int length = data[offset++];
                if (length == 0) return offset;
                if ((length & 192) == 192) { if (offset >= data.Length) throw new InvalidDataException("Обрезанный DNS указатель."); return offset + 1; }
                if (length > 63 || offset + length > data.Length) throw new InvalidDataException("Некорректное DNS имя.");
                offset += length;
            }
            throw new InvalidDataException("Слишком длинное DNS имя.");
        }

        public IPAddress Resolve(string host)
        {
            if (!IsScoped(host)) throw new InvalidOperationException("DNS-маршрут ограничен рабочими хостами Antigravity.");
            lock (_sync) {
                KeyValuePair<DateTime, IPAddress> cached;
                if (_cache.TryGetValue(host, out cached) && cached.Key > DateTime.UtcNow) return cached.Value;
            }
            IPAddress server;
            if (!IPAddress.TryParse(_server, out server)) {
                IPAddress[] addresses = Dns.GetHostAddresses(_server);
                server = Array.Find(addresses, x => x.AddressFamily == AddressFamily.InterNetwork);
                if (server == null) throw new InvalidOperationException("IPv4 DNS сервера не найден.");
            }
            byte[] transaction = new byte[2]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(transaction);
            byte[] query;
            using (var buffer = new MemoryStream()) {
                buffer.Write(transaction, 0, 2); buffer.Write(new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                foreach (string label in host.Split('.')) { byte[] bytes = Encoding.ASCII.GetBytes(label); buffer.WriteByte((byte)bytes.Length); buffer.Write(bytes, 0, bytes.Length); }
                buffer.Write(new byte[] { 0, 0, 1, 0, 1 }, 0, 5); query = buffer.ToArray();
            }
            byte[] response;
            using (var udp = new UdpClient(server.AddressFamily)) {
                udp.Client.ReceiveTimeout = 4000; udp.Client.SendTimeout = 4000; udp.Connect(server, 53);
                udp.Send(query, query.Length); IPEndPoint remote = null; response = udp.Receive(ref remote);
            }
            if (response.Length < 12 || response[0] != transaction[0] || response[1] != transaction[1] ||
                (response[2] & 128) == 0 || (response[2] & 2) != 0 || (response[3] & 15) != 0 || Read16(response, 4) != 1)
                throw new InvalidDataException("DNS сервер вернул ошибку или неполный ответ.");
            int offset = SkipName(response, 12);
            if (Read16(response, offset) != 1 || Read16(response, offset + 2) != 1) throw new InvalidDataException("DNS вопрос не совпадает.");
            offset += 4;
            int answers = Read16(response, 6);
            for (int answer = 0; answer < answers; answer++) {
                offset = SkipName(response, offset);
                int type = Read16(response, offset); int dnsClass = Read16(response, offset + 2);
                int length = Read16(response, offset + 8); offset += 10;
                if (offset + length > response.Length) throw new InvalidDataException("Обрезанные DNS данные.");
                if (type == 1 && dnsClass == 1 && length == 4) {
                    var address = new IPAddress(new byte[] { response[offset], response[offset + 1], response[offset + 2], response[offset + 3] });
                    byte first = response[offset]; byte second = response[offset + 1];
                    if (first == 0 || first == 10 || first == 127 || first >= 224 || (first == 192 && second == 168) || (first == 172 && second >= 16 && second <= 31) || (first == 169 && second == 254))
                        throw new InvalidDataException("DNS вернул локальный или зарезервированный адрес.");
                    lock (_sync) _cache[host] = new KeyValuePair<DateTime, IPAddress>(DateTime.UtcNow.AddSeconds(30), address);
                    return address;
                }
                offset += length;
            }
            throw new InvalidOperationException("DNS сервер не вернул IPv4 для " + host + ".");
        }
    }

    public static class AntigravityDiagnostics
    {
        private static string Header(Stream stream)
        {
            var data = new List<byte>();
            while (data.Count < 8192) {
                int next = stream.ReadByte();
                if (next < 0) throw new IOException("Соединение закрыто до получения HTTP заголовка.");
                data.Add((byte)next);
                int n = data.Count;
                if (n >= 4 && data[n-4] == 13 && data[n-3] == 10 && data[n-2] == 13 && data[n-1] == 10)
                    return Encoding.ASCII.GetString(data.ToArray()).Split(new string[] { "\r\n" }, StringSplitOptions.None)[0];
            }
            throw new InvalidDataException("HTTP заголовок превышает допустимый размер.");
        }

        public static string Probe(string host, int bridgePort)
        {
            if (!ScopedDnsResolver.IsScoped(host)) throw new ArgumentException("Неподдерживаемый диагностический хост.");
            return ProbeRequest(host, "/v1internal:loadCodeAssist", "application/json", "{}", bridgePort, false);
        }

        public static string ProbeToken(int bridgePort)
        {
            // Deliberately invalid identifiers: no account state or real code is read.
            return ProbeRequest("oauth2.googleapis.com", "/token", "application/x-www-form-urlencoded",
                "grant_type=authorization_code&code=devproxy-invalid-diagnostic&client_id=devproxy-invalid-diagnostic&redirect_uri=http%3A%2F%2Flocalhost", bridgePort, true);
        }

        private static string ProbeRequest(string host, string path, string contentType, string body, int bridgePort, bool token)
        {
            using (var client = new TcpClient()) {
                IAsyncResult connection = client.BeginConnect(IPAddress.Loopback, bridgePort, null, null);
                using (connection.AsyncWaitHandle) {
                    if (!connection.AsyncWaitHandle.WaitOne(3000)) throw new TimeoutException("Локальный мост не отвечает.");
                    client.EndConnect(connection);
                }
                client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                Stream stream = client.GetStream();
                byte[] connect = Encoding.ASCII.GetBytes("CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n\r\n");
                stream.Write(connect, 0, connect.Length);
                string status = Header(stream);
                if (!status.StartsWith("HTTP/1.1 200") && !status.StartsWith("HTTP/1.0 200"))
                    throw new IOException("Мост вернул " + status + ". DNS или upstream proxy не пропускает маршрут.");
                using (var tls = new SslStream(stream, false)) {
                    tls.ReadTimeout = 10000; tls.WriteTimeout = 10000;
                    tls.AuthenticateAsClient(host, null, SslProtocols.Tls12, true);
                    byte[] request = Encoding.ASCII.GetBytes("POST " + path + " HTTP/1.1\r\nHost: " + host + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + Encoding.ASCII.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
                    tls.Write(request, 0, request.Length); tls.Flush();
                    status = Header(tls);
                    return host + ": сертификат TLS проверен; " + status + (token ? ". Тест POST /token с вымышленными данными: ожидается HTTP 400/401; это проверка транспорта, вход не выполнен." : ". Запрос без токена аккаунта.");
                }
            }
        }
    }
}
