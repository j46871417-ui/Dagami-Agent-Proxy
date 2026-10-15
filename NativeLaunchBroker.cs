using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DevProxyApp
{
    public sealed class NativeLaunchRequest {
        public string TargetId { get; set; }
        public string ShortcutKey { get; set; }
    }
    public sealed class NativeLaunchBroker : IDisposable {
        public static string UserName { get { return "DevProxy.Launch.v1." + WindowsIdentity.GetCurrent().User.Value; } }
        readonly string _name;
        readonly Func<NativeLaunchRequest, string> _launch;
        volatile bool _running;
        NamedPipeServerStream _server;
        Thread _thread;
        public NativeLaunchBroker(string name, Func<NativeLaunchRequest, string> launch) { _name = name; _launch = launch; }
        public void Start() { _running = true; _thread = new Thread(Serve); _thread.IsBackground = true; _thread.Start(); }
        static byte[] Read(Stream stream, int length) {
            var buffer = new byte[length]; int offset = 0;
            while (offset < length) {
                var read = stream.BeginRead(buffer, offset, length - offset, null, null);
                using (read.AsyncWaitHandle) {
                    if (!read.AsyncWaitHandle.WaitOne(30000)) throw new TimeoutException("Launch channel timeout.");
                    int n = stream.EndRead(read); if (n == 0) throw new EndOfStreamException(); offset += n;
                }
            }
            return buffer;
        }
        static void WriteMessage(Stream stream, string message) {
            byte[] bytes = Encoding.UTF8.GetBytes(message); if (bytes.Length > 4096) throw new InvalidDataException("Launch message too large.");
            byte[] size = BitConverter.GetBytes(bytes.Length); stream.Write(size, 0, size.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
        }
        static string ReadMessage(Stream stream) {
            int length = BitConverter.ToInt32(Read(stream, 4), 0);
            if (length < 0 || length > 4096) throw new InvalidDataException("Invalid launch message length.");
            return Encoding.UTF8.GetString(Read(stream, length));
        }
        void Serve() {
            while (_running) {
                try {
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security)) {
                        _server = server; if (!_running) return;
                        server.WaitForConnection();
                        var request = new JavaScriptSerializer { MaxJsonLength = 4096 }.Deserialize<NativeLaunchRequest>(ReadMessage(server));
                        if (request == null || (request.TargetId != null && request.TargetId.Length > 160) || (request.ShortcutKey != null && request.ShortcutKey.Length > 32)) throw new InvalidDataException("Invalid launch request.");
                        string error = _launch(request);
                        WriteMessage(server, error ?? "");
                    }
                } catch (Exception ex) { if (_running) Logger.Log("[Launch IPC] " + ex.GetType().Name); }
                finally { _server = null; }
            }
        }
        public static string Send(string name, NativeLaunchRequest request, int deadlineMs) {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < deadlineMs) {
                bool sent = false;
                try {
                    using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification)) {
                        client.Connect(Math.Min(500, Math.Max(1, deadlineMs - (int)clock.ElapsedMilliseconds)));
                        sent = true;
                        WriteMessage(client, new JavaScriptSerializer().Serialize(request));
                        return ReadMessage(client);
                    }
                } catch (TimeoutException) { if (sent) return "DevProxy ещё обрабатывает запуск. Проверьте приложение или значок в трее."; }
                catch (IOException) { if (sent) return "Канал запуска закрылся после отправки запроса. Повторная отправка автоматически не выполнялась."; }
                Thread.Sleep(100);
            }
            return "Фоновый DevProxy не ответил. Откройте его через значок в трее и проверьте журнал.";
        }
        public void Dispose() { _running = false; try { if (_server != null) _server.Dispose(); } catch { } }
        public static void Run(NativeLaunchRequest initial) {
            bool owner = false;
            using (var mutex = new Mutex(false, @"Local\" + UserName)) {
                try { owner = mutex.WaitOne(0); } catch (AbandonedMutexException) { owner = true; }
                if (!owner) {
                    string error = Send(UserName, initial ?? new NativeLaunchRequest(), 12000);
                    if (!string.IsNullOrEmpty(error)) MessageBox.Show(error, "DevProxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try {
                    if (!LicenseManager.IsActivated()) {
                        using (var activation = new ActivationForm()) {
                            if (activation.ShowDialog() != DialogResult.OK) {
                                return;
                            }
                        }
                    }
                    using (var form = new MainForm(initial == null ? null : initial.TargetId, initial != null)) {
                        form.InitialShortcutKey = initial == null ? null : initial.ShortcutKey;
                        IntPtr handle = form.Handle;
                        using (var broker = new NativeLaunchBroker(UserName, request => (string)form.Invoke(new Func<string>(() => form.HandleShortcutRequest(request))))) {
                            broker.Start(); Application.Run(form);
                        }
                    }
                } finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
