using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DevProxyApp
{
    public partial class MainForm
    {
        private TextBox _proxyBox;
        private TextBox _dnsBox;
        private CheckBox _dnsEnabled;
        private CheckBox _prepareAntigravity;
        private CheckBox _blockAntigravityUpdates;
        private bool _updatingBlockUpdates;
        private ListView _targetList;
        private TextBox _nativeLog;
        private Label _nativeStatus;
        private Label _targetDetails;
        private LinkLabel _appRouteStatus, _appModelStatus;
        private ToolTip _appStatusTip;
        private AppRouteCheck _appRouteCheck;
        private NativeSession _modelAnswerSession;
        private LocalProxyBridge _modelAnswerBridge;
        private System.Windows.Forms.Timer _sessionRefreshTimer;
        private volatile bool _showNativeLog;
        private bool _logUpdateQueued;

        private void FlushNativeLog()
        {
            var batch = new System.Text.StringBuilder();
            lock (_logLock) {
                _logUpdateQueued = false;
                if (!_showNativeLog || !Visible || _logQueue.Count == 0) return;
                foreach (string message in _logQueue)
                    batch.Append(DateTime.Now.ToString("HH:mm:ss")).Append(' ').AppendLine(message);
                _logQueue.Clear();
            }
            if (_nativeLog.TextLength + batch.Length > 100000) _nativeLog.Clear();
            _nativeLog.AppendText(batch.ToString());
        }
        private bool _refreshingTargets;
        private bool _nativeBusy;
        private bool _nativeColumnsPending;
        private FormWindowState _nativeRestoreState = FormWindowState.Normal;
        private readonly List<Button> _nativeButtons = new List<Button>();

        private FluentButton _primaryAppAction;
        private FluentButton _checkAppAction;
        private FluentButton _patchAppAction, _unpatchAppAction;
        private bool _patchInProgress;
        private Label _selectedAppHeading;
        private Label _shortcutExplanation;
        private Label _appFeedback;
        private Label _shortcutState;
        private Label _clientPreparationStatus;
        private ContextMenuStrip _appActions;
        private ToolStripMenuItem _stopAppItem, _removeAppItem;
        private Panel[] _pageControls;
        private int _activeFluentPage;
        private string[] _pageNames, _pageDescriptions;
        private CheckBox _startWithWindows;
        private CheckBox _useProxyCheckBox;
        private bool _updatingRouteFlags;

        internal void SyncRouteCheckboxes()
        {
            _updatingRouteFlags = true;
            try {
                bool useProxy = ConfigManager.Instance.UseProxy;
                bool useDns = ConfigManager.Instance.UseDagamiDns;
                if (_useProxyCheckBox != null) _useProxyCheckBox.Checked = useProxy;
                if (_dnsEnabled != null && _dnsEnabled.Checked != useDns) {
                    _changingDnsPreference = true;
                    _dnsEnabled.Checked = useDns;
                    _changingDnsPreference = false;
                }
            } finally {
                _updatingRouteFlags = false;
            }
        }

        internal void ApplyRouteFlagsChange(bool useProxy, bool useDns)
        {
            if (_updatingRouteFlags || _preview) return;
            if (useProxy && useDns && ManagedAccess.IsProfile(ConfigManager.Instance.LastProxy)) {
                SyncRouteCheckboxes();
                MessageBox.Show(this,"Профиль Dagami через Telegram использует маршрут шлюза.","Dagami",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }
            try {
                if ((useProxy != ConfigManager.Instance.UseProxy || useDns != ConfigManager.Instance.UseDagamiDns) && !EnsureSessionsStoppedForRouteChange()) {
                    SyncRouteCheckboxes(); return;
                }
                ConfigManager.Instance.ChangeConnection(cfg => {
                    cfg.UseProxy = useProxy;
                    cfg.UseDagamiDns = useDns;
                });
                SyncRouteCheckboxes();
                RestartRescueBridge();
                UpdateNativeStatus();
                if (_appFeedback != null) {
                    string mode = useProxy && useDns ? "Smart DNS для поддерживаемых доменов, прокси для остальных; прямого обхода прокси при сбое нет" :
                                  useProxy ? "только через прокси" :
                                  useDns ? "только через Smart DNS" : "прокси и Smart DNS выключены (прямое соединение)";
                    _appFeedback.Text = "Режим работы переключён: " + mode + ".";
                }
            } catch (Exception error) {
                SyncRouteCheckboxes();
                MessageBox.Show(this, error.Message, "Режим работы", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        internal static bool IsTargetPatched(AppTarget target)
        {
            return TargetPatchState(target) == "ready";
        }

        internal static string TargetPatchState(AppTarget target)
        {
            if (target == null) return "none";
            try {
                string issue;
                int count = NativeCliApps.IsCommandTarget(target)
                    ? NativeCliApps.Current.VerifiedCount(target.Id, out issue)
                    : NativeShortcuts.Current.VerifiedCount(target.Id, out issue);
                if (!string.IsNullOrEmpty(issue)) return "damaged";
                if (count > 0) return "ready";
            } catch { return "damaged"; }
            try { return HasPersistentAppChanges(target) ? "ready" : "none"; }
            catch { return "damaged"; }
        }

        public static string PatchStatusLabel(int records, string issue, bool persistent)
        {
            return !string.IsNullOrEmpty(issue) ? "Проверить запуск" :
                records > 0 || persistent ? "Пропатчено" : "Не пропатчено";
        }

        private static bool HasPersistentAppChanges(AppTarget target)
        {
            if (ApplicationSupport.SupportsSettings(target)) {
                ApplicationSupport.ResolveSettings(target);
                if (!string.IsNullOrEmpty(target.ConfigPath) && File.Exists(target.ConfigPath + ".devproxy.bak"))
                    return true;
            }
            if (AntigravityIntegration.Supports(target.ExePath)) {
                foreach (string backend in AntigravityIntegration.BackendPaths(target.ExePath)) {
                    if (AntigravityIntegration.HasActivePatchRecord(backend)) return true;
                }
            }
            return false;
        }

        internal void ReadStartupState()
        {
            if (_preview || _updatingStartup || _startWithWindows == null) return;
            _updatingStartup = true;
            _startWithWindows.Enabled = false;
            _startupStatus.Text = "Проверяем автозапуск…";
            System.Threading.ThreadPool.QueueUserWorkItem(_ => {
                bool enabled = false;
                string status;
                try {
                    var startup = NativeStartup.Current;
                    enabled = startup.IsEnabled();
                    status = enabled ? "Автозапуск включён." : startup.HasLegacyElevatedTask()
                        ? "Чтобы обновить прежний автозапуск, включите его заново." : "Автозапуск выключен.";
                } catch (Exception error) { status = "Не удалось проверить автозапуск: " + error.GetType().Name; }
                try { BeginInvoke(new Action(() => {
                    _startWithWindows.Checked = enabled;
                    _startupStatus.Text = status;
                    _startWithWindows.Enabled = true;
                    _updatingStartup = false;
                })); } catch (InvalidOperationException) { }
            });
        }

        internal void ApplyStartupCheckboxChange(bool enabled)
        {
            if (_updatingStartup || _preview) return;
            try {
                NativeStartup.Current.SetEnabled(enabled);
                if (_appFeedback != null)
                    _appFeedback.Text = enabled ? "Автозапуск самого Dagami Agent Proxy с Windows включён (в трее)." : "Автозапуск самого Dagami Agent Proxy с Windows выключен.";
            } catch (Exception error) {
                MessageBox.Show(this, error.Message, "Автозапуск", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            ReadStartupState();
        }
        private Label _startupStatus;
        private bool _updatingStartup;
        private string _shortcutIssue;
        private int _shortcutRecords;
        private string _clientStatusKey, _clientStatusText;
        private int _clientStatusRevision;
        private bool _selectedPersistentPatch;
        private bool _activationKeySaved;
        private readonly object _clientStatusLock = new object();
        private string _pendingClientPath;
        private int _pendingClientRevision;
        private bool _clientStatusWorkerRunning;

        private void QueueClientStatus(bool force=false) {
            var target=_targetList==null||_targetList.SelectedItems.Count==0?null:_targetList.SelectedItems[0].Tag as AppTarget;
            string key=target!=null&&target.IsInstalled&&AntigravityIntegration.Supports(target.ExePath)?target.Id+"|"+target.ExePath:null;
            if(!force&&key==_clientStatusKey)return;
            _clientStatusKey=key;int revision=++_clientStatusRevision;
            _clientStatusText=key==null?null:_preview?"Патч: не проверен":"Патч: проверяем…";
            if(key==null||_preview)return;
            lock(_clientStatusLock) {
                _pendingClientPath=target.ExePath;_pendingClientRevision=revision;
                if(_clientStatusWorkerRunning)return;
                _clientStatusWorkerRunning=true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(state=>{
                while(true) {
                    string path;int current;
                    lock(_clientStatusLock){path=_pendingClientPath;current=_pendingClientRevision;_pendingClientPath=null;if(path==null){_clientStatusWorkerRunning=false;return;}}
                    string status=AntigravityIntegration.ClientPreparationStatus(path);
                    try{BeginInvoke(new Action(()=>{if(current!=_clientStatusRevision)return;_clientStatusText=status;UpdateAppActionState();}));}catch(InvalidOperationException){}
                }
            });
        }

        private void ShowFluentPage(int index)
        {
            if (_pageControls == null || index == _activeFluentPage) return;
            int previous = _activeFluentPage;
            _activeFluentPage = index;
            _fluentPages.SuspendLayout();
            try {
                _pageControls[previous].Hide();
                _pageControls[index].Show();
                _pageControls[index].BringToFront();
            } finally {
                _fluentPages.ResumeLayout(true);
            }
            _navigation[previous].NavigationSelected = false;
            _navigation[index].NavigationSelected = true;
            _navigation[previous].Invalidate();
            _navigation[index].Invalidate();
            _fluentHeading.Text = _pageNames[index]; _fluentSubtitle.Text = _pageDescriptions[index];
            _showNativeLog = index == 3;
            if (_showNativeLog) FlushNativeLog();
            if (index == 0) UpdateNativeDetails();
            if (index == 2) _nextDnsUiRead = DateTime.MinValue;
            UpdateNativeStatus();

        }

        private void ExecuteNativeAction(Action action)
        {
            if (_nativeBusy) return;
            try { action(); }
            catch (Exception ex) {
                Logger.Log("[UI] " + (action == null ? "<no action>" : action.Method.Name) + ": " + BugReports.Clean(ex.ToString()));
                if (_appFeedback != null) _appFeedback.Text = ex.Message;
                MessageBox.Show(this, ex.Message, "Dagami Agent Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private ToolStripMenuItem AppMenuItem(string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (sender, e) => ExecuteNativeAction(action);
            return item;
        }

        private void BuildAppContextMenu()
        {
            _appActions = new ContextMenuStrip();
            _stopAppItem = AppMenuItem("Остановить", () => {
                if (MessageBox.Show(this, "Завершить «" + SelectedNativeTarget().DisplayName + "»? Сохраните работу в приложении перед остановкой.",
                    "Остановить приложение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                StopSelectedNativeSession(); _appFeedback.Text = "Приложение остановлено.";
            });
            _appActions.Items.Add(_stopAppItem);
            _removeAppItem = AppMenuItem("Удалить из списка", () => {
                if (_unpatchAppAction.Enabled) throw new InvalidOperationException("Сначала нажмите «Отменить патч» в карточке приложения.");
                NativeSession session; if (_nativeSessions.TryGetValue(SelectedNativeTarget().Id, out session) && session.RunningProcess != null)
                    throw new InvalidOperationException("Сначала остановите приложение.");
                if (MessageBox.Show(this, "Удалить «" + SelectedNativeTarget().DisplayName + "» из списка Dagami Agent Proxy? Само приложение останется на компьютере.",
                    "Удалить из списка", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) RemoveNativeTarget();
            });
            _appActions.Items.Add(_removeAppItem);
            _appActions.Opening += (sender, e) => {
                e.Cancel = _nativeBusy || _targetList.SelectedItems.Count == 0 || (!_stopAppItem.Enabled && !_removeAppItem.Enabled);
            };
            _targetList.ContextMenuStrip = _appActions;
            _targetList.MouseDown += (sender, e) => {
                if (e.Button != MouseButtons.Right) return;
                var row = _targetList.GetItemAt(e.X, e.Y);
                if (row != null) { row.Selected = true; row.Focused = true; }
                else if (_targetList.SelectedItems.Count > 0) _targetList.SelectedItems[0].Selected = false;
            };
            Disposed += (sender, e) => _appActions.Dispose();
        }

        private bool ConfirmSettingsRestore() {
            return MessageBox.Show(this, "Отменить патч для «" + SelectedNativeTarget().DisplayName +
                "» и вернуть исходные файлы, настройки и запуск? Ручные изменения управляемых прокси-полей будут заменены.",
                "Отменить патч", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        internal static bool IsDirectProxyValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            return v.Equals("direct", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("прямой", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("прямое", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasConfiguredConnection(string proxyValue, bool smartDnsEnabled, bool useProxy = true)
        {
            Uri parsed;
            if (useProxy && ManagedAccess.IsProfile(proxyValue)) return ManagedAccess.HasActiveDevice();
            if (useProxy && (TryParseSupportedProxy(proxyValue, out parsed) || IsDirectProxyValue(proxyValue))) return true;
            if (smartDnsEnabled) return true;
            return false;
        }

        private void PatchSelectedNativeTarget()
        {
            if (!ConfigManager.Instance.UseProxy && !ConfigManager.Instance.UseDagamiDns) { MessageBox.Show(this, "Включите хотя бы один режим работы: «Работать через прокси» или «Работать через DNS» (или оба одновременно).", "Режим подключения", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!HasConfiguredConnection(ConfigManager.Instance.LastProxy, ConfigManager.Instance.UseDagamiDns, ConfigManager.Instance.UseProxy)) { ShowFluentPage(1); return; }
            var target = SelectedNativeTarget(); if (!EnsureSessionStoppedForPatch(target, "Настройка обычного запуска (патч)")) return; if (!SaveNativeSettings()) return;
            string manager = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            _nativeBusy = true; _patchInProgress = true; SetNativeButtonsEnabled(false); _patchAppAction.Text = "Подготовка…";
            _appFeedback.Text = "Подготовка клиента, конфигурации и ярлыков…";
            string ideProxy = GetIdeProxyUrl();
            var worker = new System.Threading.Thread(() => {
                string result; bool success = false;
                try {
                    string summary;
                    var paths = NativeAppPatcher.Apply(target, manager, NativeShortcuts.UserFolders(), NativeShortcuts.Current, ideProxy, out summary, true);
                    result = summary;
                    Logger.Log("[Patch] " + target.DisplayName + ": " + result); success = true;
                } catch (Exception ex) { result = ex.Message; Logger.Log("[Patch] " + ex.GetType().Name + ": " + result); }
                try { BeginInvoke(new Action(() => {
                    _nativeBusy = false; _patchInProgress = false; _patchAppAction.Text = "Пропатчить";
                    SetNativeButtonsEnabled(true); UpdateNativeDetails(); QueueClientStatus(true);
                    if (success && !_preview && !NativeStartup.Current.IsEnabled()) { try { NativeStartup.Current.SetEnabled(true); ReadStartupState(); } catch { } }
                    _appFeedback.Text = success?"Настройка завершена — подробности в отчёте.":result;
                    MessageBox.Show(this, result+(success?"\r\n\r\nВ дальнейшем запускайте приложение обычным настроенным ярлыком или командой. Dagami запустится в фоне автоматически; для запуска без запросов UAC можно включить автозапуск в настройках.":""), success?"Настройка обычного запуска":"Настройка не завершена",MessageBoxButtons.OK,success?MessageBoxIcon.Information:MessageBoxIcon.Warning);
                })); } catch (InvalidOperationException) { }
            });
            worker.IsBackground = true; worker.SetApartmentState(System.Threading.ApartmentState.STA); worker.Start();
        }

        private bool EnsureSessionStoppedForPatch(AppTarget target, string actionTitle)
        {
            NativeSession session;
            bool active=_nativeSessions.TryGetValue(target.Id,out session)&&session.RunningProcess!=null;
            string[] paths=ApplicationSupport.RelatedExecutables(target);bool external=false;
            foreach(string path in paths)try{NativeLogin.RequireStopped(path);}catch(InvalidOperationException){external=true;break;}
            if(!active&&!external)return true;
            if(MessageBox.Show(this,"Для «"+target.DisplayName+"» работают процессы приложения или его backend. Общий agy может использоваться другой IDE/CLI.\r\n\r\nПринудительно закрыть их и продолжить? Несохранённые данные могут быть потеряны.",actionTitle,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return false;
            if(active)StopNativeSession(target.Id);
            NativeLogin.StopExactExecutables(paths);
            return true;
        }

        private void UnpatchSelectedNativeTarget()
        {
            var target = SelectedNativeTarget();
            if (!ConfirmSettingsRestore()) return;
            if (!EnsureSessionStoppedForPatch(target, "Отмена патча")) return;
            _nativeBusy = true; _patchInProgress = true; SetNativeButtonsEnabled(false);
            _appFeedback.Text = "Восстановление исходных файлов, настроек и ярлыков…";
            var worker = new System.Threading.Thread(() => {
                var summary = new System.Text.StringBuilder();
                bool success = false;
                try {
                    var restoredLinks = NativeAppPatcher.RestoreWithLauncher(target, () => NativeCliApps.IsCommandTarget(target) ? NativeCliApps.Current.Restore(target.Id) : NativeShortcuts.Current.Restore(target.Id));
                    summary.AppendLine("Восстановлено файлов запуска/ярлыков: " + restoredLinks.Count + ".");
                    if (target.IsInstalled && AntigravityIntegration.Supports(target.ExePath)) summary.AppendLine("Исходный backend восстановлен или уже был исходным.");
                    Logger.Log("[Unpatch] " + target.DisplayName + ": " + summary.ToString().Trim());
                    success = true;
                } catch (Exception ex) {
                    summary.Clear();
                    summary.Append(ex.Message);
                    Logger.Log("[Unpatch] " + ex.GetType().Name + ": " + ex.Message);
                }
                string finalMsg = summary.ToString().Trim();
                try { BeginInvoke(new Action(() => {
                    _nativeBusy = false; _patchInProgress = false;
                    SetNativeButtonsEnabled(true); UpdateNativeDetails(); QueueClientStatus(true);
                    _appFeedback.Text = success ? "Патч отменён, исходные параметры возвращены." : finalMsg;
                    MessageBox.Show(this, string.IsNullOrEmpty(finalMsg) ? "Исходные настройки и ярлыки восстановлены." : finalMsg,
                        success ? "Отмена патча завершена" : "Ошибка отмены патча",
                        MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                })); } catch (InvalidOperationException) { }
            });
            worker.IsBackground = true; worker.SetApartmentState(System.Threading.ApartmentState.STA); worker.Start();
        }

        private void RunPrimaryNativeAction()
        {
            if (_preview) return;
            var target = SelectedNativeTarget(); NativeSession session;
            if (_nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null) {
                FocusSession(session.RunningProcess); return;
            }
            if (!target.IsInstalled) return;
            var config = ConfigManager.Instance;
            if (!string.IsNullOrEmpty(config.LastLoadError) || !HasConfiguredConnection(config.LastProxy, config.UseDagamiDns, config.UseProxy)) {
                if (!CanConnectSelectedApp(config)) { ShowFluentPage(1); return; }
                var activation = OpenManagedKey();
                if (activation == DialogResult.Retry) {
                    _appFeedback.Text = "Ключ сохранён. Повторите «Запустить через Dagami», когда шлюз станет доступен.";
                    return;
                }
                if (activation != DialogResult.OK || !HasConfiguredConnection(config.LastProxy, config.UseDagamiDns, config.UseProxy)) return;
            }
            // Diagnostics are advisory. Launch uses saved parameters and must not
            // depend on a stale probe result or a writable settings directory.
            _nativeBusy = true; SetNativeButtonsEnabled(false);
            _primaryAppAction.Text = "Запуск…"; _primaryAppAction.Refresh();
            try {
                string error;
                if (!LaunchTarget(target, out error)) {
                    MessageBox.Show(this, error ?? "Не удалось запустить приложение.", "Запуск приложения", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _appFeedback.Text = error ?? "Не удалось запустить приложение.";
                    return;
                }
                string notice;
                _appFeedback.Text = _nativeClientNotices.TryGetValue(target.Id,out notice) ? "Процесс запущен. "+notice : "Процесс запущен. Доступ к сервису можно проверить на вкладке «Диагностика».";
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _appFeedback.Text = ex.Message;
            } finally { _nativeBusy = false; SetNativeButtonsEnabled(true); QueueClientStatus(true); UpdateNativeStatus(); }
        }

        private static bool CanConnectSelectedApp(ConfigManager config)
        {
            return string.IsNullOrEmpty(config.LastLoadError) && config.UseProxy && ManagedAccess.IsProfile(config.LastProxy);
        }

        private sealed class AppRouteCheck {
            public AppTarget Target;
            public LocalProxyBridge Bridge;
            public ProxyProbeResult Result;
        }

        private static string AppDiagnosticService(AppTarget target)
        {
            if (target == null) return null;
            if (AntigravityIntegration.Supports(target.ExePath)) return "Antigravity";
            if (AppProxyPolicy.IsGemini(target)) return "Gemini";
            if (target.Id == "codex" || target.Id == "codex-gui") return "Codex";
            if (target.Id == "cursor") return "Cursor";
            if (target.Id == "claude-gui" || target.Id == "claude_cli") return "Claude";
            return null;
        }

        private LocalProxyBridge AppDiagnosticBridge(NativeSession session)
        {
            return session != null && session.PrivateBridge != null ? session.PrivateBridge : _rescueBridge;
        }

        private async void CheckSelectedAppRoute()
        {
            if (_preview || _appRouteCheck != null && _appRouteCheck.Result == null) return;
            var target = SelectedNativeTarget();
            string service = AppDiagnosticService(target);
            if (service == null) return;
            NativeSession session;
            _nativeSessions.TryGetValue(target.Id, out session);
            if (session != null && session.RunningProcess == null) session = null;
            var bridge = AppDiagnosticBridge(session);
            if (bridge == null || bridge.BoundPort <= 0) return;

            var check = new AppRouteCheck { Target = target, Bridge = bridge };
            int port = bridge.BoundPort;
            _appRouteCheck = check;
            UpdateAppActionState();
            check.Result = await System.Threading.Tasks.Task.Run(() => ConnectionDiagnostics.CheckService(port, service));
            if (!IsDisposed && !Disposing) UpdateAppActionState();
        }

        private void ConfirmSelectedAppModel()
        {
            if (_preview) return;
            var target = SelectedNativeTarget();
            NativeSession session;
            if (AppDiagnosticService(target) == null || !_nativeSessions.TryGetValue(target.Id, out session) ||
                session.RunningProcess == null) return;
            var bridge = AppDiagnosticBridge(session);
            if (MessageBox.Show(this, "Отправьте тестовый запрос в «" + target.DisplayName + "». Модель прислала ответ?",
                "Подтверждение ответа", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            NativeSession current;
            if (!_nativeSessions.TryGetValue(target.Id, out current) || current != session || current.RunningProcess == null ||
                AppDiagnosticBridge(current) != bridge) return;
            _modelAnswerSession = session;
            _modelAnswerBridge = bridge;
            UpdateAppActionState();
        }

        private void UpdateAppRunStatus(AppTarget target, NativeSession runningSession)
        {
            if (_appRouteStatus == null) return;
            var bridge = AppDiagnosticBridge(runningSession);
            string service = AppDiagnosticService(target);
            bool probing = _appRouteCheck != null && _appRouteCheck.Result == null;
            var matching = _appRouteCheck != null && _appRouteCheck.Target == target && _appRouteCheck.Bridge == bridge
                ? _appRouteCheck : null;
            var result = matching == null ? null : matching.Result;
            string route = matching != null && probing ? "Маршрут: проверяем…" : result != null
                ? (result.Available ? "Маршрут: адрес ответил" : "Маршрут: проверка не прошла") : "Маршрут: проверить";
            bool routeAction = service != null && bridge != null && bridge.BoundPort > 0 && !probing;
            if (service == null) route = "Маршрут: нет теста для приложения";
            else if (bridge == null || bridge.BoundPort <= 0) route = "Маршрут: мост остановлен";
            else if (probing && matching == null) route = "Маршрут: дождитесь проверки";
            SetAppStatusLink(_appRouteStatus, route, routeAction);
            string details = result == null ? "Проверяется тестовый адрес сервиса через локальный мост." : result.Message;
            if (_appStatusTip.GetToolTip(_appRouteStatus) != details) _appStatusTip.SetToolTip(_appRouteStatus, details);

            bool modelAction = service != null && runningSession != null;
            bool confirmed = modelAction && _modelAnswerSession == runningSession && _modelAnswerBridge == bridge;
            string model = confirmed ? "Модель: ответ подтверждён вами" : modelAction ? "Модель: подтвердить ответ" : "Модель: ответ не проверен";
            SetAppStatusLink(_appModelStatus, model, modelAction && !confirmed);
        }

        private static void SetAppStatusLink(LinkLabel label, string text, bool clickable)
        {
            if (label.Text != text) label.Text = text;
            int length = clickable ? text.Length : 0;
            if (label.LinkArea.Length != length) label.LinkArea = new LinkArea(0, length);
            label.TabStop = clickable;
        }

        private async void CheckSelectedApp()
        {
            if (_preview) return;
            var target = SelectedNativeTarget();
            var config = ConfigManager.Instance;
            bool configured = string.IsNullOrEmpty(config.LastLoadError) &&
                HasConfiguredConnection(config.LastProxy, config.UseDagamiDns, config.UseProxy);
            _nativeBusy = true;
            SetNativeButtonsEnabled(false);
            _appFeedback.Text = "Проверяем запуск приложения…";
            Action selectedAction = null;
            try {
                var check = await System.Threading.Tasks.Task.Run(() => CheckAppFiles(target));
                if (IsDisposed || Disposing) return;
                if (_targetList.SelectedItems.Count == 0 || !ReferenceEquals(_targetList.SelectedItems[0].Tag, target)) {
                    _appFeedback.Text = "Выберите приложение и повторите проверку.";
                    return;
                }
                string connection = !configured ? "Подключение нужно настроить." : _bridgePort > 0
                    ? "Локальный мост запущен." : "Локальный мост остановлен.";
                string report = "Приложение · " + (check.Installed ? "найдено" : "файл не найден") +
                    "\r\n" + check.Launcher + "\r\n" + connection;
                if (check.ClientStatus != null) report += "\r\n" + check.ClientStatus;
                report += "\r\n\r\nВыполнена локальная проверка. Доступ к сервису можно проверить на вкладке «Диагностика».";

                string actionText = "Открыть диагностику";
                Action action = () => ShowFluentPage(4);
                if (!check.Installed) {
                    actionText = "Обновить список";
                    action = RefreshNativeTargets;
                } else if (check.CanRepairLauncher) {
                    actionText = check.IsCommand ? "Исправить команду" : "Исправить ярлык";
                    action = RepairNativeLauncher;
                } else if (NeedsClientPreparation(check.ClientStatus)) {
                    report += "\r\n\r\nВосстановите исходный клиент, затем нажмите «Пропатчить» в карточке приложения.";
                    actionText = "Восстановить Antigravity";
                    action = () => RunAntigravityOperation("restore");
                } else if (ClientCheckFailed(check.ClientStatus) || check.LauncherReadFailed) {
                    actionText = "Открыть журнал";
                    action = () => ShowFluentPage(3);
                } else if (!configured) {
                    actionText = "Открыть подключение";
                    action = () => ShowFluentPage(1);
                }
                _appFeedback.Text = "Проверка запуска завершена.";
                using (var dialog = BuildAppCheckDialog(target.DisplayName, report, actionText)) {
                    ShowThemedDialog(dialog);
                    if (dialog.DialogResult == DialogResult.OK) selectedAction = action;
                }
            } catch (Exception error) {
                if (!IsDisposed && !Disposing) {
                    Logger.Log("[App check] " + BugReports.Clean(error.ToString()));
                    _appFeedback.Text = "Не удалось завершить проверку. Подробности в журнале.";
                }
            } finally {
                if (!IsDisposed && !Disposing) {
                    _nativeBusy = false;
                    SetNativeButtonsEnabled(true);
                }
            }
            if (selectedAction != null && !IsDisposed && !Disposing) ExecuteNativeAction(selectedAction);
        }

        internal sealed class AppFileCheck {
            public bool Installed, IsCommand, CanRepairLauncher, LauncherReadFailed;
            public string Launcher, ClientStatus;
        }

        internal static AppFileCheck CheckAppFiles(AppTarget target)
        {
            var result = new AppFileCheck { Installed = target.IsInstalled, IsCommand = NativeCliApps.IsCommandTarget(target) };
            string label = result.IsCommand ? "Команда CLI" : "Ярлыки";
            string issue;
            try {
                int count = result.IsCommand ? NativeCliApps.Current.VerifiedCount(target.Id, out issue) :
                    NativeShortcuts.Current.VerifiedCount(target.Id, target, out issue);
                result.CanRepairLauncher = issue != null;
                result.Launcher = label + " · " + (issue ?? (count == 0 ? "обычный запуск" : "настроен запуск через Dagami"));
            } catch (Exception error) {
                Logger.Log("[App check] " + BugReports.Clean(error.ToString()));
                result.Launcher = label + " · не удалось прочитать настройки.";
                result.LauncherReadFailed = true;
            }
            if (result.Installed && AntigravityIntegration.Supports(target.ExePath))
                result.ClientStatus = AntigravityIntegration.ClientPreparationStatus(target.ExePath);
            return result;
        }

        private static Form BuildAppCheckDialog(string appName, string report, string actionText)
        {
            var dialog = new Form {
                Text = "Проверка запуска · " + appName, ClientSize = new Size(570, 280),
                MinimumSize = new Size(460, 240), StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
            };
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var details = new TextBox { Name = "AppCheckDetails", Text = report, Dock = DockStyle.Fill,
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Результаты проверки запуска" };
            rows.Controls.Add(details, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft };
            var close = new FluentButton { Text = "Закрыть", DialogResult = DialogResult.Cancel, AutoSize = true };
            var next = new FluentButton { Text = actionText, DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(close);
            buttons.Controls.Add(next);
            rows.Controls.Add(buttons, 0, 1);
            dialog.Controls.Add(rows);
            dialog.CancelButton = close;
            dialog.ActiveControl = close;
            return dialog;
        }

        private void UpdateAppActionState()
        {
            if (_primaryAppAction == null || _nativeBusy) return;
            AppTarget target = _targetList.SelectedItems.Count == 0 ? null : (AppTarget)_targetList.SelectedItems[0].Tag;
            bool configured = string.IsNullOrEmpty(ConfigManager.Instance.LastLoadError) && HasConfiguredConnection(ConfigManager.Instance.LastProxy, ConfigManager.Instance.UseDagamiDns, ConfigManager.Instance.UseProxy);
            NativeSession session = null;
            bool running = target != null && _nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null;
            _selectedAppHeading.Text = target == null ? "Выберите приложение" : target.DisplayName;
            _primaryAppAction.Text = "Запустить через Dagami";
            _primaryAppAction.AccessibleDescription = running ? "Открывает текущую сессию приложения." :
                configured ? "Запускает приложение с выбранным подключением." : "Предлагает настроить подключение перед запуском.";
            _primaryAppAction.Enabled = running || (target != null && target.IsInstalled);
            bool canPatch = target != null && target.IsInstalled;
            bool cli = NativeCliApps.IsCommandTarget(target);
            bool needsClientPreparation = NeedsClientPreparation(_clientStatusText);
            bool invalidRecovery = ClientCheckFailed(_clientStatusText);
            bool needsRepair = target != null && (!target.IsInstalled || _shortcutIssue != null || needsClientPreparation || invalidRecovery);
            bool hasBinaryPatch = needsClientPreparation || (_clientStatusText != null && _clientStatusText.StartsWith("Патч: применён", StringComparison.OrdinalIgnoreCase));
            bool hasPatchRecords = target != null && (_shortcutRecords > 0 || _shortcutIssue != null || hasBinaryPatch || _selectedPersistentPatch);
            _patchAppAction.Enabled = canPatch;
            _unpatchAppAction.Enabled = hasPatchRecords;
            _checkAppAction.Visible = needsRepair;
            _checkAppAction.Enabled = needsRepair;
            _stopAppItem.Enabled = running;
            _removeAppItem.Enabled = target != null && target.IsCustom;
            string state = target == null ? "Процесс: выберите приложение" : !target.IsInstalled ? "Процесс: файл не найден" :
                running ? "Процесс: запущен через Dagami" : "Процесс: вне сессии Dagami";
            if (target != null && _targetList != null && _targetList.SelectedItems.Count > 0 && _targetList.SelectedItems[0].SubItems.Count > 1) {
                string listStatus = !target.IsInstalled ? "Не найдено" : needsRepair ? "Проверить запуск" : hasPatchRecords ? "Настроено" : "Готово к запуску";
                if (_targetList.SelectedItems[0].SubItems[1].Text != listStatus) _targetList.SelectedItems[0].SubItems[1].Text = listStatus;
            }
            _targetDetails.Text = state;
            UpdateAppRunStatus(target, running ? session : null);
            _shortcutExplanation.Text = target == null ? "Выберите приложение, чтобы настроить его ярлыки." : _shortcutIssue != null ?
                (_shortcutIssue.TrimEnd('.') + ". Нажмите «Проверить и восстановить».") : needsClientPreparation ?
                "Нажмите «Проверить и восстановить», верните исходный клиент и снова нажмите «Пропатчить»." : invalidRecovery ?
                "Dagami не смог проверить данные Antigravity. Нажмите «Проверить и восстановить» для подробностей." :
                !target.IsInstalled ? "Нажмите «Проверить и восстановить», чтобы обновить список установленных приложений." :
                (_shortcutRecords > 0 ? "Открывайте настроенный ярлык: Dagami запустится в фоне." :
                "Нажмите «Пропатчить», чтобы настроить обычный запуск через Dagami.");

            if (target != null && target.IsInstalled) {
                string launcher = cli ? "Команда CLI" : "Ярлыки";
                _shortcutState.Text = launcher + ": " + (_shortcutIssue != null ? "проверьте настройку запуска" :
                    _shortcutRecords > 0 ? "настроен запуск через Dagami" : "обычный запуск");
            } else { _shortcutState.Text = "Статус: не установлено"; }


            if(target!=null&&ApplicationSupport.Kind(target)=="git")_shortcutState.Text="Команда dagami-git: " + (_shortcutRecords > 0 && _shortcutIssue == null ? "настроена" : "не настроена");
            if(_blockAntigravityUpdates!=null){_updatingBlockUpdates=true;_blockAntigravityUpdates.Checked=ConfigManager.Instance.BlockAntigravityUpdates;_blockAntigravityUpdates.Visible=target==null||AntigravityIntegration.Supports(target.ExePath);_updatingBlockUpdates=false;}
            if(_clientPreparationStatus!=null)_clientPreparationStatus.Text=_clientStatusText==null?"":(_clientStatusText+(ConfigManager.Instance.BlockAntigravityUpdates?" · Обновления через мост: блокируются":" · Обновления через мост: разрешены"));
            if(cli&&!needsRepair)_shortcutExplanation.Text=_shortcutRecords>0?"Команда настроена. Перезапустите терминал.":"Нажмите «Пропатчить», затем перезапустите терминал.";
            if (running && !needsRepair) _shortcutExplanation.Text = "Чтобы завершить сессию, откройте контекстное меню строки приложения и выберите «Остановить».";
        }

        private static bool NeedsClientPreparation(string status) {
            return status == "Патч: требуется повторная подготовка CLI / IDE" || status == "Патч: частично применён";
        }

        private static bool ClientCheckFailed(string status) {
            return status == "Патч: не удалось проверить данные восстановления" || status == "Патч: не удалось проверить";
        }

        private FluentButton AddNativeButton(Control parent, string text, Action action)
        {
            var button = new FluentButton { Text = text, AutoSize = true, Height = 34, Primary = false };
            button.Click += (s, e) => {
                ExecuteNativeAction(action);
            };
            _nativeButtons.Add(button);
            parent.Controls.Add(button);
            return button;
        }

        private void SetNativeButtonsEnabled(bool enabled)
        {
            foreach (Button button in _nativeButtons) button.Enabled = enabled;
            if (_appActions != null) _appActions.Enabled = enabled;
            if (enabled) UpdateAppActionState();
        }

        internal static string NormalizeSavedProxy(string proxy, bool useProxy, bool useDns)
        {
            proxy = (proxy ?? "").Trim();
            // Disabling a route must not erase its saved address/credentials.
            if (!useProxy) return proxy;
            if (ManagedAccess.IsProfile(proxy)) return ManagedAccess.Profile;
            if (IsDirectProxyValue(proxy) || (string.IsNullOrEmpty(proxy) && useDns)) return "direct";
            Uri parsed;
            if (!TryParseSupportedProxy(proxy, out parsed))
                throw new InvalidOperationException("Укажите HTTP, HTTPS или SOCKS5 proxy URL (например http://host:8080) или оставьте пустым для работы через Smart DNS.");
            if (UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy(proxy))
                throw new InvalidOperationException("В поле прокси указан DNS-сервер. Укажите HTTP/HTTPS/SOCKS5-прокси, а адрес DNS задайте отдельно.");
            return proxy;
        }

        internal static string NormalizeCustomProxy(string raw, int bridgePort)
        {
            string value = (raw ?? "").Trim();
            if (IsDirectProxyValue(value)) return "direct";
            if (!value.Contains("://") && value.Contains(":")) value = "http://" + value;
            Uri proxy;
            if (value.Length > 2048 || !TryParseSupportedProxy(value, out proxy) || UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy(value))
                throw new ArgumentException("Укажите адрес HTTP/HTTPS/SOCKS5-прокси, например 127.0.0.1:7897, или введите direct для системного VPN.");
            if (LocalProxyBridge.IsBridgeEndpoint(proxy, bridgePort))
                throw new ArgumentException("Этот порт занят мостом Dagami. Введите порт вашего прокси.");
            return value;
        }

        private bool SaveNativeSettings()
        {
            bool useProxy = _useProxyCheckBox != null ? _useProxyCheckBox.Checked : ConfigManager.Instance.UseProxy;
            bool useDns = _dnsEnabled.Checked;
            string proxy = NormalizeSavedProxy(ConfigManager.Instance.LastProxy, useProxy, useDns);
            if (useProxy && (ManagedAccess.IsProfile(proxy) || ConfigManager.Instance.PublicProxySelected)) useDns = false;
            string dns = _dnsBox.Text.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(dns)) dns = "dns.dagami.ru";
            if (!DnsProviders.IsValidSpec(dns))
                throw new InvalidOperationException("Укажите корректный DNS: домен, IP, IP:порт, https://.../dns-query или tls://...");
            // Closing sessions refreshes the UI using the old configuration.
            // Capture every pending edit before asking for confirmation.
            bool obfuscation = _dnsObfuscation != null ? _dnsObfuscation.Checked : ConfigManager.Instance.DnsObfuscation;
            string dpiMode = _dpiModeBox != null ? DpiModeValue(_dpiModeBox.SelectedIndex) : ConfigManager.Instance.DpiDesyncMode;
            bool blockUpdates = _blockAntigravityUpdates != null ? _blockAntigravityUpdates.Checked : ConfigManager.Instance.BlockAntigravityUpdates;
            bool prepareClient = _prepareAntigravity.Checked;
            bool proxyChanged = proxy != ConfigManager.Instance.LastProxy || useProxy != ConfigManager.Instance.UseProxy;
            bool routeChanged = dns != ConfigManager.Instance.DnsHost || useDns != ConfigManager.Instance.UseDagamiDns;
            if ((proxyChanged || routeChanged) && !EnsureSessionsStoppedForRouteChange()) {
                SyncRouteCheckboxes(); return false;
            }
            ConfigManager.Instance.ChangeConnection(cfg => {
                cfg.LastProxy = proxy; cfg.UseProxy = useProxy; cfg.DnsHost = dns; cfg.UseDagamiDns = useDns;
                cfg.DnsObfuscation = obfuscation; cfg.DpiDesyncMode = dpiMode;
                cfg.BlockAntigravityUpdates = blockUpdates;
                cfg.PrepareAntigravityClient = prepareClient;
            });
            if (proxyChanged || routeChanged || _bridgePort <= 0) RestartRescueBridge();
            if (_bridgePort <= 0) throw new InvalidOperationException("Не удалось запустить локальный прокси-мост.");
            UpdateNativeStatus();
            return true;
        }

        private void SyncSelectedAntigravityUpdateBlock()
        {
            try {
                // Update blocking applies only to requests through the bridge.
                // A UI toggle must not delete pending installers.
                if (_appFeedback != null) _appFeedback.Text = ConfigManager.Instance.BlockAntigravityUpdates ? "Запросы обновлений через мост блокируются; другие маршруты не изменены." : "Блокировка обновлений через мост выключена.";
                UpdateAppActionState();
            } catch (Exception ex) { Logger.Log("[UpdateGuard] " + ex.Message); }
        }

        private AppTarget SelectedNativeTarget()
        {
            if (_targetList.SelectedItems.Count == 0) throw new InvalidOperationException("Выберите строку приложения.");
            return (AppTarget)_targetList.SelectedItems[0].Tag;
        }

        private void FitNativeListColumns()
        {
            if (_targetList == null || !_targetList.IsHandleCreated || _targetList.Columns.Count < 3 || _nativeColumnsPending) return;
            _nativeColumnsPending = true;
            BeginInvoke(new Action(() => {
                _nativeColumnsPending = false;
                if (_targetList.IsDisposed) return;
                int statusWidth = Math.Max(144, TextRenderer.MeasureText("Проверить запуск", _targetList.Font).Width + 32);
                _targetList.Columns[0].Width=Math.Max(180,_targetList.ClientSize.Width-statusWidth);
                _targetList.Columns[1].Width=statusWidth;_targetList.Columns[2].Width=0;
            }));
        }

        private bool _targetScanRunning, _targetScanAgain, _startupStatusLoaded;
        private string _targetSelectionAfterScan;
        private HashSet<string> _damagedTargets = new HashSet<string>();
        private Dictionary<string, Bitmap> _nativeAppIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> FindPatchedTargets(List<AppTarget> targets, out HashSet<string> damaged)
        {
            var ready = new HashSet<string>();
            damaged = new HashSet<string>();
            foreach (var target in targets) {
                string state = TargetPatchState(target);
                if (state == "ready") ready.Add(target.Id);
                else if (state == "damaged") damaged.Add(target.Id);
            }
            return ready;
        }

        private static Dictionary<string, Bitmap> LoadNativeAppIcons(List<AppTarget> targets)
        {
            var icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            foreach (AppTarget target in targets) {
                string path = target.ExePath;
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || icons.ContainsKey(path))
                    continue;
                try {
                    if (!target.IsInstalled) continue;
                    using (var icon = Icon.ExtractAssociatedIcon(path)) {
                        if (icon != null) icons.Add(path, icon.ToBitmap());
                    }
                } catch (Exception) { /* The row uses its initial if the shell cannot provide an icon. */ }
            }
            return icons;
        }

        private static void DisposeNativeAppIcons(Dictionary<string, Bitmap> icons)
        {
            if (icons == null) return;
            foreach (var image in icons.Values) image.Dispose();
            icons.Clear();
        }

        private void RefreshNativeTargets() { RefreshNativeTargets(null); }

        private void RefreshNativeTargets(string selectedId)
        {
            if (_targetList == null || IsDisposed) return;
            if (selectedId != null) _targetSelectionAfterScan = selectedId;
            if (_targetScanRunning) { _targetScanAgain = true; return; }
            if (_preview) {
                var targets = ProxyEngine.ScanAllTargets();
                var patched = FindPatchedTargets(targets, out _damagedTargets);
                RenderNativeTargets(targets, patched, LoadNativeAppIcons(targets));
                return;
            }
            _targetScanRunning = true;
            UpdateNativeStatus();
            var custom = ConfigManager.Instance.CustomTargets.ConvertAll(target => new AppTarget {
                Id = target.Id, DisplayName = target.DisplayName, ExePath = target.ExePath,
                ConfigPath = target.ConfigPath, IsCustom = target.IsCustom
            });
            var worker = new System.Threading.Thread(() => {
                List<AppTarget> targets = null;
                HashSet<string> patched = null;
                HashSet<string> damaged = null;
                Dictionary<string, Bitmap> icons = null;
                try {
                    targets = ProxyEngine.ScanAllTargets(custom);
                    patched = FindPatchedTargets(targets, out damaged);
                    icons = LoadNativeAppIcons(targets);
                } catch (Exception error) { Logger.Log("[Detect] " + error.GetType().Name); }
                try { BeginInvoke(new Action(() => {
                    _targetScanRunning = false;
                    if (_targetScanAgain) {
                        DisposeNativeAppIcons(icons);
                        _targetScanAgain = false;
                        RefreshNativeTargets();
                    } else if (patched != null) {
                        _damagedTargets = damaged;
                        RenderNativeTargets(targets, patched, icons);
                    }
                    else { UpdateNativeStatus(); _nativeStatus.Text = "Не удалось найти приложения. Повторите поиск через «Добавить»."; }
                })); } catch (InvalidOperationException) { DisposeNativeAppIcons(icons); }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
        }

        private void RenderNativeTargets(List<AppTarget> targets, HashSet<string> patched, Dictionary<string, Bitmap> icons)
        {
            string selectedId = _targetSelectionAfterScan ?? (_targetList.SelectedItems.Count > 0 ? ((AppTarget)_targetList.SelectedItems[0].Tag).Id : null);
            _targetSelectionAfterScan = null;
            _cachedTargets = targets;
            _refreshingTargets = true;
            _targetList.BeginUpdate();
            try {
                DisposeNativeAppIcons(_nativeAppIcons);
                _nativeAppIcons = icons ?? new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                _targetList.Items.Clear();
                foreach (AppTarget target in _cachedTargets) {
                    var item = new ListViewItem(target.DisplayName) { Tag = target };
                    item.SubItems.Add(!target.IsInstalled ? "Не найдено" : _damagedTargets.Contains(target.Id) ? "Проверить запуск" : (patched.Contains(target.Id) ? "Настроено" : "Готово к запуску"));
                    item.SubItems.Add(target.ExePath ?? "");
                    item.ToolTipText = (target.ExePath ?? target.DisplayName)+"\r\n"+ApplicationSupport.Describe(target);
                    item.Checked = ConfigManager.Instance.CheckedIds.Contains(target.Id);
                    if (!target.IsInstalled) item.ForeColor = SystemColors.GrayText;
                    _targetList.Items.Add(item);
                    if (target.Id == selectedId) item.Selected = true;
                }
                if (_targetList.SelectedItems.Count == 0 && _targetList.Items.Count > 0) {
                    ListViewItem first = _targetList.Items[0];
                    foreach (ListViewItem item in _targetList.Items) if (((AppTarget)item.Tag).IsInstalled) { first = item; break; }
                    first.Selected = true; first.Focused = true;
                }
            } finally { _targetList.EndUpdate(); _refreshingTargets = false; }
            FitNativeListColumns(); UpdateNativeDetails();
            UpdateNativeStatus();
        }

        private void UpdateNativeDetails()
        {
            if (_refreshingTargets || _targetDetails == null) return;
            _shortcutRecords = 0; _shortcutIssue = null;
            _selectedPersistentPatch = false;
            if (_targetList.SelectedItems.Count > 0) {
                AppTarget target = (AppTarget)_targetList.SelectedItems[0].Tag;
                try { _shortcutRecords = NativeCliApps.IsCommandTarget(target)?NativeCliApps.Current.VerifiedCount(target.Id,out _shortcutIssue):NativeShortcuts.Current.VerifiedCount(target.Id, target, out _shortcutIssue); }
                catch (Exception) { _shortcutIssue = "Не удалось прочитать настройки ярлыков. Откройте журнал для проверки."; }
                try { _selectedPersistentPatch=target.IsInstalled&&_shortcutRecords==0&&_shortcutIssue==null&&HasPersistentAppChanges(target); }
                catch { _shortcutIssue="Не удалось прочитать состояние настройки приложения."; }
            }
            QueueClientStatus();
            UpdateAppActionState();
        }

        private void UpdateNativeStatus()
        {
            if (_nativeStatus == null) return;
            int active = 0;
            foreach (var sessionEntry in _nativeSessions.Values) if (sessionEntry.RunningProcess != null) active++;
            _nativeStatus.Text = _cachedTargets.Count == 0 && (_targetScanRunning || !_backgroundWorkStarted)
                ? "Ищем установленные приложения…" : "Приложений: "+_cachedTargets.Count+" · Запущено через Dagami: "+active;
            SyncRouteCheckboxes();
            if (_connectionSummary != null) {
                var config = ConfigManager.Instance;
                bool managed = config.UseProxy && ManagedAccess.IsProfile(config.LastProxy);
                bool paired = ManagedAccess.HasActiveDevice();
                bool needsPairing = managed && !paired;
                string route = managed ? "Прокси Dagami" : config.UseProxy && !IsDirectProxyValue(config.LastProxy)
                    ? (config.PublicProxySelected ? "Публичный прокси" : "Свой прокси") : config.UseDagamiDns ? "Smart DNS" : "Системный маршрут / VPN";
                string routeState = route;
                if (managed) routeState += needsPairing ? (_activationKeySaved ? " · ключ сохранён, ожидает активации" : " · требуется активация ключа") :
                    _bridgePort > 0 ? " · устройство привязано, локальный мост запущен" : " · устройство привязано, локальный мост остановлен";
                else if (!paired && _activationKeySaved) routeState += " · ключ Dagami ожидает активации";
                if (!string.IsNullOrEmpty(config.LastLoadError)) routeState = "Не удалось загрузить настройки. Сохраните подключение заново.";
                _connectionSummary.Text = routeState;
                RefreshWindowsProxyStatus(routeState);
                if (_managedRouteAction != null) _managedRouteAction.Text = paired
                    ? (managed ? "Заменить ключ" : "Использовать Dagami") : _activationKeySaved ? "Активировать ключ" : "Подключить по ключу";
                _connectionAction.Text = needsPairing ? "Подключить" : "Настройки";
            }



            if (_sessionRefreshTimer != null)
                _sessionRefreshTimer.Enabled = active != 0 && Visible && WindowState != FormWindowState.Minimized;
            if (!_nativeBusy && _pageControls != null && _pageControls[2].Visible) UpdateDnsPage();
            UpdateAppActionState();
        }

        private void AddNativeTarget()
        {
            using (var picker = new OpenFileDialog {
                Title = "Выберите приложение или CLI", Filter = "Приложения и CLI (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1",
                CheckFileExists = true, Multiselect = false
            }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                string path = Path.GetFullPath(picker.FileName);
                var existing=ApplicationDiscovery.FindByExecutable(_cachedTargets,path);
                if(existing!=null) {
                    foreach(ListViewItem item in _targetList.Items)item.Selected=((AppTarget)item.Tag).Id==existing.Id;
                    _appFeedback.Text="Приложение уже есть в списке: "+existing.DisplayName+". Выбрана существующая запись.";return;
                }
                string id = StableTargetId(path);
                var candidate=new AppTarget{Id=id,ExePath=path,IsCustom=true};
                using (var dialog = new Form {
                    Text = "Добавить приложение", ClientSize = new Size(540, 290),
                    StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                    MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
                }) {
                    var name = new TextBox { Left = 16, Top = 38, Width = 508, Text = Path.GetFileNameWithoutExtension(path), MaxLength = 80 };
                    var details = new TextBox { Left=16,Top=78,Width=508,Height=150,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text=ApplicationSupport.Describe(candidate)+"\r\n\r\nДобавление само по себе не меняет приложение. «Пропатчить» подготавливает обычный запуск. Smart DNS покрывает известные домены Google; для остальных адресов используется прокси или маршрут Windows. Вход и ответ модели проверяются отдельно." };
                    var ok = new Button { Left = 310, Top = 246, Width = 100, Text = "Добавить", DialogResult = DialogResult.OK };
                    var cancel = new Button { Left = 420, Top = 246, Width = 104, Text = "Отмена", DialogResult = DialogResult.Cancel };
                    dialog.Controls.AddRange(new Control[] {
                        new Label { Text = "Название приложения", Left = 16, Top = 12, Width = 500 }, name, details, ok, cancel
                    });
                    dialog.AcceptButton = ok;
                    dialog.CancelButton = cancel;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    AppTarget target = ConfigManager.Instance.CustomTargets.Find(x => x.Id == id);
                    bool added=target==null;string oldName=target==null?null:target.DisplayName;
                    if (target == null) {
                        target = new AppTarget { Id = id, ExePath = path, IsCustom = true };
                        ConfigManager.Instance.CustomTargets.Add(target);
                    }
                    target.DisplayName = string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileNameWithoutExtension(path) : name.Text.Trim();
                    if(!ConfigManager.Instance.Save()) {
                        if(added)ConfigManager.Instance.CustomTargets.Remove(target);else target.DisplayName=oldName;
                        throw new IOException("Не удалось сохранить добавленное приложение. "+ConfigManager.Instance.LastSaveError);
                    }
                    RefreshNativeTargets(id);
                    Logger.Log("[✓] Приложение добавлено: " + target.DisplayName);
                }
            }
        }

        private void RemoveNativeTarget()
        {
            AppTarget target = SelectedNativeTarget();
            if (!target.IsCustom) throw new InvalidOperationException("Из списка можно удалить только добавленные вручную приложения.");
            NativeSession active;
            if(_nativeSessions.TryGetValue(target.Id,out active)&&active.RunningProcess!=null)throw new InvalidOperationException("Сначала выберите «Остановить» в контекстном меню строки приложения.");
            if(TargetPatchState(target)!="none")throw new InvalidOperationException("Сначала нажмите «Отменить патч». Удаление записи не должно оставлять управляемые ярлыки или команду CLI без возможности отката.");
            int position=ConfigManager.Instance.CustomTargets.IndexOf(target);
            bool wasChecked=ConfigManager.Instance.CheckedIds.Remove(target.Id);
            ConfigManager.Instance.CustomTargets.Remove(target);
            if(!ConfigManager.Instance.Save()) {
                ConfigManager.Instance.CustomTargets.Insert(position,target);
                if(wasChecked)ConfigManager.Instance.CheckedIds.Add(target.Id);
                throw new IOException("Не удалось сохранить удаление приложения. "+ConfigManager.Instance.LastSaveError);
            }
            RefreshNativeTargets();
            Logger.Log("[✓] Приложение удалено из списка: " + target.DisplayName);
        }

        private void ShowNativeWindow()
        {
            if (!_preview && !LicenseManager.IsActivated()) {
                _activationKeySaved = ManagedAccess.ReadDeferredKey() != null;
                if (!_activationKeySaved) {
                    using (var activation = new ActivationForm()) {
                        var result = activation.ShowDialog();
                        if (result != DialogResult.OK && result != DialogResult.Retry) return;
                        _activationKeySaved = result == DialogResult.Retry;
                    }
                }
                UpdateNativeStatus();
            }
            Opacity = 1;
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = _nativeRestoreState;
            Show();
            Activate();
            if (!_preview && !_backgroundWorkStarted) BeginInvoke(new Action(InitializeBackgroundWork));
        }
    }
}
