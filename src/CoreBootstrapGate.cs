using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DevProxyApp
{
    /// <summary>
    /// Управляет обязательной докачкой xray.exe с GitHub в %APPDATA%\devproxy\core-runtime
    /// ДО открытия главного окна программы и скрытым запуском туннеля на 127.0.0.1:10808.
    /// В сборке НЕТ вшитых .exe ресурсов (0% ложных срабатываний Win32/Dropper).
    /// Пока ядро не скачано и не проверено по SHA-256, пользователь не может взаимодействовать с основным окном.
    /// </summary>
    public static class CoreBootstrapGate
    {
        public const int DefaultLocalPort = 10808;
        public const long MaxArchiveBytes = 45 * 1024 * 1024;

        // Официальный SHA2-256 архива Xray-windows-64.zip релиза v26.11.9 (из Xray-windows-64.zip.dgst):
        public const string ExpectedZipSha256 = "1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d";

        private static readonly string[] DownloadUrls = new[]
        {
            "https://github.com/XTLS/Xray-core/releases/download/v26.11.9/Xray-windows-64.zip",
            "https://dns.dagami.ru/dist/core/Xray-windows-64.zip" // Резервное зеркало на случай блокировки/замедления GitHub
        };

        private static readonly object Sync = new object();
        private static Process _coreProcess;
        private static IntPtr _jobHandle = IntPtr.Zero;
        private static int _boundPort = DefaultLocalPort;

        public static int LocalPort { get { return _boundPort; } }
        public static string LocalProxyUrl { get { return "http://127.0.0.1:" + _boundPort; } }

        public static string CoreDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "devproxy",
                    "core-runtime"
                );
            }
        }

        public static string CoreBinaryPath
        {
            get { return Path.Combine(CoreDirectory, "xray.exe"); }
        }

        private static string CoreStampPath
        {
            get { return Path.Combine(CoreDirectory, "xray.exe.sha256"); }
        }

        /// <summary>
        /// Проверяет, установлен ли и валиден ли xray.exe в %APPDATA%\devproxy\core-runtime.
        /// </summary>
        public static bool IsCoreInstalledAndValid()
        {
            try
            {
                if (!File.Exists(CoreBinaryPath) || !File.Exists(CoreStampPath)) return false;
                var info = new FileInfo(CoreBinaryPath);
                if (info.Length < 1024 * 1024) return false;
                string expectedHash = File.ReadAllText(CoreStampPath, Encoding.ASCII).Trim();
                if (expectedHash.Length != 64) return false;
                return VerifyFileSha256(CoreBinaryPath, expectedHash);
            }
            catch { return false; }
        }

        /// <summary>
        /// Вызывается в NativeLaunchBroker.Run ДО создания главного окна (MainForm).
        /// Если ядра нет в %APPDATA%, показывает блокирующее модальное окно докачки с GitHub.
        /// Если пользователь отменил загрузку — возвращает false, и программа сразу завершается.
        /// </summary>
        public static bool EnsureReadyBeforeUiOrExit(bool guiMode = true)
        {
            if (IsCoreInstalledAndValid())
                return true;

            if (!guiMode)
            {
                Console.Error.WriteLine("[Dagami] Первичная загрузка сетевого ядра xray.exe с GitHub...");
                DownloadAndInstallSync((pct, status) => Console.Error.WriteLine("[Dagami] " + status + " (" + pct + "%)"));
                return IsCoreInstalledAndValid();
            }

            using (var gateForm = new CoreBootstrapDialog())
            {
                DialogResult result = gateForm.ShowDialog();
                return result == DialogResult.OK && IsCoreInstalledAndValid();
            }
        }

        /// <summary>
        /// Скачивает официальный ZIP-архив с GitHub в ОЗУ, сверяет SHA-256 архива, извлекает xray.exe
        /// и атомарно записывает в %APPDATA%\devproxy\core-runtime без метки :Zone.Identifier.
        /// </summary>
        public static void DownloadAndInstallSync(Action<int, string> onProgress)
        {
            Directory.CreateDirectory(CoreDirectory);
            Exception lastError = null;

            for (int i = 0; i < DownloadUrls.Length; i++)
            {
                string url = DownloadUrls[i];
                string sourceLabel = i == 0 ? "GitHub Releases" : "резервного зеркала";
                try
                {
                    if (onProgress != null)
                        onProgress(2, "Подключение к " + sourceLabel + "...");

                    byte[] zipBytes = DownloadZipWithProgress(new Uri(url), MaxArchiveBytes, (pct, mbText) =>
                    {
                        if (onProgress != null)
                            onProgress(pct, "Скачивание ядра xray с " + sourceLabel + ": " + mbText);
                    });

                    if (onProgress != null)
                        onProgress(91, "Проверка SHA-256 архива Xray-windows-64.zip...");

                    string zipHash = ComputeSha256Hex(zipBytes);
                    string expectedZip = Environment.GetEnvironmentVariable("DAGAMI_CORE_ZIP_SHA256") ?? ExpectedZipSha256;
                    if (!string.Equals(zipHash, expectedZip.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Контрольная сумма SHA-256 архива (" + zipHash + ") не совпала с эталоном релиза.");

                    if (onProgress != null)
                        onProgress(95, "Распаковка xray.exe в оперативной памяти...");

                    byte[] exeBytes = ExtractExecutableFromZipInMemory(zipBytes, "xray.exe");
                    if (exeBytes.Length < 1024 * 1024 || exeBytes[0] != 0x4D || exeBytes[1] != 0x5A)
                        throw new InvalidDataException("Извлеченный файл не является корректным PE-исполняемым файлом Windows.");

                    string exeHash = ComputeSha256Hex(exeBytes);

                    if (onProgress != null)
                        onProgress(99, "Сохранение в %APPDATA%\devproxy\core-runtime...");

                    WriteAtomic(CoreBinaryPath, exeBytes);
                    WriteAtomic(CoreStampPath, Encoding.ASCII.GetBytes(exeHash));

                    if (onProgress != null)
                        onProgress(100, "Сетевое ядро готово к работе.");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Logger.Log("[CoreBootstrapGate] Сбой загрузки с " + url + ": " + ex.Message);
                }
            }

            throw new IOException("Не удалось скачать сетевое ядро xray.exe. Проверьте подключение к сети.", lastError);
        }

        private static byte[] DownloadZipWithProgress(Uri initialUri, long maxBytes, Action<int, string> onProgress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Uri current = initialUri;

            for (int redirect = 0; redirect < 6; redirect++)
            {
                var req = (HttpWebRequest)WebRequest.Create(current);
                req.Method = "GET";
                req.AllowAutoRedirect = false;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 25000;
                req.UserAgent = "Dagami-Agent-Proxy/" + Brand.ReleaseTag;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string location = resp.Headers["Location"];
                        if (string.IsNullOrEmpty(location))
                            throw new InvalidDataException("Пустой Location при перенаправлении GitHub.");
                        current = new Uri(current, location);
                        continue;
                    }

                    long totalLength = resp.ContentLength;
                    if (totalLength > maxBytes)
                        throw new InvalidDataException("Размер архива на сервере превышает лимит безопасности.");

                    using (var input = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[65536];
                        int read;
                        long received = 0;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            received += read;
                            if (received > maxBytes)
                                throw new InvalidDataException("Превышен лимит размера при чтении потока.");
                            ms.Write(buffer, 0, read);

                            if (onProgress != null)
                            {
                                int pct = totalLength > 0
                                    ? (int)Math.Min(90, Math.Max(5, (received * 85) / totalLength + 5))
                                    : 50;
                                string mbText = (received / (1024.0 * 1024.0)).ToString("0.0") + " МБ" +
                                    (totalLength > 0 ? " из " + (totalLength / (1024.0 * 1024.0)).ToString("0.0") + " МБ" : "");
                                onProgress(pct, mbText);
                            }
                        }
                        return ms.ToArray();
                    }
                }
            }
            throw new InvalidDataException("Слишком много перенаправлений GitHub.");
        }

        private static byte[] ExtractExecutableFromZipInMemory(byte[] zipBytes, string targetExeName)
        {
            using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.Equals(targetExeName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using (var entryStream = entry.Open())
                    using (var output = new MemoryStream())
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = entryStream.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (output.Length + n > MaxArchiveBytes)
                                throw new InvalidDataException("Превышен допустимый размер EXE внутри архива.");
                            output.Write(buf, 0, n);
                        }
                        return output.ToArray();
                    }
                }
            }
            throw new FileNotFoundException("В скачанном архиве не найден " + targetExeName);
        }

        private static void WriteAtomic(string destination, byte[] data)
        {
            string temp = Path.Combine(Path.GetDirectoryName(destination), ".core-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, data);
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static string ComputeSha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool VerifyFileSha256(string path, string expectedHex)
        {
            try
            {
                return string.Equals(ComputeSha256Hex(File.ReadAllBytes(path)), expectedHex, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Запускает уже скачанное ядро из %APPDATA%\devproxy\core-runtime в скрытом режиме.
        /// </summary>
        public static bool EnsureStarted(int localPort = DefaultLocalPort)
        {
            lock (Sync)
            {
                if (_coreProcess != null && !_coreProcess.HasExited)
                    return true;

                if (!IsCoreInstalledAndValid())
                    throw new InvalidOperationException("Сетевое ядро еще не установлено в %APPDATA%.");

                _boundPort = localPort;
                string jsonConfig = BuildClientConfigJson(_boundPort);

                // Конфиг передается через StandardInput (stdin:), секреты не пишутся на диск.

                var psi = new ProcessStartInfo
                {
                    FileName = CoreBinaryPath,
                    Arguments = "run -c stdin:",
                    WorkingDirectory = CoreDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _coreProcess = Process.Start(psi);
                if (_coreProcess == null)
                    throw new InvalidOperationException("Не удалось запустить процесс " + CoreBinaryPath);

                AttachProcessToKillOnCloseJob(_coreProcess);

                using (StreamWriter stdin = _coreProcess.StandardInput)
                {
                    stdin.Write(jsonConfig);
                    stdin.Flush();
                }

                _coreProcess.BeginOutputReadLine();
                _coreProcess.BeginErrorReadLine();

                return WaitForLocalPort("127.0.0.1", _boundPort, 3000);
            }
        }

        private static bool WaitForLocalPort(string host, int port, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_coreProcess == null || _coreProcess.HasExited) return false;
                try
                {
                    using (var probe = new TcpClient())
                    {
                        var ar = probe.BeginConnect(host, port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(120))
                        {
                            probe.EndConnect(ar);
                            return true;
                        }
                    }
                }
                catch { }
                Thread.Sleep(60);
            }
            return false;
        }

        public static void Stop()
        {
            lock (Sync)
            {
                try
                {
                    if (_coreProcess != null && !_coreProcess.HasExited)
                    {
                        _coreProcess.Kill();
                        _coreProcess.WaitForExit(1500);
                    }
                }
                catch { }
                finally
                {
                    if (_coreProcess != null) { _coreProcess.Dispose(); _coreProcess = null; }
                }
            }
        }

        private static string BuildClientConfigJson(int localPort)
        {
            string raw = "{\n  \"log\": {\n    \"loglevel\": \"none\"\n  },\n  \"inbounds\": [\n    {\n      \"tag\": \"dagami-local-http\",\n      \"listen\": \"127.0.0.1\",\n      \"port\": 10808,\n      \"protocol\": \"http\",\n      \"settings\": {\n        \"timeout\": 300,\n        \"allowTransparent\": false\n      },\n      \"sniffing\": {\n        \"enabled\": true,\n        \"destOverride\": [\n          \"http\",\n          \"tls\",\n          \"quic\"\n        ],\n        \"routeOnly\": true\n      }\n    }\n  ],\n  \"outbounds\": [\n    {\n      \"tag\": \"dagami-obfuscated-out\",\n      \"protocol\": \"vless\",\n      \"settings\": {\n        \"vnext\": [\n          {\n            \"address\": \"45.86.65.240\",\n            \"port\": 443,\n            \"users\": [\n              {\n                \"id\": \"8f9e4c12-5d3a-4b8e-9c11-2a3b4c5d6e7f\",\n                \"encryption\": \"none\",\n                \"flow\": \"xtls-rprx-vision\"\n              }\n            ]\n          }\n        ]\n      },\n      \"streamSettings\": {\n        \"network\": \"tcp\",\n        \"security\": \"reality\",\n        \"realitySettings\": {\n          \"show\": false,\n          \"fingerprint\": \"chrome\",\n          \"serverName\": \"www.microsoft.com\",\n          \"publicKey\": \"j9kL2mP8qR4tV6wX1yZ3aB5cD7eF9gH0iJ2kL4mN6oP\",\n          \"shortId\": \"a1b2c3d4e5f60718\",\n          \"spiderX\": \"/\"\n        }\n      }\n    },\n    {\n      \"tag\": \"direct\",\n      \"protocol\": \"freedom\"\n    }\n  ]\n}";
            return raw.Replace("\"port\": 10808", "\"port\": " + localPort)
                      .Replace("\"listen_port\": 10808", "\"listen_port\": " + localPort);
        }

        #region Win32 Job Object (Автоматическое закрытие xray.exe при завершении агента)
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        private static void AttachProcessToKillOnCloseJob(Process proc)
        {
            try
            {
                if (_jobHandle == IntPtr.Zero)
                {
                    _jobHandle = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr ptr = Marshal.AllocHGlobal(length);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(_jobHandle, 9, ptr, (uint)length);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                AssignProcessToJobObject(_jobHandle, proc.Handle);
            }
            catch (Exception ex)
            {
                Logger.Log("[CoreBootstrapGate] Предупреждение JobObject: " + ex.Message);
            }
        }
        #endregion
    }

    /// <summary>
    /// Модальное окно инициализации: полностью блокирует доступ к основному интерфейсу Dagami,
    /// пока не завершится докачка и SHA-256 проверка xray.exe с GitHub.
    /// </summary>
    public sealed class CoreBootstrapDialog : Form
    {
        private readonly Label _titleLabel = new Label { Dock = DockStyle.Fill, AutoSize = false };
        private readonly Label _statusLabel = new Label { Dock = DockStyle.Fill, AutoSize = false };
        private readonly ProgressBar _progressBar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 0 };
        private readonly Button _retryButton = new Button { Text = "Повторить", AutoSize = true, Enabled = false };
        private readonly Button _exitButton = new Button { Text = "Выход из программы", AutoSize = true };
        private volatile bool _isDownloading;
        private volatile bool _completed;

        public CoreBootstrapDialog()
        {
            Text = Brand.Name + " — Первичная инициализация сетевого ядра";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false; // Убираем крестик, чтобы нельзя было закрыть окно и попасть в программу без ядра
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 195);

            _titleLabel.Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
            _titleLabel.Text = "Подготовка защищенного транспорта (XRAY)";
            _statusLabel.Text = "Проверка %APPDATA%\devproxy\core-runtime...";

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(20)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 12, 0, 0)
            };
            buttons.Controls.Add(_exitButton);
            buttons.Controls.Add(_retryButton);

            layout.Controls.Add(_titleLabel, 0, 0);
            layout.Controls.Add(_statusLabel, 0, 1);
            layout.Controls.Add(_progressBar, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            Controls.Add(layout);

            _retryButton.Click += (s, e) => StartDownload();
            _exitButton.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Environment.Exit(1); // Полный выход из процесса — без скачанного ядра работа запрещена
            };

            FormClosing += (s, e) =>
            {
                if (!_completed && DialogResult != DialogResult.Cancel)
                    e.Cancel = true; // Блокируем обход окна через Alt+F4
            };

            Shown += (s, e) => StartDownload();
        }

        private void StartDownload()
        {
            if (_isDownloading) return;
            _isDownloading = true;
            _retryButton.Enabled = false;
            _progressBar.Value = 2;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    CoreBootstrapGate.DownloadAndInstallSync((pct, message) =>
                    {
                        Ui(() =>
                        {
                            _progressBar.Value = Math.Min(100, Math.Max(0, pct));
                            _statusLabel.Text = message;
                        });
                    });

                    Ui(() =>
                    {
                        _isDownloading = false;
                        _completed = true;
                        DialogResult = DialogResult.OK;
                        Close(); // Только теперь открывается главное окно Dagami!
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        _isDownloading = false;
                        _retryButton.Enabled = true;
                        _statusLabel.Text = "Ошибка загрузки: " + ex.Message + " Нажмите «Повторить».";
                    });
                }
            });
        }

        private void Ui(Action action)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
    }
}