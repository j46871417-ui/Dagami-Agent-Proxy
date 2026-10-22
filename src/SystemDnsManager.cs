using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DevProxyApp
{
    public sealed class SystemDnsAdapter {
        public int Index { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string CurrentServers { get; set; }
        public bool HasDefaultGateway { get; set; }
        public override string ToString() { return Name + " [" + Index + "] — DNS: " + CurrentServers; }
    }
    public sealed class SystemDnsRequest {
        public string JobId { get; set; }
        public string Operation { get; set; }
        public string Server { get; set; }
        public string Preset { get; set; }
        public bool ChangeAdapters { get; set; }
        public bool UseNrpt { get; set; }
        public bool ReplaceExisting { get; set; }
        public bool KeepExternalChanges { get; set; }
        public bool ReplaceActiveSetup { get; set; }
        public bool ConfirmResetAll { get; set; }
        public SystemDnsRequest() { KeepExternalChanges = true; ReplaceActiveSetup = true; }
        public SystemDnsAdapter[] Adapters { get; set; }
    }
    public static class SystemDnsManager
    {
        public static string StoreRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DevProxy-SystemDns"); } }
        public static List<SystemDnsAdapter> Adapters() {
            var result = new List<SystemDnsAdapter>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()) {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try {
                    var properties = adapter.GetIPProperties(); var ipv4 = properties.GetIPv4Properties(); Guid id;
                    if (ipv4 == null || !Guid.TryParse(adapter.Id, out id)) continue;
                    var servers = new List<string>(); foreach (var ip in properties.DnsAddresses) servers.Add(ip.ToString());
                    bool gateway = false;
                    foreach (var address in properties.GatewayAddresses) if (!address.Address.Equals(IPAddress.Any) && !address.Address.Equals(IPAddress.IPv6Any)) gateway = true;
                    result.Add(new SystemDnsAdapter { Index = ipv4.Index, Guid = id.ToString(), Name = adapter.Name, CurrentServers = string.Join(", ", servers.ToArray()), HasDefaultGateway = gateway });
                } catch (NetworkInformationException) { }
            }
            return result;
        }
        public static void Validate(SystemDnsRequest request) {
            Guid id;
            if (request == null || !Guid.TryParseExact(request.JobId, "N", out id) || (request.Operation != "Apply" && request.Operation != "Restore" && request.Operation != "ResetAll")) throw new ArgumentException("Некорректная операция DNS.");
            if (request.Operation == "ResetAll") { if (!request.ConfirmResetAll) throw new ArgumentException("Для полного сброса нужны два подтверждения."); return; }
            if (request.Operation == "Restore") return;
            IPAddress ip;
            if (!IPAddress.TryParse(request.Server, out ip)) throw new ArgumentException("Укажите IP-адрес DNS-сервера, например 185.149.145.6.");
            request.Server = ip.ToString();
            if (request.Preset != "Full" && request.Preset != "Development") throw new ArgumentException("Выберите набор доменов.");
            if (!request.ChangeAdapters && !request.UseNrpt) throw new ArgumentException("Выберите изменение DNS адаптеров или правила NRPT.");
            if (request.Adapters == null || request.Adapters.Length > 64 || (request.ChangeAdapters && request.Adapters.Length == 0)) throw new ArgumentException("Отметьте хотя бы один сетевой адаптер.");
            foreach (var adapter in request.Adapters) if (adapter.Index < 1 || !Guid.TryParse(adapter.Guid, out id)) throw new ArgumentException("Некорректный сетевой адаптер.");
        }
        public static string EncodeRequest(SystemDnsRequest request) {
            Validate(request); string json = new JavaScriptSerializer().Serialize(request);
            if (Encoding.UTF8.GetByteCount(json) > 12000) throw new ArgumentException("Слишком большой запрос настройки DNS.");
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }
        public static string Script() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DevProxy.SystemDns.ps1")) {
                if (stream == null) throw new InvalidOperationException("Встроенный модуль системного DNS отсутствует. Пересоберите DevProxy.");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
            }
        }
        public static string PowerShellCommand(string operation, string payload) {
            if (operation != "Apply" && operation != "Restore" && operation != "ResetAll" && operation != "Status") throw new ArgumentException("Invalid operation.");
            byte[] bytes = Convert.FromBase64String(payload);
            if (bytes.Length > 12000) throw new ArgumentException("Invalid payload size.");
            string canonical = Convert.ToBase64String(bytes);
            return "$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + canonical + "')); & {\n" + Script() + "\n} -Operation " + operation + " -RequestJson $json";
        }
        public static int RunElevatedHelper(string operation, string payload) {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            byte[] data = Convert.FromBase64String(payload);
            if (data.Length > 12000) throw new ArgumentException("Invalid payload size.");
            var request = new JavaScriptSerializer().Deserialize<SystemDnsRequest>(Encoding.UTF8.GetString(data)); Validate(request);
            if (operation != request.Operation) throw new ArgumentException("Operation mismatch.");
            data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(request));
            ProtectStore();
            string script = Script(), scriptPath = Path.Combine(StoreRoot, "engine-" + Guid.NewGuid().ToString("N") + ".ps1");
            try {
                File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
                var security = new FileSecurity(); AddStoreRules(security, false); File.SetAccessControl(scriptPath, security);
                if (File.ReadAllText(scriptPath) != script) throw new IOException("Embedded DNS module integrity check failed.");
                var psi = EngineStart(operation, Convert.ToBase64String(data), scriptPath);
                using (var process = Process.Start(psi)) {
                    var stdout = Task.Factory.StartNew(() => process.StandardOutput.ReadToEnd());
                    var stderr = Task.Factory.StartNew(() => process.StandardError.ReadToEnd());
                    process.WaitForExit(); Task.WaitAll(stdout, stderr);
                    if (process.ExitCode != 0) Logger.Log("[System DNS] Helper failed; exit=" + process.ExitCode + ". See DNS operation result.");
                    return process.ExitCode;
                }
            } finally { if (File.Exists(scriptPath)) File.Delete(scriptPath); }
        }
        public static ProcessStartInfo EngineStart(string operation, string payload, string scriptPath) {
            if (operation != "Apply" && operation != "Restore" && operation != "ResetAll") throw new ArgumentException("Invalid operation.");
            string canonical = Convert.ToBase64String(Convert.FromBase64String(payload));
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"));
            psi.Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File " + NativeShortcuts.Quote(scriptPath) + " -Operation " + operation + " -RequestBase64 " + canonical;
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(psi.FileName);
            psi.EnvironmentVariables["PSModulePath"] = Path.Combine(psi.WorkingDirectory, "Modules");
            return psi;
        }
        public static ProcessStartInfo HelperStart(SystemDnsRequest request, string assemblyPath, bool elevated) {
            string payload = EncodeRequest(request);
            // Reuse this GUI executable, including when the user renamed it.
            string helper = Path.GetFullPath(assemblyPath);
            var psi = new ProcessStartInfo(helper, "--system-dns-helper " + request.Operation + " " + payload);
            psi.WorkingDirectory = Path.GetDirectoryName(helper);
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.UseShellExecute = !elevated; psi.CreateNoWindow = elevated;
            if (!elevated) psi.Verb = "runas";
            return psi;
        }
        static void AddStoreRules(FileSystemSecurity security, bool directory) {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(admins); security.SetAccessRuleProtection(true, false);
            var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        static void ProtectStore() {
            if (Directory.Exists(StoreRoot)) {
                if ((File.GetAttributes(StoreRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid DNS backup folder.");
                string owner = Directory.GetAccessControl(StoreRoot).GetOwner(typeof(SecurityIdentifier)).Value;
                if (owner != "S-1-5-32-544" && owner != "S-1-5-18") throw new IOException("DNS backup folder is not administrator-owned.");
            }
            var security = new DirectorySecurity(); AddStoreRules(security, true);
            Directory.CreateDirectory(StoreRoot, security); Directory.SetAccessControl(StoreRoot, security);
            string jobs = Path.Combine(StoreRoot, "jobs");
            if (Directory.Exists(jobs) && ((File.GetAttributes(jobs) & FileAttributes.ReparsePoint) != 0 ||
                (Directory.GetAccessControl(jobs).GetOwner(typeof(SecurityIdentifier)).Value != "S-1-5-32-544" && Directory.GetAccessControl(jobs).GetOwner(typeof(SecurityIdentifier)).Value != "S-1-5-18")))
                throw new IOException("Invalid DNS job folder.");
            Directory.CreateDirectory(jobs, security); Directory.SetAccessControl(jobs, security);
        }
        public static string ReadStoredText(string path) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
        }
        public static string LastStatus() {
            string path = Path.Combine(StoreRoot, "state.json");
            if (!File.Exists(path)) return "DevProxy ещё не применял системный DNS. Текущие настройки показаны в списке адаптеров.";
            try {
                var state = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadStoredText(path));
                string value = Convert.ToString(state["Status"]);
                if (state.ContainsKey("Kind") && Convert.ToString(state["Kind"]) == "ResetAll") {
                    var resetLabels = new Dictionary<string,string> { {"Cleared","завершён"}, {"ClearFailed","выполнен частично — проверьте отчёт"}, {"Clearing","прерван — проверьте отчёт"}, {"Restored","прежние настройки возвращены"}, {"Released","возврат с пропусками"}, {"RollbackFailed","возврат не завершён — проверьте отчёт"} };
                    return "Полный сброс DNS и NRPT: " + (resetLabels.ContainsKey(value)?resetLabels[value]:value) + ". Копия: " + Convert.ToString(state["ResetDirectory"]) + ".\r\nДля возврата нажмите «Восстановить прежний DNS».";
                }
                var labels = new Dictionary<string, string> { { "Applied", "Применён" }, { "Restored", "Восстановлен прежний DNS" }, { "Released", "Откат завершён с пропусками; внешние настройки сохранены" }, { "Prepared", "Подготовлен; требуется проверка или откат" }, { "Applying", "Применение прервано; выполните откат" }, { "RollbackFailed", "Откат не завершён — проверьте отчёт" } };
                return "Последняя операция: " + (labels.ContainsKey(value) ? labels[value] : value) + ". DNS: " + Convert.ToString(state["Server"]) + ".\r\nЭто сохранённый результат операции; текущие DNS показаны в списке адаптеров.";
            } catch (Exception ex) { return "Не удалось прочитать резервную копию: " + ex.GetType().Name; }
        }
        public static string BriefStatus() {
            string path=Path.Combine(StoreRoot,"state.json");
            if(!File.Exists(path))return "не записывался приложением";
            try {
                var state=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(ReadStoredText(path));
                string status=Convert.ToString(state["Status"]);
                if(state.ContainsKey("Kind") && Convert.ToString(state["Kind"])=="ResetAll")return status=="Cleared"?"общий DNS сброшен; NRPT удалены":status=="Restored"?"прежние настройки до сброса возвращены":"полный сброс: нужна проверка";
                if(status=="Restored")return "прежние настройки восстановлены";
                if(status=="Released")return "откат с пропусками; внешние настройки сохранены";
                if(status!="Applied")return "нужна проверка: "+status;
                string server=Convert.ToString(state["Server"]);
                var originals=state["OriginalAdapters"] as System.Collections.IEnumerable;
                var adapters=Adapters();int verified=0;
                if(originals!=null)foreach(object item in originals) {
                    var record=item as Dictionary<string,object>;if(record==null)continue;
                    var adapter=adapters.Find(a=>a.Guid.Equals(Convert.ToString(record["Guid"]),StringComparison.OrdinalIgnoreCase));
                    if(adapter==null)return "адаптер недоступен — проверьте";
                    if(!MatchesServerFamily(adapter.CurrentServers,server))return "изменён вне приложения — проверьте";
                    verified++;
                }
                return verified>0?"записан "+(IPAddress.Parse(server).AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork?"IPv4: ":"IPv6: ")+server:"последняя операция: правила NRPT";
            } catch {return "не удалось проверить";}
        }
        public static bool MatchesServerFamily(string currentServers, string server) {
            IPAddress expected;
            if (!IPAddress.TryParse(server, out expected)) return false;
            var matching = new HashSet<string>();
            foreach (string value in currentServers.Split(new[]{", "},StringSplitOptions.RemoveEmptyEntries)) {
                IPAddress address;
                if (!IPAddress.TryParse(value,out address)) return false;
                if (address.AddressFamily==expected.AddressFamily) matching.Add(address.ToString());
            }
            return matching.Count==1 && matching.Contains(expected.ToString());
        }
        public static string Execute(SystemDnsRequest request) {
            bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            var psi = HelperStart(request, typeof(SystemDnsManager).Assembly.Location, elevated);
            if (!File.Exists(psi.FileName)) throw new FileNotFoundException("Не найден файл программы для запуска помощника DNS.");
            using (var process = Process.Start(psi)) {
                process.WaitForExit();
                string resultPath = Path.Combine(StoreRoot, "jobs", request.JobId + ".json");
                if (!File.Exists(resultPath)) throw new InvalidOperationException("Помощник DNS завершился без отчёта (код " + process.ExitCode + "). Настройка не подтверждена.");
                var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadStoredText(resultPath));
                string message = Convert.ToString(result["Message"]);
                if (process.ExitCode != 0 || !Convert.ToBoolean(result["Success"])) throw new InvalidOperationException(Friendly(message));
                return Friendly(message);
            }
        }
        public static string Friendly(string text) {
            if(text.Contains("DNS backup file is busy:"))return "Файл копии DNS занят другим процессом. Повтор записи не помог. Закройте другие окна DNS или дождитесь снятия блокировки и повторите действие. Подробности и итог отката:\r\n"+text;
            if(text.StartsWith("Pre-reset DNS settings restored."))return "Прежние настройки DNS и NRPT до полного сброса возвращены. Копия сохранена. Все пропуски указаны ниже.\r\n"+text;
            if(text.StartsWith("Full DNS reset completed."))return "Полный сброс выполнен: DNS IPv4/IPv6 всех найденных адаптеров переведён в авто, правила NRPT удалены, кеш DNS очищен.\r\n"+text;
            if(text.StartsWith("Full DNS reset incomplete:"))return "Сброс выполнен частично. Проверьте детали ниже; исходная копия сохранена. Политики вашей организации могут вернуть правила NRPT.\r\n"+text;
            if(text.StartsWith("DNS server preflight failed:"))return "Новый DNS не прошёл проверку доступности. Настройки сети не менялись.\r\n"+text;
            if(text.StartsWith("Connectivity preflight failed:"))return "Не удалось проверить исходный доступ к DNS и HTTPS. Настройки сети не менялись.\r\n"+text;
            if(text.StartsWith("Apply failed:") && text.Contains("Previous settings restored, but connectivity could not be verified:"))return "Применение отменено, прежние настройки возвращены. Доступ к сети после отката не подтверждён.\r\n"+text;
            if(text.StartsWith("Apply failed:") && text.Contains("Rollback incomplete:"))return "Применение не удалось, откат не завершён. Исходная копия сохранена; повторите восстановление прежнего DNS.\r\n"+text;
            if(text.StartsWith("Apply failed:") && text.EndsWith("Previous settings restored."))return "Применение отменено: проверка или запись не прошла. Прежние настройки возвращены, доступ к DNS и HTTPS проверен.\r\n"+text;
            if (text.StartsWith("DNS rollback completed with preserved external settings.")) return "Откат завершён с пропусками: внешние настройки сохранены, отсутствующие адаптеры пропущены. Можно применить DNS заново. Исходная копия сохранена.\r\n" + text;
            if (text.StartsWith("DNS applied.")) {
                string family=text.Contains("Family: IPv4.")?"IPv4":(text.Contains("Family: IPv6.")?"IPv6":null);
                return (family==null?"Системный DNS применён.":"DNS "+family+" применён. DNS "+(family=="IPv4"?"IPv6":"IPv4")+" сохранён без изменений.")+(text.Contains("DNS and HTTPS checks passed.")?" Проверки DNS и HTTPS пройдены.":"")+" Резервная копия сохранена.\r\n"+text;
            }
            if (text.StartsWith("Previous DNS settings restored")) return "Прежние DNS восстановлены вместе с режимом DHCP/статики. Кэш DNS очищен.";
            if (text.StartsWith("Previous DNS settings are already")) return "Прежние DNS уже восстановлены.";
            if (text.StartsWith("Conflicting NRPT rule")) return "Обнаружены конфликтующие правила NRPT. Для замены отметьте «Заменить конфликтующие NRPT-правила с резервной копией».\r\n" + text;
            if (text.StartsWith("A DNS backup is already")) return "Есть активная копия DNS. Включите замену прежней настройки в запросе либо выполните откат. В окне DNS замена уже включена.";
            if (text.StartsWith("There is no DevProxy")) return "Резервной копии системного DNS от DevProxy нет. Настройки другим скриптом не откатываются.";
            if (text.StartsWith("Adapter state changed")) return "Состав или состояние адаптеров изменились. Нажмите «Обновить» и выберите адаптеры заново.";
            if (text.Contains("DNS changed outside DevProxy:") || text.Contains("Adapter is missing:") || text.Contains("A foreign NRPT rule appeared during rollback:")) return "Откат выполнен частично. Исходная копия сохранена. В обычном режиме внешние DNS остаются как есть, а удалённые адаптеры пропускаются.\r\n" + text;
            return text;
        }
    }

    public sealed class SystemDnsForm : Form
    {
        readonly TextBox _server = new TextBox { Text = "185.149.145.6", Dock = DockStyle.Fill };
        readonly ComboBox _preset = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckedListBox _adapters = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        readonly CheckBox _changeAdapters = new CheckBox { Text = "Дополнительно: изменить DNS отмеченных адаптеров для всей сети", AutoSize = true, Checked = false };
        readonly CheckBox _nrpt = new CheckBox { Text = "Smart DNS: правила NRPT для выбранных доменов", AutoSize = true, Checked = true };
        readonly CheckBox _replace = new CheckBox { Text = "Заменить конфликтующие NRPT-правила с резервной копией", AutoSize = true };
        readonly TextBox _report = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
        readonly Button _apply = new FluentButton { Text = "Записать DNS в систему", AutoSize = true, Height = 34, Primary = true };
        readonly Button _restore = new FluentButton { Text = "Восстановить прежний DNS", AutoSize = true, Height = 34, Primary = true };
        readonly Button _refresh = new Button { Text = "Обновить", AutoSize = true, Height = 34 };
        readonly Button _resetAll = new Button { Text = "Очистить все DNS и NRPT…", AutoSize = true, Height = 34, BackColor = Color.FromArgb(185, 28, 28), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        bool _busy;
        bool _loading;
        public SystemDnsForm(string server = null) {
            if(!string.IsNullOrWhiteSpace(server))_server.Text=server.Trim();
            Icon = Brand.LoadIcon();
            Text = Brand.Name + " — запись DNS в Windows"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(820, 720); MinimumSize = new Size(720, 700);
            Font = new Font("Segoe UI", 10); BackColor = FluentTheme.Background;
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(18) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int size in new[] { 78, 66, 32, 32, 32, 110, 38, 54, 72 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Правила NRPT сохраняют DNS вашей сети и задают сервер для выбранных доменов.\r\nОбщий DNS адаптеров меняется лишь при выборе отдельного пункта ниже.\r\nПеред записью и после неё проверим сеть. При сбое — откат; для записи нужен UAC." }, 0, 0);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.Controls.Add(new Label { Text = "DNS-сервер (IP)", AutoSize = true }, 0, 0); fields.Controls.Add(_server, 1, 0);
            fields.Controls.Add(new Label { Text = "Домены NRPT", AutoSize = true }, 0, 1); fields.Controls.Add(_preset, 1, 1);
            _preset.Items.AddRange(new object[] { "Полный список + вход Google/Antigravity", "Разработка и ИИ (без игр и стриминга)" }); _preset.SelectedIndex = 1;
            body.Controls.Add(fields, 0, 1); body.Controls.Add(_changeAdapters, 0, 2); body.Controls.Add(_nrpt, 0, 3); body.Controls.Add(_replace, 0, 4); body.Controls.Add(_adapters, 0, 5);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            actions.Controls.AddRange(new Control[] { _apply, _restore, _refresh }); body.Controls.Add(actions, 0, 6);
            var danger = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            danger.Controls.Add(_resetAll); danger.Controls.Add(new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = "Все адаптеры и чужие правила.\r\nНужны два подтверждения.", Margin = new Padding(12, 3, 0, 0) }); body.Controls.Add(danger, 0, 7);
            body.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = FluentTheme.Muted, Text = "По умолчанию общий DNS сети сохраняется. Для смены DNS всей сети выберите отдельный пункт и нужные адаптеры.\r\nНовая запись сохранит прежнюю копию. При откате внешние DNS остаются как есть, удалённые адаптеры пропускаются. Итог — в отчёте ниже." }, 0, 8);
            body.Controls.Add(_report, 0, 9); Controls.Add(body);
            _apply.Click += (s, e) => Run("Apply"); _restore.Click += (s, e) => Run("Restore"); _refresh.Click += (s, e) => RefreshAdapters(null);
            _resetAll.Click += (s, e) => { if (!_busy && !_loading && ConfirmFullReset((text, title) => MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))) Run("ResetAll"); };
            Shown += (s, e) => RefreshAdapters(null);
            FormClosing += (s, e) => { if (_busy) { e.Cancel = true; _report.AppendText("\r\nДождитесь завершения операции и отчёта."); } };
        }
        public static bool ConfirmFullReset(Func<string, string, DialogResult> show) {
            const string question = "Вы точно уверены в том что вы делаете?";
            if (show(question + "\r\n\r\nБудут сброшены DNS IPv4/IPv6 ВСЕХ найденных адаптеров, включая VPN и VMware. Все правила NRPT, в том числе чужие и локальные правила политики, будут удалены. Доступ к сети и рабочим ресурсам может пропасть.", "Полный сброс — шаг 1 из 2") != DialogResult.Yes) return false;
            return show(question + "\r\n\r\nDNS вернётся в авто. Списки и галочки выше не ограничивают сброс. Сначала сохраним копию в ProgramData\\DevProxy-SystemDns. Правила из домена могут появиться вновь. Нажатие «Да» запустит сброс после UAC.", "Полный сброс — шаг 2 из 2") == DialogResult.Yes;
        }
        void RefreshAdapters(string operationMessage) {
            if (_busy || _loading) return;
            bool first = _adapters.Items.Count == 0;
            var selected = new HashSet<string>(); foreach (SystemDnsAdapter adapter in _adapters.CheckedItems) selected.Add(adapter.Guid);
            _loading = true; _apply.Enabled = _restore.Enabled = _refresh.Enabled = _resetAll.Enabled = false;
            string requestedServer=_server.Text.Trim();
            _report.Text = (operationMessage == null ? "" : operationMessage + "\r\n\r\n") + "Чтение сетевых адаптеров…";
            ThreadPool.QueueUserWorkItem(o => {
                List<SystemDnsAdapter> adapters = null; string error = null; string resolvedServer=null;
                try {
                    adapters = SystemDnsManager.Adapters(); IPAddress ip;
                    if(IPAddress.TryParse(requestedServer,out ip))resolvedServer=ip.ToString();
                    else {ip=Array.Find(Dns.GetHostAddresses(requestedServer),a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork);if(ip==null)throw new InvalidOperationException("Не найден IPv4 DNS-сервера. Введите его IP вручную.");resolvedServer=ip.ToString();}
                } catch (Exception ex) { error = ex.Message; }
                try { BeginInvoke(new Action(() => {
                    _loading = false; _apply.Enabled = _restore.Enabled = _refresh.Enabled = _resetAll.Enabled = true;
                    if(resolvedServer!=null && _server.Text.Trim()==requestedServer)_server.Text=resolvedServer;
                    _adapters.Items.Clear(); if (adapters != null) foreach (var adapter in adapters) _adapters.Items.Add(adapter, first ? adapter.HasDefaultGateway : selected.Contains(adapter.Guid));
                    _report.Text = SystemDnsManager.LastStatus() + (error == null ? "" : "\r\nОшибка чтения адаптеров: " + error) + (operationMessage == null ? "" : "\r\n\r\n" + operationMessage);
                })); } catch (InvalidOperationException) { }
            });
        }
        SystemDnsRequest CreateRequest(string operation) {
            var selected = new List<SystemDnsAdapter>(); foreach (SystemDnsAdapter adapter in _adapters.CheckedItems) selected.Add(adapter);
            return new SystemDnsRequest { JobId = Guid.NewGuid().ToString("N"), Operation = operation, Server = _server.Text.Trim(),
                Preset = _preset.SelectedIndex == 0 ? "Full" : "Development", ChangeAdapters = _changeAdapters.Checked, UseNrpt = _nrpt.Checked, ReplaceExisting = _replace.Checked, KeepExternalChanges = true, ReplaceActiveSetup = true, ConfirmResetAll = operation == "ResetAll", Adapters = selected.ToArray() };
        }
        void Run(string operation) {
            if (_busy || _loading) return;
            var request = CreateRequest(operation);
            try { SystemDnsManager.Validate(request); } catch (Exception ex) { _report.Text = ex.Message; return; }
            _busy = true; _apply.Enabled = _restore.Enabled = _refresh.Enabled = _resetAll.Enabled = false;
            _report.Text = operation == "ResetAll" ? "Ожидание UAC. Сохраним копию, затем сбросим DNS всех найденных адаптеров и удалим NRPT, включая чужие правила." : "Ожидание UAC. Перед записью проверим DNS-сервер, после — DNS и HTTPS. При сбое вернём прежние настройки.\r\nСервер: " + request.Server + "; выбранных адаптеров: " + request.Adapters.Length;
            ThreadPool.QueueUserWorkItem(o => {
                string message;
                try { message = SystemDnsManager.Execute(request); }
                catch (Win32Exception ex) { message = ex.NativeErrorCode == 1223 ? "Операция отменена в окне UAC. Изменения этим запросом не выполнялись." : ex.Message; }
                catch (Exception ex) { message = "Ошибка: " + ex.Message; }
                try { BeginInvoke(new Action(() => { _busy = false; RefreshAdapters(message); Logger.Log("[System DNS] " + message); })); } catch (InvalidOperationException) { }
            });
        }
    }
}
