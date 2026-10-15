using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Security.Principal;
using System.Reflection;
using System.Net.Security;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace DevProxyApp
{
    static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                Logger.Log("[CRITICAL DOMAIN EXCEPTION] " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null"));
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => {
                Logger.Log("[CRITICAL THREAD EXCEPTION] " + (e.Exception != null ? e.Exception.ToString() : "null"));
            };

            try
            {
                if (args != null && args.Length == 3 && args[0] == "--system-dns-helper") {
                    Environment.Exit(SystemDnsManager.RunElevatedHelper(args[1], args[2]));
                    return;
                }
                Logger.Log("[MAIN START] args: " + (args != null && args.Length > 0 ? string.Join(", ", args) : "(none)"));
                if (args != null && args.Length >= 2 && args[0].Equals("--shortcut-launch", StringComparison.OrdinalIgnoreCase))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    if (args.Length != 2 && (args.Length != 4 || args[2] != "--shortcut-key")) throw new ArgumentException("Некорректные параметры ярлыка.");
                    NativeLaunchBroker.Run(new NativeLaunchRequest { TargetId = args[1], ShortcutKey = args.Length == 4 ? args[3] : null });
                    return;
                }
                if (args != null && args.Length > 0)
                {
                    if (!AttachConsole(-1))
                    {
                        AllocConsole();
                    }
                    RunCli(args);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Logger.Log("[MAIN BEFORE RUN]");
                NativeLaunchBroker.Run(null);
                Logger.Log("[MAIN EXIT]");
            }
            catch (Exception ex)
            {
                Logger.Log("[CRASH] " + ex.ToString());
                if (args != null && args.Length > 0) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
                else MessageBox.Show(ex.ToString(), Brand.Name + " — ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void RunCli(string[] args)
        {
            if (NativeCli.Handle(args)) return;
            Console.OutputEncoding = Encoding.UTF8;
            string cmd = args[0].Trim();
            Logger.Log("[CLI] Команда: " + string.Join(" ", args));
            if (cmd == "--proxy-shortcuts" || cmd == "--restore-shortcuts") {
                if (args.Length != 2) throw new ArgumentException("Укажите ID приложения.");
                var target = ProxyEngine.ScanAllTargets().Find(x => x.Id == args[1]);
                if (target == null) throw new InvalidOperationException("Приложение не найдено.");
                var store = NativeShortcuts.Current;
                var paths = cmd == "--proxy-shortcuts" ? store.Enable(target, Process.GetCurrentProcess().MainModule.FileName, NativeShortcuts.UserFolders()) : store.Restore(target.Id);
                foreach (string path in paths) Console.WriteLine(path);
                Console.WriteLine("Ярлыков: " + paths.Count); return;
            }
            if (cmd == "--antigravity-inspect" || cmd == "--antigravity-prepare" || cmd == "--antigravity-restore")
            {
                if (args.Length != 2) throw new ArgumentException("Укажите полный путь к Antigravity.exe.");
                Console.WriteLine(cmd == "--antigravity-inspect" ? AntigravityIntegration.Inspect(args[1]) :
                    cmd == "--antigravity-prepare" ? AntigravityIntegration.Prepare(args[1]) : AntigravityIntegration.Restore(args[1]));
                return;
            }
            if (cmd == "--help" || cmd == "-h" || cmd == "help")
            {
                Console.WriteLine(Brand.Name + " " + Brand.Version + " (Windows)");
                Console.WriteLine("apps | proxy set | proxy status | run APP | patch APP | unpatch APP | shortcuts APP");
                Console.WriteLine("dns status | dns adapters | dns apply REQUEST.json | dns restore (UAC)");
                Console.WriteLine("Использование:");
                Console.WriteLine("  DagamiAgentProxy.exe                    - Открыть графический интерфейс (GUI)");
                Console.WriteLine("  DagamiAgentProxy.exe [ПРОКСИ]           - Применить прокси во все найденные IDE");
                Console.WriteLine("  DagamiAgentProxy.exe --status           - Проверить статус настроек");
                Console.WriteLine("  DagamiAgentProxy.exe --remove           - Откатить настройки прокси");
                return;
            }
            if (cmd == "--status" || cmd == "-s" || cmd == "status")
            {
                ProxyEngine.ShowStatus();
                return;
            }
            if (cmd == "--remove" || cmd == "-r" || cmd == "clean" || cmd == "remove")
            {
                ProxyEngine.RemoveAll();
                return;
            }
            string norm = ProxyEngine.NormalizeProxy(cmd);
            if (norm != null)
            {
                ProxyEngine.ApplyProxyAll(norm);
                ConfigManager.Instance.LastProxy = norm;
                ConfigManager.Instance.Save();
                return;
            }
            Console.WriteLine("[-] Некорректный формат прокси."); Environment.ExitCode = 2;
        }
    }

    public class JobObjectManager : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryLimit;
            public UIntPtr PeakJobMemoryLimit;
        }

        IntPtr handle;

        public JobObjectManager()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle != IntPtr.Zero)
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr pInfo = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, pInfo, false);
                    SetInformationJobObject(handle, JobObjectExtendedLimitInformation, pInfo, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(pInfo);
                }
            }
        }

        public bool AssignProcess(IntPtr hProcess)
        {
            if (handle == IntPtr.Zero) return false;
            return AssignProcessToJobObject(handle, hProcess);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;
        public static Action<string> OnLog;

        public static string LogFilePath
        {
            get
            {
                if (_logFilePath == null)
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string dir = Path.Combine(appData, "devproxy");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    _logFilePath = Path.Combine(dir, "devproxy.log");
                }
                return _logFilePath;
            }
        }

        public static void Log(string msg)
        {
            msg = Redact(msg);
            string line = string.Format("[{0}] {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), msg);
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }

            if (OnLog != null)
            {
                try { OnLog(msg); } catch { }
            }
        }

        private static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = Regex.Replace(value, @"(?i)(?:https?|socks5h?)://[^\s<>""']+", match => {
                Uri uri;
                return Uri.TryCreate(match.Value, UriKind.Absolute, out uri) ? uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "") + "/[redacted]" : "[url redacted]";
            });
            return Regex.Replace(value, @"(?i)\b(password|token|state|code|code_verifier|cookie|authorization)\s*[:=]\s*[^\s,;]+", "$1=[redacted]");
        }

        public static void Clear()
        {
            lock (_lock)
            {
                try { File.WriteAllText(LogFilePath, string.Empty, Encoding.UTF8); } catch { }
            }
        }
    }

    public class LocalProxyBridge : IDisposable
    {
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private string _upstreamHost;
        private int _upstreamPort;
        private bool _isHttps;
        private string _authHeader;
        private ScopedDnsResolver _scopedDns;
        private readonly object _routeLock = new object();
        private readonly Dictionary<string, int> _routeCounts = new Dictionary<string, int>();
        private static int _connectionSequence;
        public int BoundPort { get; private set; }

        public bool Start(int port, string proxyUrl, string dnsHost = null)
        {
            try
            {
                _scopedDns = string.IsNullOrWhiteSpace(dnsHost) ? null : new ScopedDnsResolver(dnsHost);
                Uri u = new Uri(proxyUrl);
                _upstreamHost = u.Host;
                _upstreamPort = u.Port > 0 ? u.Port : 8080;
                _isHttps = proxyUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(u.UserInfo))
                {
                    byte[] b = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(u.UserInfo));
                    _authHeader = "Basic " + Convert.ToBase64String(b);
                }
                else
                {
                    _authHeader = null;
                }

                for (int tryPort = port; tryPort < port + 10; tryPort++)
                {
                    try
                    {
                        _listener = new TcpListener(IPAddress.Loopback, tryPort);
                        _listener.ExclusiveAddressUse = true;
                        _listener.Start(100);
                        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                        _running = true;
                        break;
                    }
                    catch { }
                }

                if (!_running || _listener == null)
                {
                    Logger.Log("[-] Не удалось привязать порт моста.");
                    return false;
                }

                _thread = new Thread(ListenLoop);
                _thread.IsBackground = true;
                _thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("[-] Ошибка запуска моста на порту " + port + ": " + ex.Message);
                return false;
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleConnection, client);
                }
                catch
                {
                    break;
                }
            }
        }

        private void HandleConnection(object state)
        {
            TcpClient client = (TcpClient)state;
            TcpClient upstream = null;
            string connection = "[Bridge #" + Interlocked.Increment(ref _connectionSequence) + "]";
            string phase = "client-header";
            bool tunnelEstablished = false;
            var elapsed = Stopwatch.StartNew();
            try
            {
                client.NoDelay = true;
                NetworkStream clientStream = client.GetStream();
                clientStream.ReadTimeout = 15000;

                byte[] buffer = new byte[16384];
                int totalRead = 0;
                int headerEnd = -1;

                while (totalRead < buffer.Length)
                {
                    int r = clientStream.Read(buffer, totalRead, buffer.Length - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                    string curr = Encoding.ASCII.GetString(buffer, 0, totalRead);
                    headerEnd = curr.IndexOf("\r\n\r\n");
                    if (headerEnd >= 0) break;
                }

                if (headerEnd < 0) { client.Close(); return; }

                int bodyOffset = headerEnd + 4;
                string headerText = Encoding.ASCII.GetString(buffer, 0, bodyOffset);
                string firstLine = headerText.Substring(0, headerText.IndexOf("\r\n"));
                string[] parts = firstLine.Split(' ');
                if (parts.Length < 3) { client.Close(); return; }

                string method = parts[0];
                string target = parts[1];
                string routedHost = null;
                IPAddress dnsEndpoint = null;
                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = target.LastIndexOf(':');
                    string host = colon > 0 ? target.Substring(0, colon) : target;
                    if (_scopedDns != null && ScopedDnsResolver.IsScoped(host))
                    {
                        if (colon <= 0 || target.Substring(colon + 1) != "443")
                            throw new InvalidDataException("Antigravity DNS route supports only TLS port 443.");
                        dnsEndpoint = _scopedDns.Resolve(host);
                        routedHost = host;
                        Logger.Log("[DNS] " + host + " → " + dnsEndpoint + ":443 через Smart DNS напрямую; SNI сохраняется клиентом.");
                    }
                }

                string destinationLabel = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)
                    ? SafeAuthority(target) : "HTTP";
                connection += " " + destinationLabel;
                Logger.Log(connection + " start route=" + (dnsEndpoint == null ? "upstream" : "smart-dns"));
                phase = "upstream-connect";
                Stream upstreamStream;
                upstream = ConnectUpstream(dnsEndpoint, out upstreamStream);
                if (dnsEndpoint != null)
                {
                    byte[] success = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                    clientStream.Write(success, 0, success.Length); clientStream.Flush();
                    tunnelEstablished = true;
                    if (bodyOffset < totalRead) upstreamStream.Write(buffer, bodyOffset, totalRead - bodyOffset);
                    lock (_routeLock)
                    {
                        if (!_routeCounts.ContainsKey(routedHost)) _routeCounts[routedHost] = 0;
                        _routeCounts[routedHost]++;
                    }
                    Relay(clientStream, upstreamStream, client, upstream, connection);
                    return;
                }
                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    string connectReq = string.IsNullOrEmpty(_authHeader)
                        ? string.Format("CONNECT {0} HTTP/1.1\r\nHost: {0}\r\n\r\n", target)
                        : string.Format("CONNECT {0} HTTP/1.1\r\nHost: {0}\r\nProxy-Authorization: {1}\r\n\r\n", target, _authHeader);

                    byte[] conBytes = Encoding.ASCII.GetBytes(connectReq);
                    byte[] respBuf = new byte[16384];
                    int respRead = 0;
                    phase = "upstream-CONNECT";
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        upstreamStream.Write(conBytes, 0, conBytes.Length);
                        upstreamStream.Flush();
                        try { respRead = ReadConnectResponse(upstreamStream, respBuf); break; }
                        catch (EndOfStreamException)
                        {
                            Logger.Log(connection + " upstream EOF before CONNECT response; attempt=" + attempt);
                            if (attempt == 3) throw;
                            // No tunnel/data has been acknowledged. Reconnect only;
                            // never replay application requests or OAuth codes.
                            upstreamStream.Dispose(); upstream.Close();
                            Thread.Sleep(attempt * 150);
                            upstream = ConnectUpstream(null, out upstreamStream);
                        }
                    }
                    string respText = Encoding.ASCII.GetString(respBuf, 0, respRead);

                    int status = ConnectStatus(respText);
                    Logger.Log(connection + " CONNECT status=" + status + " elapsed_ms=" + elapsed.ElapsedMilliseconds);
                    if (status >= 200 && status < 300)
                    {
                        if (routedHost != null) lock (_routeLock)
                        {
                            if (!_routeCounts.ContainsKey(routedHost)) _routeCounts[routedHost] = 0;
                            _routeCounts[routedHost]++;
                        }
                        byte[] okBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                        clientStream.Write(okBytes, 0, okBytes.Length);
                        clientStream.Flush();
                        tunnelEstablished = true;
                        int upstreamHeaderEnd = respText.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                        if (upstreamHeaderEnd > 3 && upstreamHeaderEnd < respRead)
                            clientStream.Write(respBuf, upstreamHeaderEnd, respRead - upstreamHeaderEnd);
                        if (bodyOffset < totalRead)
                            upstreamStream.Write(buffer, bodyOffset, totalRead - bodyOffset);
                        Relay(clientStream, upstreamStream, client, upstream, connection);
                    }
                    else
                    {
                        throw new IOException("Upstream proxy refused CONNECT; HTTP status=" + status);
                    }
                }
                else
                {
                    byte[] sendBytes;
                    if (!string.IsNullOrEmpty(_authHeader) && headerText.IndexOf("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        string mod = headerText.Insert(headerEnd, "\r\nProxy-Authorization: " + _authHeader);
                        byte[] modifiedHeader = Encoding.ASCII.GetBytes(mod);
                        sendBytes = new byte[modifiedHeader.Length + (totalRead - bodyOffset)];
                        Array.Copy(modifiedHeader, sendBytes, modifiedHeader.Length);
                        if (bodyOffset < totalRead)
                            Array.Copy(buffer, bodyOffset, sendBytes, modifiedHeader.Length, totalRead - bodyOffset);
                    }
                    else
                    {
                        sendBytes = new byte[totalRead];
                        Array.Copy(buffer, sendBytes, totalRead);
                    }
                    upstreamStream.Write(sendBytes, 0, sendBytes.Length);
                    upstreamStream.Flush();
                    Relay(clientStream, upstreamStream, client, upstream, connection);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(connection + " failed phase=" + phase + " elapsed_ms=" + elapsed.ElapsedMilliseconds + " " + ErrorKind(ex));
                try
                {
                    if (tunnelEstablished) throw new IOException("Tunnel already established; do not inject HTTP into TLS.");
                    byte[] body = Encoding.UTF8.GetBytes("DevProxy: " + ex.GetType().Name + ". Смотрите журнал диагностики.");
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    Stream output = client.GetStream(); output.Write(header, 0, header.Length); output.Write(body, 0, body.Length);
                }
                catch { }
                try { client.Close(); } catch { }
                try { if (upstream != null) upstream.Close(); } catch { }
            }
        }

        private TcpClient ConnectUpstream(IPAddress endpoint, out Stream stream)
        {
            var tcp = new TcpClient();
            try {
                tcp.NoDelay = true;
                var result = tcp.BeginConnect(endpoint == null ? _upstreamHost : endpoint.ToString(), endpoint == null ? _upstreamPort : 443, null, null);
                using (result.AsyncWaitHandle) {
                    if (!result.AsyncWaitHandle.WaitOne(8000)) throw new TimeoutException("Upstream connection timed out.");
                    tcp.EndConnect(result);
                }
                stream = tcp.GetStream(); stream.ReadTimeout = 15000; stream.WriteTimeout = 15000;
                if (endpoint == null && _isHttps) {
                    var tls = new SslStream(stream, false);
                    tls.AuthenticateAsClient(_upstreamHost, null, System.Security.Authentication.SslProtocols.Tls12, true);
                    stream = tls;
                }
                return tcp;
            } catch { tcp.Close(); throw; }
        }

        public static string SafeAuthority(string target)
        {
            Uri authority;
            if (!Uri.TryCreate("https://" + target, UriKind.Absolute, out authority) || !string.IsNullOrEmpty(authority.UserInfo) ||
                authority.AbsolutePath != "/" || !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment) || authority.HostNameType == UriHostNameType.Unknown)
                throw new InvalidDataException("Invalid CONNECT authority.");
            return authority.IdnHost + ":" + authority.Port;
        }

        public static int ReadConnectResponse(Stream stream, byte[] buffer)
        {
            int count = 0;
            while (count < buffer.Length) {
                int n = stream.Read(buffer, count, buffer.Length-count);
                if (n == 0) throw new EndOfStreamException("Upstream closed before a complete CONNECT response.");
                count += n;
                if (Encoding.ASCII.GetString(buffer, 0, count).IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) return count;
            }
            throw new InvalidDataException("Upstream CONNECT header too large.");
        }

        public static int ConnectStatus(string response)
        {
            var match = Regex.Match(response, @"^HTTP/1\.[01] ([0-9]{3})(?: |\r\n)");
            if (!match.Success) throw new InvalidDataException("Invalid upstream CONNECT status.");
            return int.Parse(match.Groups[1].Value);
        }

        private static string ErrorKind(Exception error)
        {
            var socket = error as SocketException;
            string result = error.GetType().Name;
            if (socket != null) result += " socket=" + socket.SocketErrorCode;
            // Messages may contain untrusted response text. Only generated status
            // and phase information are logged; headers and payloads stay opaque.
            if (error.InnerException != null) result += " inner=" + ErrorKind(error.InnerException);
            return result;
        }

        private void Relay(Stream s1, Stream s2, IDisposable d1, IDisposable d2, string connection)
        {
            // Streaming model responses may legitimately pause longer than the
            // handshake deadline. Preserve the tunnel until one side closes it.
            if (s1.CanTimeout) s1.ReadTimeout = Timeout.Infinite;
            if (s2.CanTimeout) s2.ReadTimeout = Timeout.Infinite;
            int remaining = 2;
            Action finished = () => {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    try { d1.Dispose(); } catch { }
                    try { d2.Dispose(); } catch { }
                }
            };
            Thread t1 = new Thread(() => Pipe(s1, s2, d2 as TcpClient, finished, connection + " client->upstream"));
            Thread t2 = new Thread(() => Pipe(s2, s1, d1 as TcpClient, finished, connection + " upstream->client"));
            t1.IsBackground = true;
            t2.IsBackground = true;
            t1.Start();
            t2.Start();
        }

        private void Pipe(Stream from, Stream to, TcpClient destination, Action finished, string connection)
        {
            byte[] buf = new byte[16384];
            long bytes = 0;
            var elapsed = Stopwatch.StartNew();
            string outcome = "EOF";
            try
            {
                int r;
                while ((r = from.Read(buf, 0, buf.Length)) > 0)
                {
                    to.Write(buf, 0, r);
                    bytes += r;
                    to.Flush();
                }
            }
            catch (Exception ex) { outcome = ErrorKind(ex); }
            finally
            {
                Logger.Log(connection + " end=" + outcome + " bytes=" + bytes + " elapsed_ms=" + elapsed.ElapsedMilliseconds);
                CompleteWrite(to, destination);
                finished();
            }
        }

        public static void CompleteWrite(Stream destinationStream, TcpClient destination)
        {
            // TCP shutdown bypasses SslStream and truncates the TLS record layer.
            // Continue reading the opposite direction until the TLS peer closes.
            if (destinationStream is SslStream) return;
            try { if (destination != null) destination.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        public string RouteReport()
        {
            lock (_routeLock)
            {
                var lines = new List<string>();
                foreach (string host in ScopedDnsResolver.Hosts)
                            lines.Add(host + ": установленных туннелей через Smart DNS — " + (_routeCounts.ContainsKey(host) ? _routeCounts[host] : 0));
                return string.Join(Environment.NewLine, lines.ToArray());
            }
        }
    }

    public class AppTarget
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string ExePath { get; set; }
        public string ConfigPath { get; set; }
        public bool IsCustom { get; set; }

        public bool IsInstalled
        {
            get { return !string.IsNullOrEmpty(ExePath) && File.Exists(ExePath); }
        }

        public override string ToString()
        {
            string status = IsInstalled ? "[Установлено]" : "[Не найдено]";
            return string.Format("{0}  —  {1}", DisplayName, status);
        }
    }

    public class ProxyProfileItem
    {
        public string Name { get; set; }
        public string Url { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1})", Name, Url);
        }
    }

    public class ConfigManager
    {
        static ConfigManager instance;
        public static ConfigManager Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new ConfigManager();
                    instance.Load();
                }
                return instance;
            }
        }

        public string LastProxy { get; set; }
        public bool SetSystemEnv { get; set; }
        public bool UseDagamiDns { get; set; }
        public string DnsHost { get; set; }
        public bool PrepareAntigravityClient { get; set; }
        public List<ProxyProfileItem> Profiles { get; set; }
        public List<string> CheckedIds { get; set; }
        public List<AppTarget> CustomTargets { get; set; }
        public Dictionary<string, string> EnvironmentBackup { get; set; }

        string configDir;
        string configFile;

        public ConfigManager()
        {
            Profiles = new List<ProxyProfileItem>();
            CheckedIds = new List<string>();
            CustomTargets = new List<AppTarget>();
            EnvironmentBackup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SetSystemEnv = true;
            UseDagamiDns = true;
            DnsHost = "dns.dagami.ru";
            PrepareAntigravityClient = true;
            LastProxy = "";

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            configDir = Path.Combine(appData, "devproxy");
            configFile = Path.Combine(configDir, "config.json");
        }

        public void Load()
        {
            try
            {
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                if (File.Exists(configFile))
                {
                    string content = File.ReadAllText(configFile, Encoding.UTF8);
                    ParseJsonConfig(content);
                }
            }
            catch { }

            // If no proxy saved in config, scan existing settings in IDEs or environment
            if (string.IsNullOrEmpty(LastProxy))
            {
                string detected = ScanExistingIdeProxy();
                if (!string.IsNullOrEmpty(detected))
                {
                    LastProxy = detected;
                    AddProfileIfMissing("Обнаруженный прокси", detected);
                }
                else
                {
                    LastProxy = "";
                }
            }

            // Ensure baseline presets exist in profiles
            EnsureDefaultProfiles();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendFormat("  \"last_proxy_protected\": \"{0}\",\n", EscapeJson(ProtectString(LastProxy)));
                sb.AppendFormat("  \"set_system_env\": {0},\n", SetSystemEnv ? "true" : "false");
                sb.AppendFormat("  \"use_dagami_dns\": {0},\n", UseDagamiDns ? "true" : "false");
                sb.AppendFormat("  \"dns_host\": \"{0}\",\n", EscapeJson(DnsHost));
                sb.AppendFormat("  \"prepare_antigravity_client\": {0},\n", PrepareAntigravityClient ? "true" : "false");

                // Checked IDs
                sb.Append("  \"checked_ids\": [");
                for (int i = 0; i < CheckedIds.Count; i++)
                {
                    sb.AppendFormat("\"{0}\"{1}", EscapeJson(CheckedIds[i]), (i < CheckedIds.Count - 1 ? ", " : ""));
                }
                sb.AppendLine("],");

                // Profiles
                sb.AppendLine("  \"profiles\": [");
                for (int i = 0; i < Profiles.Count; i++)
                {
                    sb.AppendFormat("    {{\"name\": \"{0}\", \"url_protected\": \"{1}\"}}{2}\n",
                        EscapeJson(Profiles[i].Name),
                        EscapeJson(ProtectString(Profiles[i].Url)),
                        (i < Profiles.Count - 1 ? "," : ""));
                }
                sb.AppendLine("  ],");

                sb.AppendLine("  \"environment_backup\": [");
                int envIndex = 0;
                foreach (var entry in EnvironmentBackup)
                {
                    sb.AppendFormat("    {{\"name\": \"{0}\", \"value_protected\": \"{1}\"}}{2}\n",
                        EscapeJson(entry.Key), EscapeJson(ProtectString(entry.Value)),
                        envIndex < EnvironmentBackup.Count - 1 ? "," : "");
                    envIndex++;
                }
                sb.AppendLine("  ],");

                // Custom Targets
                sb.AppendLine("  \"custom_targets\": [");
                for (int i = 0; i < CustomTargets.Count; i++)
                {
                    sb.AppendFormat("    {{\"id\": \"{0}\", \"name\": \"{1}\", \"exe\": \"{2}\"}}{3}\n",
                        EscapeJson(CustomTargets[i].Id),
                        EscapeJson(CustomTargets[i].DisplayName),
                        EscapeJson(CustomTargets[i].ExePath),
                        (i < CustomTargets.Count - 1 ? "," : ""));
                }
                sb.AppendLine("  ]");

                sb.AppendLine("}");

                File.WriteAllText(configFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Logger.Log("[-] Не удалось сохранить конфигурацию: " + ex.Message); }
        }

        void ParseJsonConfig(string json)
        {
            // Protected values are preferred; legacy plaintext is read only for migration.
            Match mProtectedProxy = Regex.Match(json, @"\""last_proxy_protected\""\s*:\s*\""([^\""]*)\""");
            if (mProtectedProxy.Success)
                LastProxy = UnprotectString(UnescapeJson(mProtectedProxy.Groups[1].Value));
            else
            {
                Match mProxy = Regex.Match(json, @"\""last_proxy\""\s*:\s*\""([^\""]*)\""");
                if (mProxy.Success) LastProxy = UnescapeJson(mProxy.Groups[1].Value);
            }

            // set_system_env
            Match mEnv = Regex.Match(json, @"\""set_system_env\""\s*:\s*(true|false)");
            if (mEnv.Success) SetSystemEnv = mEnv.Groups[1].Value.ToLower() == "true";

            // use_dagami_dns
            Match mDns = Regex.Match(json, @"\""use_dagami_dns\""\s*:\s*(true|false)");
            if (mDns.Success) UseDagamiDns = mDns.Groups[1].Value.ToLower() == "true";

            Match mDnsHost = Regex.Match(json, @"\""dns_host\""\s*:\s*\""([^\""]+)\""");
            if (mDnsHost.Success) DnsHost = UnescapeJson(mDnsHost.Groups[1].Value);
            Match mPrepare = Regex.Match(json, @"\""prepare_antigravity_client\""\s*:\s*(true|false)");
            if (mPrepare.Success) PrepareAntigravityClient = mPrepare.Groups[1].Value == "true";

            // checked_ids
            Match mChecked = Regex.Match(json, @"\""checked_ids\""\s*:\s*\[([^\]]*)\]");
            if (mChecked.Success)
            {
                CheckedIds.Clear();
                foreach (Match idMatch in Regex.Matches(mChecked.Groups[1].Value, @"\""([^\""]+)\"""))
                {
                    CheckedIds.Add(idMatch.Groups[1].Value);
                }
            }

            // profiles
            Match mProf = Regex.Match(json, @"\""profiles\""\s*:\s*\[(.*?)\]\s*,\s*\""custom", RegexOptions.Singleline);
            if (mProf.Success)
            {
                Profiles.Clear();
                foreach (Match pMatch in Regex.Matches(mProf.Groups[1].Value, @"\{\s*\""name\""\s*:\s*\""([^\""]+)\""\s*,\s*\""url_protected\""\s*:\s*\""([^\""]+)\""\s*\}"))
                {
                    Profiles.Add(new ProxyProfileItem {
                        Name = UnescapeJson(pMatch.Groups[1].Value),
                        Url = UnprotectString(UnescapeJson(pMatch.Groups[2].Value))
                    });
                }
                foreach (Match pMatch in Regex.Matches(mProf.Groups[1].Value, @"\{\s*\""name\""\s*:\s*\""([^\""]+)\""\s*,\s*\""url\""\s*:\s*\""([^\""]+)\""\s*\}"))
                {
                    Profiles.Add(new ProxyProfileItem {
                        Name = UnescapeJson(pMatch.Groups[1].Value),
                        Url = UnescapeJson(pMatch.Groups[2].Value)
                    });
                }
            }

            // custom_targets
            Match mCust = Regex.Match(json, @"\""custom_targets\""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (mCust.Success)
            {
                CustomTargets.Clear();
                foreach (Match cMatch in Regex.Matches(mCust.Groups[1].Value, @"\{\s*\""id\""\s*:\s*\""([^\""]+)\""\s*,\s*\""name\""\s*:\s*\""([^\""]+)\""\s*,\s*\""exe\""\s*:\s*\""([^\""]*)\""\s*\}"))
                {
                    CustomTargets.Add(new AppTarget {
                        Id = UnescapeJson(cMatch.Groups[1].Value),
                        DisplayName = UnescapeJson(cMatch.Groups[2].Value),
                        ExePath = UnescapeJson(cMatch.Groups[3].Value),
                        IsCustom = true
                    });
                }
            }

            Match mEnvBackup = Regex.Match(json, @"\""environment_backup\""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (mEnvBackup.Success)
            {
                EnvironmentBackup.Clear();
                foreach (Match entry in Regex.Matches(mEnvBackup.Groups[1].Value, @"\{\s*\""name\""\s*:\s*\""([^\""]+)\""\s*,\s*\""value_protected\""\s*:\s*\""([^\""]*)\""\s*\}"))
                    EnvironmentBackup[UnescapeJson(entry.Groups[1].Value)] = UnprotectString(UnescapeJson(entry.Groups[2].Value));
            }
        }

        public void CaptureEnvironmentBackup(IEnumerable<string> names)
        {
            if (EnvironmentBackup.Count > 0) return;
            foreach (string name in names)
                EnvironmentBackup[name] = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? "__DEVPROXY_NULL__";
            Save();
        }

        public void RestoreEnvironmentBackup()
        {
            foreach (var entry in EnvironmentBackup)
            {
                string value = entry.Value == "__DEVPROXY_NULL__" ? null : entry.Value;
                Environment.SetEnvironmentVariable(entry.Key, value, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(entry.Key, value, EnvironmentVariableTarget.Process);
            }
            EnvironmentBackup.Clear();
            Save();
        }

        string ScanExistingIdeProxy()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] potentialConfigs = new string[] {
                Path.Combine(appData, @"Antigravity\User\settings.json"),
                Path.Combine(appData, @"Cursor\User\settings.json"),
                Path.Combine(appData, @"Code\User\settings.json"),
                Path.Combine(appData, @"Windsurf\User\settings.json"),
                Path.Combine(appData, @"VSCodium\User\settings.json")
            };

            foreach (string path in potentialConfigs)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        string content = File.ReadAllText(path, Encoding.UTF8);
                        Match m = Regex.Match(content, @"\""http\.proxy\""\s*:\s*\""([^\""]+)\""");
                        if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value))
                        {
                            return m.Groups[1].Value;
                        }
                    }
                    catch { }
                }
            }

            // Environment variable
            string envProxy = Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("ALL_PROXY");
            if (!string.IsNullOrEmpty(envProxy)) return envProxy;

            return null;
        }

        public void AddProfileIfMissing(string name, string url)
        {
            foreach (var p in Profiles)
            {
                if (p.Url.Equals(url, StringComparison.OrdinalIgnoreCase)) return;
            }
            Profiles.Add(new ProxyProfileItem { Name = name, Url = url });
        }

        void EnsureDefaultProfiles()
        {
            AddProfileIfMissing("SOCKS5 Local (:10808)", "socks5://127.0.0.1:10808");
            AddProfileIfMissing("Clash / Sing-box (:7890)", "http://127.0.0.1:7890");
            AddProfileIfMissing("SOCKS Local (:2080)", "socks5://127.0.0.1:2080");
        }

        static string EscapeJson(string str)
        {
            if (str == null) return "";
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string UnescapeJson(string str)
        {
            if (str == null) return "";
            return str.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        static string ProtectString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] plain = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        static string UnprotectString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] protectedBytes = Convert.FromBase64String(value);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
    }

    public static class ProxyEngine
    {
        static readonly string[] ProxyEnvironmentNames = new string[] {
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "AGY_PROXY_URL",
            "http_proxy", "https_proxy", "all_proxy", "agy_proxy_url", "NO_PROXY", "no_proxy"
        };
        public static string NormalizeProxy(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim().Trim('"', '\'');
            if (raw.IndexOf("://", StringComparison.Ordinal) < 0) raw = "http://" + raw;
            Uri parsed;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out parsed) || string.IsNullOrEmpty(parsed.Host) || parsed.Port <= 0)
                return null;
            string scheme = parsed.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https") return null;
            return parsed.AbsoluteUri.TrimEnd('/');
        }

        public static List<AppTarget> ScanAllTargets()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            var list = new List<AppTarget>();

            // 1. Google Antigravity
            string antigravityExe = FindFirstExisting(new string[] {
                Path.Combine(localAppData, @"Programs\antigravity\Antigravity.exe"),
                Path.Combine(localAppData, @"antigravity\Antigravity.exe"),
                Path.Combine(progFiles, @"Antigravity\Antigravity.exe")
            });
            list.Add(new AppTarget {
                Id = "antigravity",
                DisplayName = "Google Antigravity",
                ExePath = antigravityExe,
                ConfigPath = Path.Combine(appData, @"Antigravity\User\settings.json")
            });

            // 2. Cursor
            string cursorExe = FindFirstExisting(new string[] {
                Path.Combine(localAppData, @"Programs\cursor\Cursor.exe"),
                Path.Combine(localAppData, @"cursor\Cursor.exe"),
                Path.Combine(progFiles, @"Cursor\Cursor.exe")
            });
            list.Add(new AppTarget {
                Id = "cursor",
                DisplayName = "Cursor",
                ExePath = cursorExe,
                ConfigPath = Path.Combine(appData, @"Cursor\User\settings.json")
            });

            // 3. VS Code
            string codeExe = FindFirstExisting(new string[] {
                Path.Combine(localAppData, @"Programs\Microsoft VS Code\Code.exe"),
                Path.Combine(progFiles, @"Microsoft VS Code\Code.exe"),
                Path.Combine(progFilesX86, @"Microsoft VS Code\Code.exe")
            });
            list.Add(new AppTarget {
                Id = "vscode",
                DisplayName = "Visual Studio Code",
                ExePath = codeExe,
                ConfigPath = Path.Combine(appData, @"Code\User\settings.json")
            });

            // 4. Windsurf
            string windsurfExe = FindFirstExisting(new string[] {
                Path.Combine(localAppData, @"Programs\Windsurf\Windsurf.exe"),
                Path.Combine(localAppData, @"Windsurf\Windsurf.exe"),
                Path.Combine(progFiles, @"Windsurf\Windsurf.exe")
            });
            list.Add(new AppTarget {
                Id = "windsurf",
                DisplayName = "Windsurf",
                ExePath = windsurfExe,
                ConfigPath = Path.Combine(appData, @"Windsurf\User\settings.json")
            });

            // 5. VSCodium
            string vscodiumExe = FindFirstExisting(new string[] {
                Path.Combine(localAppData, @"Programs\VSCodium\VSCodium.exe"),
                Path.Combine(progFiles, @"VSCodium\VSCodium.exe")
            });
            list.Add(new AppTarget {
                Id = "vscodium",
                DisplayName = "VSCodium",
                ExePath = vscodiumExe,
                ConfigPath = Path.Combine(appData, @"VSCodium\User\settings.json")
            });

            // 6. OpenAI Codex CLI
            string codexExe = null;
            string codexBinDir = Path.Combine(localAppData, @"OpenAI\Codex\bin");
            if (Directory.Exists(codexBinDir))
            {
                try
                {
                    string[] matches = Directory.GetFiles(codexBinDir, "codex.exe", SearchOption.AllDirectories);
                    if (matches.Length > 0) codexExe = matches[0];
                }
                catch { }
            }
            list.Add(new AppTarget {
                Id = "codex",
                DisplayName = "Codex CLI",
                ExePath = codexExe,
                ConfigPath = null
            });

            // 8. JetBrains IDEs
            ScanJetBrains(list, progFiles, localAppData);

            // 9. Load saved custom targets from ConfigManager
            foreach (var custom in ConfigManager.Instance.CustomTargets)
            {
                list.Add(custom);
            }

            return list;
        }

        static void ScanJetBrains(List<AppTarget> list, string progFiles, string localAppData)
        {
            string jbProgramFiles = Path.Combine(progFiles, "JetBrains");
            if (Directory.Exists(jbProgramFiles))
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(jbProgramFiles))
                    {
                        string dirName = Path.GetFileName(dir);
                        string binDir = Path.Combine(dir, "bin");
                        if (Directory.Exists(binDir))
                        {
                            string[] exes = Directory.GetFiles(binDir, "*64.exe");
                            if (exes.Length > 0)
                            {
                                list.Add(new AppTarget {
                                    Id = "jb_" + dirName.ToLower().Replace(" ", "_"),
                                    DisplayName = "JetBrains " + dirName,
                                    ExePath = exes[0],
                                    ConfigPath = null
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            string jbToolbox = Path.Combine(localAppData, @"JetBrains\Toolbox\apps");
            if (Directory.Exists(jbToolbox))
            {
                try
                {
                    foreach (string appDir in Directory.GetDirectories(jbToolbox))
                    {
                        string appName = Path.GetFileName(appDir);
                        string[] exes = Directory.GetFiles(appDir, "*64.exe", SearchOption.AllDirectories);
                        if (exes.Length > 0)
                        {
                            list.Add(new AppTarget {
                                Id = "jb_tb_" + appName.ToLower(),
                                DisplayName = "JetBrains " + appName,
                                ExePath = exes[0],
                                ConfigPath = null
                            });
                        }
                    }
                }
                catch { }
            }
        }

        public static string FindFirstExisting(string[] paths)
        {
            if (paths == null) return null;
            foreach (string p in paths)
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            return null;
        }

        static string FindInPath(string exeName)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;
            string[] dirs = pathEnv.Split(';');
            foreach (string d in dirs)
            {
                try
                {
                    string full = Path.Combine(d.Trim(), exeName);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        public static bool ApplyProxyToTarget(AppTarget target, string proxyUrl, out string msg)
        {
            if (target.ConfigPath == "git://global")
            {
                msg = "Постоянное изменение глобального Git config отключено; используйте изолированную сессию или настройте Git вручную.";
                return false;
            }

            if (string.IsNullOrEmpty(target.ConfigPath))
            {
                msg = target.DisplayName + ": постоянная конфигурация для этого типа приложения не поддерживается.";
                return false;
            }

            try
            {
                string dir = Path.GetDirectoryName(target.ConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string content = "{\n}";
                if (File.Exists(target.ConfigPath))
                {
                    content = File.ReadAllText(target.ConfigPath, Encoding.UTF8);
                    string backup = target.ConfigPath + ".devproxy.bak";
                    if (!File.Exists(backup)) File.Copy(target.ConfigPath, backup, false);
                }

                var settings = ParseSettings(content);
                settings["http.proxy"] = proxyUrl;
                settings["http.proxyStrictSSL"] = true;
                settings["http.proxySupport"] = "override";
                settings["http.noProxy"] = "localhost,127.0.0.1,::1";
                content = new JavaScriptSerializer().Serialize(settings);

                WriteAtomic(target.ConfigPath, content);
                msg = "Настройки записаны в " + target.ConfigPath;
                return true;
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                return false;
            }
        }

        public static bool RestoreTarget(AppTarget target, out string msg)
        {
            if (target.ConfigPath == "git://global")
            {
                msg = "DevProxy не изменяет глобальный Git config.";
                return false;
            }

            if (string.IsNullOrEmpty(target.ConfigPath) || !File.Exists(target.ConfigPath))
            {
                msg = "Файл конфигурации не найден.";
                return false;
            }

            try
            {
                string bak = target.ConfigPath + ".devproxy.bak";
                if (File.Exists(bak))
                {
                    var currentSettings = ParseSettings(File.ReadAllText(target.ConfigPath, Encoding.UTF8));
                    var originalSettings = ParseSettings(File.ReadAllText(bak, Encoding.UTF8));
                    string[] managedKeys = new string[] { "http.proxy", "http.proxyStrictSSL", "http.proxySupport", "http.noProxy" };
                    foreach (string key in managedKeys)
                    {
                        if (originalSettings.ContainsKey(key)) currentSettings[key] = originalSettings[key];
                        else currentSettings.Remove(key);
                    }
                    WriteAtomic(target.ConfigPath, new JavaScriptSerializer().Serialize(currentSettings));
                    File.Delete(bak);
                    msg = "Прокси-ключи восстановлены из .devproxy.bak; остальные изменения сохранены.";
                    return true;
                }

                string content = File.ReadAllText(target.ConfigPath, Encoding.UTF8);
                var settings = ParseSettings(content);
                settings.Remove("http.proxy");
                settings.Remove("http.proxyStrictSSL");
                settings.Remove("http.proxySupport");
                settings.Remove("http.noProxy");
                content = new JavaScriptSerializer().Serialize(settings);

                WriteAtomic(target.ConfigPath, content);
                msg = "Ключи прокси удалены из " + target.ConfigPath;
                return true;
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                return false;
            }
        }

        public static void UpdateAppShortcut(AppTarget target, string proxyUrl, bool remove)
        {
            try
            {
                if (string.IsNullOrEmpty(target.ExePath)) return;
                string exeName = Path.GetFileNameWithoutExtension(target.ExePath);
                string[] desktopDirs = new string[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive\\Desktop")
                };

                foreach (string d in desktopDirs)
                {
                    if (!Directory.Exists(d)) continue;
                    string lnkPath = Path.Combine(d, exeName + ".lnk");
                    if (!File.Exists(lnkPath))
                    {
                        foreach (string file in Directory.GetFiles(d, "*.lnk"))
                        {
                            if (Path.GetFileNameWithoutExtension(file).Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                                (target.Id == "antigravity" && Path.GetFileNameWithoutExtension(file).IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                lnkPath = file;
                                break;
                            }
                        }
                    }

                    if (File.Exists(lnkPath))
                    {
                        UpdateShortcutFile(lnkPath, target.ExePath, proxyUrl, remove);
                    }
                }
            }
            catch { }
        }

        static void WriteAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".devproxy-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, content, Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        static void UpdateShortcutFile(string lnkPath, string exePath, string proxyUrl, bool remove)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type scType = shortcut.GetType();

                string existingArguments = Convert.ToString(scType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null));
                string existingTarget = Convert.ToString(scType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null));
                if (existingArguments.StartsWith("--shortcut-launch ", StringComparison.Ordinal) &&
                    (Path.GetFileNameWithoutExtension(existingTarget).StartsWith("DevProxy", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileNameWithoutExtension(existingTarget).Equals("DagamiAgentProxy", StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Log("[Shortcut] Управляемый ярлык сохранён; для отката используйте «Вернуть прямой запуск».");
                    return;
                }

                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath) });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { exePath + ",0" });

                if (remove || string.IsNullOrEmpty(proxyUrl))
                {
                    scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "" });
                }
                else
                {
                    scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--proxy-server=\"" + proxyUrl + "\" --proxy-bypass-list=\"<-loopback>;<local>;localhost;127.0.0.1;::1\"" });
                }

                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Logger.Log("[*] Ярлык на рабочем столе обновлен: " + Path.GetFileName(lnkPath));
            }
            catch (Exception ex)
            {
                Logger.Log("[-] Ошибка обновления ярлыка: " + ex.Message);
            }
            finally {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        const int HWND_BROADCAST = 0xffff;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        public static void BroadcastEnvironmentChange()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out result);
            }
            catch { }
        }

        public static void ApplySystemEnvironment(string proxyUrl)
        {
            try
            {
                ConfigManager.Instance.CaptureEnvironmentBackup(ProxyEnvironmentNames);
                // Process environment (inherited by child processes started by DevProxy)
                Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("ALL_PROXY", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("AGY_PROXY_URL", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("http_proxy", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("https_proxy", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("all_proxy", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("agy_proxy_url", proxyUrl, EnvironmentVariableTarget.Process);
                string noProxyVal = "localhost,127.0.0.1,::1,127.0.0.0/8,*.local";
                Environment.SetEnvironmentVariable("NO_PROXY", noProxyVal, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("no_proxy", noProxyVal, EnvironmentVariableTarget.Process);

                // User environment (persisted in Windows registry for shell and other apps)
                Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("ALL_PROXY", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("AGY_PROXY_URL", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("http_proxy", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("https_proxy", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("all_proxy", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("agy_proxy_url", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("NO_PROXY", noProxyVal, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("no_proxy", noProxyVal, EnvironmentVariableTarget.User);

                BroadcastEnvironmentChange();
            }
            catch { }
        }

        public static void ClearSystemEnvironment()
        {
            try
            {
                if (ConfigManager.Instance.EnvironmentBackup.Count == 0)
                {
                    Logger.Log("[*] Environment rollback skipped: DevProxy has no saved baseline.");
                    return;
                }
                Environment.SetEnvironmentVariable("HTTP_PROXY", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("ALL_PROXY", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("AGY_PROXY_URL", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("http_proxy", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("https_proxy", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("all_proxy", null, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("agy_proxy_url", null, EnvironmentVariableTarget.Process);

                Environment.SetEnvironmentVariable("HTTP_PROXY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("ALL_PROXY", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("AGY_PROXY_URL", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("http_proxy", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("https_proxy", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("all_proxy", null, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("agy_proxy_url", null, EnvironmentVariableTarget.User);

                ConfigManager.Instance.RestoreEnvironmentBackup();

                BroadcastEnvironmentChange();
            }
            catch { }
        }

        public static void RunGitCmd(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(3000);
            }
            catch { }
        }

        public static bool ApplyProxyAll(string proxyUrl)
        {
            bool attempted = false;
            bool success = true;
            foreach (var t in ScanAllTargets())
            {
                string msg;
                if (ApplyProxyToTarget(t, proxyUrl, out msg))
                {
                    attempted = true;
                    Console.WriteLine("[+] " + t.DisplayName + ": " + msg);
                }
                else if (!string.IsNullOrEmpty(t.ConfigPath) && t.ConfigPath != "git://global")
                {
                    attempted = true;
                    success = false;
                    Logger.Log("[-] " + t.DisplayName + ": " + msg);
                }
            }
            if (ConfigManager.Instance.SetSystemEnv) ApplySystemEnvironment(proxyUrl);
            return attempted && success;
        }

        public static bool RemoveAll()
        {
            bool restored = false;
            foreach (var t in ScanAllTargets())
            {
                string msg;
                if (RestoreTarget(t, out msg))
                {
                    restored = true;
                    Console.WriteLine("[+] " + t.DisplayName + ": " + msg);
                }
            }
            if (ConfigManager.Instance.SetSystemEnv) ClearSystemEnvironment();
            return restored;
        }

        public static void ShowStatus()
        {
            foreach (var t in ScanAllTargets())
            {
                Console.WriteLine("• " + t.DisplayName + (t.IsInstalled ? " (Установлено)" : " (Не найдено)"));
            }
        }

        public static string StripJsonComments(string json)
        {
            StringBuilder sb = new StringBuilder();
            bool inString = false;
            bool escape = false;
            int i = 0;
            int n = json.Length;
            while (i < n)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (c == '/' && i + 1 < n)
                {
                    if (json[i + 1] == '/')
                    {
                        while (i < n && json[i] != '\n') i++;
                        if (i < n) { sb.Append(json[i]); i++; }
                        continue;
                    }
                    if (json[i + 1] == '*')
                    {
                        i += 2;
                        while (i + 1 < n && !(json[i] == '*' && json[i + 1] == '/')) i++;
                        i += 2;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static Dictionary<string, object> ParseSettings(string json)
        {
            string cleaned = StripTrailingCommas(StripJsonComments(json));
            var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cleaned);
            if (settings == null) throw new InvalidDataException("Configuration root must be a JSON object.");
            return settings;
        }

        static string StripTrailingCommas(string json)
        {
            StringBuilder output = new StringBuilder();
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char current = json[i];
                if (inString)
                {
                    output.Append(current);
                    if (escape) escape = false;
                    else if (current == '\\') escape = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    output.Append(current);
                    continue;
                }
                if (current == ',')
                {
                    int lookahead = i + 1;
                    while (lookahead < json.Length && char.IsWhiteSpace(json[lookahead])) lookahead++;
                    if (lookahead < json.Length && (json[lookahead] == '}' || json[lookahead] == ']')) continue;
                }
                output.Append(current);
            }
            return output.ToString();
        }

        static string UpsertJsonField(string json, string key, string value)
        {
            string pattern = @"\""" + Regex.Escape(key) + @"\""\s*:\s*(?:\""[^\""]*\""|true|false|\d+|null)";
            if (Regex.IsMatch(json, pattern))
            {
                return Regex.Replace(json, pattern, "\"" + key + "\": " + value);
            }
            int lastBrace = json.LastIndexOf('}');
            if (lastBrace >= 0)
            {
                string prefix = json.Substring(0, lastBrace).TrimEnd();
                bool hasProps = prefix.Length > 0 && prefix[prefix.Length - 1] != '{';
                return prefix + (hasProps ? ",\n" : "\n") + "  \"" + key + "\": " + value + "\n}";
            }
            return json;
        }

        static string RemoveJsonField(string json, string key)
        {
            string pattern = @"\""" + Regex.Escape(key) + @"\""\s*:\s*(?:\""[^\""]*\""|true|false|\d+|null)\s*,?";
            string res = Regex.Replace(json, pattern, "");
            res = Regex.Replace(res, @",\s*(\})", "$1");
            return res;
        }

        static string EscapeJson(string str)
        {
            if (str == null) return "";
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    public static class DnsManager
    {
        public const string PrimaryDns = "185.149.145.6";
        public const string SecondaryDns = "1.1.1.1";
        public const string DnsHost = "dns.dagami.ru";

        public static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        static bool RunElevatedScript(string psCommands, string successDesc, out string msg)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "devproxy_dns_" + Guid.NewGuid().ToString("N") + ".ps1");
            string resultFile = Path.Combine(Path.GetTempPath(), "devproxy_dns_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                string fullScript =
                    "$adapterNames = ''\r\n" +
                    "try {\r\n" +
                    psCommands + "\r\n" +
                    "[System.IO.File]::WriteAllText('" + resultFile.Replace("\\", "\\\\") + "', ('OK:' + $adapterNames), [System.Text.Encoding]::UTF8)\r\n" +
                    "exit 0\r\n" +
                    "} catch {\r\n" +
                    "[System.IO.File]::WriteAllText('" + resultFile.Replace("\\", "\\\\") + "', ('ERR:' + $_.Exception.Message), [System.Text.Encoding]::UTF8)\r\n" +
                    "exit 1\r\n" +
                    "}";

                File.WriteAllText(tempFile, fullScript, Encoding.UTF8);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + tempFile + "\"";
                if (IsAdministrator())
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                }
                else
                {
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }

                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(15000);
                    }
                }

                if (File.Exists(resultFile))
                {
                    string output = File.ReadAllText(resultFile, Encoding.UTF8).Trim();
                    if (output.StartsWith("OK:"))
                    {
                        string adapters = output.Substring(3).Trim();
                        msg = successDesc + (!string.IsNullOrEmpty(adapters) ? " для: " + adapters : "");
                        return true;
                    }
                    if (output.StartsWith("ERR:"))
                    {
                        msg = "Ошибка настройки DNS: " + output.Substring(4);
                        return false;
                    }
                }
                msg = successDesc;
                return true;
            }
            catch (System.ComponentModel.Win32Exception wEx)
            {
                if (wEx.NativeErrorCode == 1223) // ERROR_CANCELLED (user clicked "No" on UAC)
                {
                    msg = "Настройка DNS отменена пользователем (отклонен запрос прав администратора).";
                    return false;
                }
                msg = "Ошибка вызова UAC: " + wEx.Message;
                return false;
            }
            catch (Exception ex)
            {
                msg = "Ошибка при установке DNS: " + ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
            }
        }

        public static bool ApplyDagamiDns(out string msg)
        {
            string commands = string.Format(
                "$adapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Status -eq 'Up' -and $_.InterfaceDescription -notlike '*Virtual*' -and $_.InterfaceDescription -notlike '*VMware*' }};\r\n" +
                "if (-not $adapters) {{ $adapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Status -eq 'Up' }} }};\r\n" +
                "foreach ($a in $adapters) {{\r\n" +
                "    Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction SilentlyContinue;\r\n" +
                "    Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ('{0}', '{1}') -ErrorAction SilentlyContinue\r\n" +
                "}};\r\n" +
                "Clear-DnsClientCache -ErrorAction SilentlyContinue;\r\n" +
                "$adapterNames = ($adapters | Select-Object -ExpandProperty Name) -join ', '",
                PrimaryDns, SecondaryDns
            );
            return RunElevatedScript(commands, "DNS dagami (" + PrimaryDns + ") установлен", out msg);
        }

        public static bool RestoreDns(out string msg)
        {
            string commands =
                "$adapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' };\r\n" +
                "foreach ($a in $adapters) {\r\n" +
                "    Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction SilentlyContinue\r\n" +
                "};\r\n" +
                "Clear-DnsClientCache -ErrorAction SilentlyContinue;\r\n" +
                "$adapterNames = ($adapters | Select-Object -ExpandProperty Name) -join ', '";
            return RunElevatedScript(commands, "DNS успешно сброшен на DHCP (автоматический)", out msg);
        }

        public static bool TestDnsServer(out string msg)
        {
            try
            {
                var t0 = DateTime.Now;
                using (var client = new UdpClient())
                {
                    client.Client.ReceiveTimeout = 2500;
                    client.Client.SendTimeout = 2500;
                    client.Connect(PrimaryDns, 53);

                    // Standard DNS query packet for google.com
                    byte[] query = new byte[] {
                        0xAA, 0xBB, // ID
                        0x01, 0x00, // Recursion desired
                        0x00, 0x01, // QDCOUNT: 1
                        0x00, 0x00, // ANCOUNT: 0
                        0x00, 0x00, // NSCOUNT: 0
                        0x00, 0x00, // ARCOUNT: 0
                        // 6 google 3 com 0
                        0x06, 0x67, 0x6F, 0x6F, 0x67, 0x6C, 0x65,
                        0x03, 0x63, 0x6F, 0x6D,
                        0x00,
                        0x00, 0x01, // Type A
                        0x00, 0x01  // Class IN
                    };
                    client.Send(query, query.Length);
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] resp = client.Receive(ref ep);
                    double ms = (DateTime.Now - t0).TotalMilliseconds;
                    if (resp != null && resp.Length > 12)
                    {
                        msg = string.Format("DNS dagami ({0}:53) доступен! Задержка: {1:F0} мс.", PrimaryDns, ms);
                        return true;
                    }
                }
                msg = "DNS dagami (" + PrimaryDns + ") не ответил вовремя.";
                return false;
            }
            catch (Exception ex)
            {
                msg = "Ошибка проверки DNS: " + ex.Message;
                return false;
            }
        }
    }

    public partial class MainForm : Form
    {
        private int _bridgePort = 0;
        private NotifyIcon _trayIcon;
        private ContextMenu _trayMenu;
        private LocalProxyBridge _rescueBridge;
        private Process _activeSessionProc;
        private JobObjectManager _activeJobMgr;
        private List<AppTarget> _cachedTargets = new List<AppTarget>();
        private readonly List<string> _logQueue = new List<string>();
        private readonly object _logLock = new object();
        private readonly bool _shortcutSession;
        private readonly string _shortcutTargetId;
        private bool _exitRequested;

        public MainForm(string shortcutTargetId = null, bool shortcutSession = false, bool preview = false)
        {
            _shortcutTargetId = shortcutTargetId;
            _shortcutSession = shortcutSession;
            this.ShowInTaskbar = false;
            this.Opacity = 0;

            if (!preview) { InitLogging(); StartRescueBridge(); }
            _cachedTargets = ProxyEngine.ScanAllTargets();
            BuildFluentUi();
            if (!preview) InitTray();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_shortcutSession)
            {
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                this.Hide();
                BeginInvoke(new Action(StartShortcutSession));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_exitRequested) {
                e.Cancel = true;
                Hide();
                Logger.Log("[Session] Window hidden; proxy remains active in the tray.");
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DisposeNativeSessions();
            if (_rescueBridge != null) _rescueBridge.Dispose();
            if (_trayIcon != null) _trayIcon.Dispose();
            if (_nativeLogTimer != null) _nativeLogTimer.Dispose();
            Logger.OnLog = null;
            base.OnFormClosed(e);
        }

        private void InitLogging()
        {
            Logger.OnLog = (msg) => {
                lock (_logLock)
                {
                    _logQueue.Add(msg);
                    if (_logQueue.Count > 1000) _logQueue.RemoveAt(0);
                }
            };
        }

        private void InitTray()
        {
            _trayMenu = new ContextMenu();
            _trayMenu.MenuItems.Add(new MenuItem("Открыть Dagami Agent Proxy", (s, e) => ShowNativeWindow()));
            _trayMenu.MenuItems.Add("-");
            _trayMenu.MenuItems.Add(new MenuItem("Тест прокси-соединения", (s, e) => DoTestProxy()));
            _trayMenu.MenuItems.Add(new MenuItem("Пересканировать IDE", (s, e) => RefreshNativeTargets()));
            _trayMenu.MenuItems.Add("-");
            _trayMenu.MenuItems.Add(new MenuItem("Выход", (s, e) => ExitApplication()));

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = Brand.Name;
            try { _trayIcon.Icon = CreateAppIcon(); } catch { _trayIcon.Icon = SystemIcons.Application; }
            _trayIcon.ContextMenu = _trayMenu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => ShowNativeWindow();
        }

        private Icon CreateAppIcon() { return Brand.LoadIcon(); }

        private void StartRescueBridge()
        {
            try
            {
                string proxyUrl = ConfigManager.Instance.LastProxy;
                if (!string.IsNullOrEmpty(proxyUrl))
                {
                    Uri parsedProxy;
                    if (!TryParseSupportedProxy(proxyUrl, out parsedProxy))
                    {
                        Logger.Log("[-] C# bridge supports only HTTP and HTTPS upstream proxies. Use the Python GUI for SOCKS5.");
                        return;
                    }
                    _rescueBridge = new LocalProxyBridge();
                    if (_rescueBridge.Start(53200, proxyUrl, ConfigManager.Instance.UseDagamiDns ? ConfigManager.Instance.DnsHost : null))
                    {
                        _bridgePort = _rescueBridge.BoundPort;
                        Logger.Log("[+] Спасательный мост запущен на 127.0.0.1:" + _bridgePort);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[-] Ошибка запуска моста: " + ex.Message);
            }
        }

        private void StartShortcutSession()
        {
            string error = HandleShortcutRequest(new NativeLaunchRequest { TargetId = _shortcutTargetId, ShortcutKey = InitialShortcutKey });
            if (!string.IsNullOrEmpty(error)) {
                MessageBox.Show(error, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitApplication();
            }
        }

        private bool LaunchTarget(AppTarget target, out string error) { return LaunchTarget(target, "", null, out error); }

        private bool LaunchTarget(AppTarget target, string originalArguments, string originalDirectory, out string error)
        {
            error = null;
            if (_bridgePort <= 0)
            {
                error = "Локальный прокси-мост не запущен. Укажите рабочий HTTP или HTTPS upstream proxy.";
                return false;
            }
            if (target == null || string.IsNullOrEmpty(target.ExePath) || !File.Exists(target.ExePath))
            {
                error = "Исполняемый файл приложения не найден.";
                return false;
            }
            NativeSession session;
            _nativeSessions.TryGetValue(target.Id, out session);
            bool reuse = session != null && session.RunningProcess != null;
            if (reuse && string.IsNullOrWhiteSpace(originalArguments)) {
                _activeSessionProc = session.RunningProcess; _activeJobMgr = session.Job;
                FocusSession(_activeSessionProc); Logger.Log("[Shortcut] Уже работающая сессия: " + target.DisplayName); return true;
            }
            try
            {
                bool antigravity = Path.GetFileName(target.ExePath).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase);
                if (!reuse && IsElectronTarget(target)) NativeLogin.RequireStopped(target.ExePath);
                if (antigravity && !reuse && ConfigManager.Instance.PrepareAntigravityClient)
                    Logger.Log("[Antigravity] " + AntigravityIntegration.Prepare(target.ExePath));
                string localProxy = "http://127.0.0.1:" + _bridgePort;
                var psi = new ProcessStartInfo(target.ExePath);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = string.IsNullOrEmpty(originalDirectory) ? Path.GetDirectoryName(target.ExePath) : Environment.ExpandEnvironmentVariables(originalDirectory);
                psi.Arguments = originalArguments ?? "";

                string noProxy = "localhost,127.0.0.1,::1";
                foreach (string name in new string[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                    psi.EnvironmentVariables[name] = localProxy;
                psi.EnvironmentVariables["NO_PROXY"] = noProxy;
                psi.EnvironmentVariables["no_proxy"] = noProxy;
                // Support a server already prepared by an external unlocker too.
                psi.EnvironmentVariables["AG_LS_PROXY"] = localProxy;
                psi.EnvironmentVariables["AGY_PROXY_URL"] = localProxy;
                psi.EnvironmentVariables["agy_proxy_url"] = localProxy;
                psi.EnvironmentVariables["DEVPROXY_DNS_HOST"] = ConfigManager.Instance.DnsHost;

                if (IsElectronTarget(target))
                {
                    string profileRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "devproxy", "profiles", SafeFileName(target.Id));
                    psi.Arguments += " " + NativeLogin.ElectronArguments(target, localProxy, profileRoot);
                }

                if (!reuse) {
                    if (session != null) session.Dispose();
                    session = new NativeSession(); _nativeSessions[target.Id] = session;
                }
                Process launched = Process.Start(psi);
                if (launched == null) { error = "Процесс приложения не был создан."; return false; }
                session.Processes.Add(launched);
                try { session.Job.AssignProcess(launched.Handle); }
                catch (Exception jobEx) { Logger.Log("[!] Процесс запущен без Job Object: " + jobEx.GetType().Name); }
                _activeSessionProc = session.RunningProcess ?? launched; _activeJobMgr = session.Job;
                Logger.Log("[✓] Процесс запущен: " + target.DisplayName + " (PID: " + launched.Id + "). Маршрут передан дочерним процессам.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Не удалось запустить приложение: " + ex.Message;
                Logger.Log("[-] " + error);
                return false;
            }
        }

        private static bool IsElectronTarget(AppTarget target)
        {
            string marker = ((target.Id ?? "") + " " + (target.DisplayName ?? "") + " " + (target.ExePath ?? "")).ToLowerInvariant();
            return marker.Contains("antigravity") || marker.Contains("cursor") || marker.Contains("windsurf") ||
                marker.Contains("vscode") || marker.Contains("visual studio code") || marker.Contains("vscodium");
        }

        private static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "application";
            foreach (char ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
            return value.Trim();
        }

        private static string StableTargetId(string exePath)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToLowerInvariant());
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder("custom-");
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static List<string> CreateManagedShortcuts(AppTarget target)
        {
            var created = new List<string>();
            string managerExe = Process.GetCurrentProcess().MainModule.FileName;
            string fileName = SafeFileName(target.DisplayName) + " — Dagami Agent Proxy.lnk";
            string[] directories = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };
            foreach (string directory in directories)
            {
                if (string.IsNullOrEmpty(directory)) continue;
                Directory.CreateDirectory(directory);
                string shortcutPath = Path.Combine(directory, fileName);
                object shell = null;
                object shortcut = null;
                try
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType == null) throw new InvalidOperationException("Windows Script Host недоступен.");
                    shell = Activator.CreateInstance(shellType);
                    shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    Type shortcutType = shortcut.GetType();
                    shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { managerExe });
                    shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--shortcut-launch \"" + target.Id + "\"" });
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(managerExe) });
                    shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target.ExePath + ",0" });
                    shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Запустить " + target.DisplayName + " через Dagami Agent Proxy" });
                    shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    created.Add(shortcutPath);
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                }
            }
            return created;
        }

        private bool CheckProxyConnection(string proxyUrl)
        {
            if (string.IsNullOrEmpty(proxyUrl)) return false;
            try
            {
                Uri u = new Uri(proxyUrl);
                using (var client = new TcpClient())
                {
                    var result = client.BeginConnect(u.Host, u.Port > 0 ? u.Port : 8080, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(3000);
                    if (success) client.EndConnect(result);
                    return success;
                }
            }
            catch { return false; }
        }

        private void RestartRescueBridge()
        {
            if (_rescueBridge != null)
            {
                _rescueBridge.Dispose();
                _rescueBridge = null;
            }
            _bridgePort = 0;
            StartRescueBridge();
        }

        private string GetIdeProxyUrl()
        {
            if (_bridgePort <= 0)
                throw new InvalidOperationException("Локальный прокси-мост не запущен.");
            return "http://127.0.0.1:" + _bridgePort;
        }

        private static bool TryParseSupportedProxy(string value, out Uri proxy)
        {
            proxy = null;
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out proxy))
                return false;
            string scheme = proxy.Scheme.ToLowerInvariant();
            return !string.IsNullOrEmpty(proxy.Host) && proxy.Port > 0 &&
                (scheme == "http" || scheme == "https");
        }

        private void DoTestProxy()
        {
            ThreadPool.QueueUserWorkItem((o) => {
                Logger.Log("[*] Запуск тестирования соединения...");
                var sw = Stopwatch.StartNew();
                bool ok = CheckProxyConnection(ConfigManager.Instance.LastProxy);
                sw.Stop();
                if (ok)
                    _trayIcon.ShowBalloonTip(3000, Brand.Name, "Прокси-сервер доступен (" + sw.ElapsedMilliseconds + " ms)", ToolTipIcon.Info);
                else
                    _trayIcon.ShowBalloonTip(3000, Brand.Name, "Ошибка подключения к вышестоящему прокси", ToolTipIcon.Warning);
            });
        }

        private void ExitApplication()
        {
            if (_patchInProgress) {
                ShowNativeWindow();
                _appFeedback.Text = "Дождитесь завершения подготовки перед выходом из DevProxy.";
                return;
            }
            _exitRequested = true;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_rescueBridge != null)
            {
                _rescueBridge.Dispose();
            }
            Application.Exit();
        }
    }
}
