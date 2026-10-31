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
        private CheckBox _systemEnvironment;
        private CheckBox _prepareAntigravity;
        private CheckBox _blockAntigravityUpdates;
        private bool _updatingBlockUpdates;
        private ListView _targetList;
        private TextBox _nativeLog;
        private Label _nativeStatus;
        private Label _targetDetails;
        private System.Windows.Forms.Timer _nativeLogTimer;
        private bool _refreshingTargets;
        private bool _nativeBusy;
        private bool _nativeColumnsPending;
        private FormWindowState _nativeRestoreState = FormWindowState.Normal;
        private readonly List<Button> _nativeButtons = new List<Button>();

        private FluentButton _primaryAppAction;
        private FluentButton _patchAppButton;
        private FluentButton _unpatchAppButton;
        private FluentButton _moreAppActions;
        private ToolStripMenuItem _patchMenuItem;
        private ToolStripMenuItem _unpatchMenuItem;
        private bool _patchInProgress;
        private Label _selectedAppHeading;
        private Label _shortcutExplanation;
        private Label _appFeedback;
        private Label _shortcutState;
        private Label _clientPreparationStatus;
        private ContextMenuStrip _appActions;
        private ToolStripMenuItem _stopAppItem, _removeAppItem, _restoreShortcutItem, _clientPreparationItem;
        private Panel[] _pageControls;
        private string[] _pageNames, _pageDescriptions;
        private bool _proxyCheckFailed;
        private CheckBox _startWithWindows;
        private CheckBox _mainStartWithWindows;
        private CheckBox _useProxyCheckBox;
        private CheckBox _mainUseProxy;
        private CheckBox _mainUseDns;
        private bool _updatingRouteFlags;

        internal void SyncRouteCheckboxes()
        {
            _updatingRouteFlags = true;
            try {
                bool useProxy = ConfigManager.Instance.UseProxy;
                bool useDns = ConfigManager.Instance.UseDagamiDns;
                if (_useProxyCheckBox != null) _useProxyCheckBox.Checked = useProxy;
                if (_mainUseProxy != null) _mainUseProxy.Checked = useProxy;
                if (_mainUseDns != null) _mainUseDns.Checked = useDns;
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
            if (target == null || !target.IsInstalled) return false;
            try {
                string issue;
                int count = NativeCliApps.IsCommandTarget(target)
                    ? NativeCliApps.Current.VerifiedCount(target.Id, out issue)
                    : NativeShortcuts.Current.VerifiedCount(target.Id, out issue);
                if (count > 0 || !string.IsNullOrEmpty(issue)) return true;
            } catch { }
            try {
                if (ApplicationSupport.SupportsSettings(target)) {
                    ApplicationSupport.ResolveSettings(target);
                    if (!string.IsNullOrEmpty(target.ConfigPath) && File.Exists(target.ConfigPath + ".devproxy.bak"))
                        return true;
                }
            } catch { }
            try {
                if (AntigravityIntegration.Supports(target.ExePath)) {
                    foreach (string backend in AntigravityIntegration.BackendPaths(target.ExePath)) {
                        if (AntigravityIntegration.HasActivePatchRecord(backend)) return true;
                    }
                }
            } catch { }
            return false;
        }

        internal void ReadStartupState()
        {
            _updatingStartup = true;
            try {
                var startup = NativeStartup.Current;
                bool enabled = !_preview && startup.IsEnabled();
                if (_startWithWindows != null) _startWithWindows.Checked = enabled;
                if (_mainStartWithWindows != null) _mainStartWithWindows.Checked = enabled;
                if (_startupStatus != null) {
                    _startupStatus.Text = enabled ? "Автозапуск включён для этой копии." :
                        (!_preview && startup.HasLegacyElevatedTask() ? "Обнаружен старый автозапуск с правами администратора. Включите автозапуск заново, чтобы обновить его." : "Автозапуск выключен.");
                }
            } catch (Exception error) {
                if (_startupStatus != null) _startupStatus.Text = "Не удалось проверить автозапуск: " + error.GetType().Name;
            } finally {
                _updatingStartup = false;
            }
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
        private string _shortcutIssue, _proxyCheckMessage;
        private int _shortcutRecords;
        private int _shortcutRefreshTicks;
        private string _clientStatusKey, _clientStatusText;
        private int _clientStatusRevision;
        private bool _selectedPersistentPatch;
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
            if (_pageControls == null) return;
            for (int i = 0; i < _pageControls.Length; i++) {
                _pageControls[i].Visible = i == index;
                _navigation[i].NavigationSelected = i == index;
                _navigation[i].Invalidate();
            }
            _pageControls[index].BringToFront();
            _fluentHeading.Text = _pageNames[index]; _fluentSubtitle.Text = _pageDescriptions[index];
            if (index == 1 && _settingsTabs.SelectedIndex==0) { _proxyBox.Focus(); _proxyBox.SelectAll(); }
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

        private void BuildAppActionsMenu()
        {
            _appActions = new ContextMenuStrip();
            _patchMenuItem=AppMenuItem("Настроить обычный запуск",PatchSelectedNativeTarget);
            _appActions.Items.Add(_patchMenuItem);
            _unpatchMenuItem=AppMenuItem("Отменить патч (вернуть исходный запуск)",UnpatchSelectedNativeTarget);
            _appActions.Items.Add(_unpatchMenuItem);
            _stopAppItem = AppMenuItem("Остановить", () => {
                if (MessageBox.Show(this, "Завершить «" + SelectedNativeTarget().DisplayName + "»? Сохраните работу в приложении перед остановкой.",
                    "Остановить приложение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                StopSelectedNativeSession(); _appFeedback.Text = "Приложение остановлено.";
            });
            _appActions.Items.Add(_stopAppItem);
            var advanced = new ToolStripMenuItem("Дополнительно");
            advanced.DropDownItems.Add(AppMenuItem("Записать настройки приложения", () => ApplySelectedNativeTarget(false)));
            advanced.DropDownItems.Add(AppMenuItem("Восстановить исходные настройки (заменить ручные правки)", () => { if (ConfirmSettingsRestore()) ApplySelectedNativeTarget(true); }));
            _restoreShortcutItem = AppMenuItem("Восстановить обычные ярлыки", () => { if (ConfirmSettingsRestore()) ConfigureNativeShortcuts(true); });
            advanced.DropDownItems.Add(_restoreShortcutItem);
            _clientPreparationItem = new ToolStripMenuItem("Бинарная подготовка Antigravity");
            _clientPreparationItem.DropDownItems.Add(AppMenuItem("Подготовить клиент", () => RunAntigravityOperation("prepare")));
            _clientPreparationItem.DropDownItems.Add(AppMenuItem("Отменить подготовку", () => RunAntigravityOperation("restore")));
            advanced.DropDownItems.Add(_clientPreparationItem);
            _appActions.Items.Add(advanced);
            _removeAppItem = AppMenuItem("Удалить из списка", () => {
                if (_shortcutRecords > 0 || _shortcutIssue != null) throw new InvalidOperationException("Сначала восстановите обычные ярлыки через «Действия → Дополнительно».");
                // session checked inside EnsureSessionStoppedForPatch
                NativeSession session; if (_nativeSessions.TryGetValue(SelectedNativeTarget().Id, out session) && session.RunningProcess != null)
                    throw new InvalidOperationException("Сначала остановите приложение.");
                if (MessageBox.Show(this, "Удалить «" + SelectedNativeTarget().DisplayName + "» из списка Dagami Agent Proxy? Само приложение останется на компьютере.",
                    "Удалить из списка", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) RemoveNativeTarget();
            });
            _appActions.Items.Add(_removeAppItem);
            Disposed += (sender, e) => _appActions.Dispose();
        }

        private void ApplySelectedNativeTarget(bool restore)
        {
            var target = SelectedNativeTarget();
            if (!restore && !SaveNativeSettings()) return;
            string message;
            bool ok = restore ? ProxyEngine.RestoreTarget(target, out message) : ProxyEngine.ApplyProxyToTarget(target, GetIdeProxyUrl(), out message);
            Logger.Log((ok ? "[✓] " : "[-] ") + target.DisplayName + ": " + message);
            if (!ok) throw new InvalidOperationException(message);
            if (!restore && ConfigManager.Instance.SetSystemEnv) ProxyEngine.ApplySystemEnvironment(GetIdeProxyUrl());
            // Restoring one application must not clear shared Windows environment for other apps.
            _appFeedback.Text = target.DisplayName + ": " + message;
        }

        private bool ConfirmSettingsRestore() {
            return MessageBox.Show(this, "Восстановить исходные настройки «" + SelectedNativeTarget().DisplayName +
                "»? Ручные правки управляемых proxy-полей будут заменены. Остальные настройки сохранятся.",
                "Восстановление настроек", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
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
            if (useProxy && (TryParseSupportedProxy(proxyValue, out parsed) || IsDirectProxyValue(proxyValue))) return true;
            if (smartDnsEnabled) return true;
            return false;
        }

        private void PatchSelectedNativeTarget()
        {
            if (!ConfigManager.Instance.UseProxy && !ConfigManager.Instance.UseDagamiDns) { MessageBox.Show(this, "Включите хотя бы один режим работы: «Работать через прокси» или «Работать через DNS» (или оба одновременно).", "Режим подключения", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!HasConfiguredConnection(_proxyBox.Text.Trim(), _dnsEnabled.Checked, ConfigManager.Instance.UseProxy)) { ShowFluentPage(1); return; }
            var target = SelectedNativeTarget(); if (!EnsureSessionStoppedForPatch(target, "Настройка обычного запуска (патч)")) return; if (!SaveNativeSettings()) return;
            string manager = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            _nativeBusy = true; _patchInProgress = true; SetNativeButtonsEnabled(false); _patchMenuItem.Text = "Подготовка…";
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
                    _nativeBusy = false; _patchInProgress = false; _patchMenuItem.Text = "Настроить обычный запуск"; _proxyCheckMessage = null;
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
            // session checked inside EnsureSessionStoppedForPatch
            if (!EnsureSessionStoppedForPatch(target, "Отмена патча")) return;

            if (!ConfirmSettingsRestore()) return;
            _nativeBusy = true; _patchInProgress = true; SetNativeButtonsEnabled(false);
            if (_unpatchAppButton != null) _unpatchAppButton.Text = "Откат…";
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
                    if (_unpatchAppButton != null) _unpatchAppButton.Text = "Отменить патч";
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
            if (!HasConfiguredConnection(ConfigManager.Instance.LastProxy, ConfigManager.Instance.UseDagamiDns, ConfigManager.Instance.UseProxy)) { ShowFluentPage(1); return; }
            var target = SelectedNativeTarget(); NativeSession session;
            if (_nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null) {
                FocusSession(session.RunningProcess); return;
            }
            // Diagnostics are advisory. Launch uses saved parameters and must not
            // depend on a stale probe result or a writable settings directory.
            if (_bridgePort <= 0) RestartRescueBridge();
            _nativeBusy = true; SetNativeButtonsEnabled(false);
            _primaryAppAction.Text = "Запуск…"; _primaryAppAction.Refresh();
            try {
                string error;
                if (!LaunchTarget(target, out error)) {
                    MessageBox.Show(this, error ?? "Не удалось запустить приложение.", "Запуск приложения", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _appFeedback.Text = error ?? "Не удалось запустить приложение.";
                    return;
                }
                _proxyCheckMessage = null;
                string notice;
                _appFeedback.Text = _nativeClientNotices.TryGetValue(target.Id,out notice) ? "Приложение запущено через прокси. "+notice : "Приложение запущено. Закрытие окна Dagami Agent Proxy оставляет прокси в трее.";
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _appFeedback.Text = ex.Message;
            } finally { _nativeBusy = false; SetNativeButtonsEnabled(true); QueueClientStatus(true); UpdateNativeStatus(); }
        }

        private void UpdateAppActionState()
        {
            if (_primaryAppAction == null || _nativeBusy) return;
            AppTarget target = _targetList.SelectedItems.Count == 0 ? null : (AppTarget)_targetList.SelectedItems[0].Tag;
            bool configured = HasConfiguredConnection(ConfigManager.Instance.LastProxy, ConfigManager.Instance.UseDagamiDns, ConfigManager.Instance.UseProxy);
            NativeSession session = null;
            bool running = target != null && _nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null;
            _selectedAppHeading.Text = target == null ? "Выберите приложение" : target.DisplayName;
            bool flagsActive = ConfigManager.Instance.UseProxy || ConfigManager.Instance.UseDagamiDns;
            _primaryAppAction.Text = !flagsActive ? "Выберите режим" : !configured ? "Настроить подключение" : running ? "Открыть" : "Запустить через Dagami";
            _primaryAppAction.Enabled = flagsActive && (!configured || (target != null && target.IsInstalled));
            _moreAppActions.Enabled = target != null;
            bool canPatch = target != null && target.IsInstalled;
            bool hasSettingsBackup = false;
            try { if (target != null && ApplicationSupport.SupportsSettings(target)) { ApplicationSupport.ResolveSettings(target); hasSettingsBackup = !string.IsNullOrEmpty(target.ConfigPath) && File.Exists(target.ConfigPath + ".devproxy.bak"); } } catch { }
            bool hasBinaryPatch = _clientStatusText != null && (_clientStatusText.StartsWith("Патч: применён", StringComparison.OrdinalIgnoreCase) || _clientStatusText.IndexOf("частично", StringComparison.OrdinalIgnoreCase) >= 0 || _clientStatusText.IndexOf("повторная подготовка",StringComparison.OrdinalIgnoreCase)>=0);
            bool isPatched = target != null && (_shortcutRecords > 0 || _shortcutIssue != null || hasSettingsBackup || hasBinaryPatch || _selectedPersistentPatch);
            bool canUnpatch = isPatched;
            _patchMenuItem.Visible = true;
            _patchMenuItem.Enabled = canPatch;
            if (_patchAppButton != null) _patchAppButton.Enabled = canPatch;
            if (_unpatchMenuItem != null) _unpatchMenuItem.Enabled = canUnpatch;
            if (_unpatchAppButton != null) _unpatchAppButton.Enabled = canUnpatch;
            _stopAppItem.Enabled = running;
            _removeAppItem.Enabled = target != null && target.IsCustom;
            _restoreShortcutItem.Enabled = _shortcutRecords > 0 || _shortcutIssue != null;
            _clientPreparationItem.Enabled = target != null && target.IsInstalled && AntigravityIntegration.Supports(target.ExePath);
            string state = target == null ? "Выберите приложение" : !target.IsInstalled ? "Не установлено" :
                running ? "Запущено через Dagami" : "Не запущено через Dagami";
            string patchBadge = target == null || !target.IsInstalled ? "" : (isPatched ? "Пропатчено · " : "Не пропатчено · ");
            if (target != null && _targetList != null && _targetList.SelectedItems.Count > 0 && _targetList.SelectedItems[0].SubItems.Count > 1) {
                string listStatus = !target.IsInstalled ? "Не найдено" : (isPatched ? "Пропатчено" : "Не пропатчено");
                if (_targetList.SelectedItems[0].SubItems[1].Text != listStatus) _targetList.SelectedItems[0].SubItems[1].Text = listStatus;
            }
            _targetDetails.Text = _proxyCheckFailed ? state + ". Последняя проверка маршрута не прошла." : state;
            _shortcutExplanation.Text = target == null ? "Выберите приложение, чтобы настроить его ярлыки." : _shortcutIssue != null ?
                (_shortcutIssue.TrimEnd('.') + ". Нажмите «Пропатчить».") :
                (_shortcutRecords > 0 ? "Открывайте настроенный ярлык: Dagami запустится в фоне." :
                "Нажмите «Пропатчить», затем открывайте приложение обычным ярлыком.");
            bool cli=NativeCliApps.IsCommandTarget(target);

            if (target != null && target.IsInstalled) {
                _shortcutState.Text = isPatched ? ((_shortcutRecords > 0 && _shortcutIssue == null) ? "Статус: ПРОПАТЧЕНО · " + (cli ? "команда настроена через прокси" : "ярлыки настроены через прокси") : "Статус: Частичный патч · Требуется обновить " + (cli ? "команду" : "ярлыки")) : "Статус: НЕ ПРОПАТЧЕНО · " + (cli ? "прямой запуск команды" : "обычные ярлыки напрямую");
            } else { _shortcutState.Text = "Статус: не установлено"; }


            if(target!=null&&ApplicationSupport.Kind(target)=="git")_shortcutState.Text="Команда dagami-git: " + (_shortcutRecords > 0 && _shortcutIssue == null ? "настроена" : "не настроена");
            if(_blockAntigravityUpdates!=null){_updatingBlockUpdates=true;_blockAntigravityUpdates.Checked=ConfigManager.Instance.BlockAntigravityUpdates;_blockAntigravityUpdates.Visible=target==null||AntigravityIntegration.Supports(target.ExePath);_updatingBlockUpdates=false;}
            if(_clientPreparationStatus!=null)_clientPreparationStatus.Text=_clientStatusText==null?"":(_clientStatusText+(ConfigManager.Instance.BlockAntigravityUpdates?" · Обновления через мост: блокируются":" · Обновления через мост: разрешены"));
            if(cli&&_shortcutIssue==null)_shortcutExplanation.Text=_shortcutRecords>0?"Команда настроена. Перезапустите терминал; прямой путь к EXE обходит запускатель.":"Настройте обычную команду CLI, затем перезапустите терминал.";
            _restoreShortcutItem.Text=cli?"Восстановить прямой запуск команды":"Восстановить обычные ярлыки";
            if (running) _shortcutExplanation.Text = "Сессия активна (просто закрыть окно недостаточно — остаются фоновые процессы). Для отзыва или применения патча нажмите кнопку ниже либо выберите «Действия → Остановить».";
            if (_proxyCheckMessage != null) _appFeedback.Text = _proxyCheckMessage;
        }


        private void AddNativeButton(Control parent, string text, Action action)
        {
            var button = new FluentButton { Text = text, AutoSize = true, Height = 34, Primary = false };
            button.Click += (s, e) => {
                ExecuteNativeAction(action);
            };
            _nativeButtons.Add(button);
            parent.Controls.Add(button);
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
            if (IsDirectProxyValue(proxy) || (string.IsNullOrEmpty(proxy) && useDns)) return "direct";
            Uri parsed;
            if (!TryParseSupportedProxy(proxy, out parsed))
                throw new InvalidOperationException("Укажите HTTP или HTTPS proxy URL (например http://host:8080) или оставьте пустым для работы через Smart DNS.");
            if (UpstreamProxyDiagnostics.IsDnsEndpointMistakenForProxy(proxy))
                throw new InvalidOperationException("В поле прокси указан DNS-сервер. Укажите HTTP/HTTPS-прокси, а адрес DNS задайте отдельно.");
            return proxy;
        }

        private bool SaveNativeSettings()
        {
            bool useProxy = _useProxyCheckBox != null ? _useProxyCheckBox.Checked : ConfigManager.Instance.UseProxy;
            bool useDns = _dnsEnabled.Checked;
            string proxy = NormalizeSavedProxy(_proxyBox.Text, useProxy, useDns);
            string dns = _dnsBox.Text.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(dns)) dns = "dns.dagami.ru";
            if (!DnsProviders.IsValidSpec(dns))
                throw new InvalidOperationException("Укажите корректный DNS: домен, IP, IP:порт, https://.../dns-query или tls://...");
            // Closing sessions refreshes the UI using the old configuration.
            // Capture every pending edit before asking for confirmation.
            bool obfuscation = _dnsObfuscation != null ? _dnsObfuscation.Checked : ConfigManager.Instance.DnsObfuscation;
            string dpiMode = _dpiModeBox != null ? DpiModeValue(_dpiModeBox.SelectedIndex) : ConfigManager.Instance.DpiDesyncMode;
            bool blockUpdates = _blockAntigravityUpdates != null ? _blockAntigravityUpdates.Checked : ConfigManager.Instance.BlockAntigravityUpdates;
            bool systemEnvironment = _systemEnvironment.Checked, prepareClient = _prepareAntigravity.Checked;
            bool proxyChanged = proxy != ConfigManager.Instance.LastProxy || useProxy != ConfigManager.Instance.UseProxy;
            bool routeChanged = dns != ConfigManager.Instance.DnsHost || useDns != ConfigManager.Instance.UseDagamiDns;
            if ((proxyChanged || routeChanged) && !EnsureSessionsStoppedForRouteChange()) {
                SyncRouteCheckboxes(); return false;
            }
            ConfigManager.Instance.ChangeConnection(cfg => {
                cfg.LastProxy = proxy; cfg.UseProxy = useProxy; cfg.DnsHost = dns; cfg.UseDagamiDns = useDns;
                cfg.DnsObfuscation = obfuscation; cfg.DpiDesyncMode = dpiMode;
                cfg.BlockAntigravityUpdates = blockUpdates;
                cfg.SetSystemEnv = systemEnvironment;
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
                _targetList.Columns[0].Width=Math.Max(180,_targetList.ClientSize.Width-140);
                _targetList.Columns[1].Width=130;_targetList.Columns[2].Width=0;
            }));
        }

        private void RefreshNativeTargets()
        {
            if (_targetList == null) return;
            string selectedId = _targetList.SelectedItems.Count > 0 ? ((AppTarget)_targetList.SelectedItems[0].Tag).Id : null;
            _cachedTargets = ProxyEngine.ScanAllTargets();
            _refreshingTargets = true;
            _targetList.BeginUpdate();
            try {
                _targetList.Items.Clear();
                foreach (AppTarget target in _cachedTargets) {
                    var item = new ListViewItem(target.DisplayName) { Tag = target };
                    item.SubItems.Add(!target.IsInstalled ? "Не найдено" : (IsTargetPatched(target) ? "Пропатчено" : "Не пропатчено"));
                    item.SubItems.Add(target.ExePath ?? "");
                    item.ToolTipText = (target.ExePath ?? target.DisplayName)+"\r\n"+ApplicationSupport.Describe(target);
                    item.Checked = ConfigManager.Instance.CheckedIds.Contains(target.Id);
                    if (!target.IsInstalled) item.ForeColor = SystemColors.GrayText;
                    _targetList.Items.Add(item);
                    if (target.Id == selectedId) item.Selected = true;
                }
            } finally { _targetList.EndUpdate(); _refreshingTargets = false; }
            if (_targetList.SelectedItems.Count == 0 && _targetList.Items.Count > 0) {
                ListViewItem first = _targetList.Items[0];
                foreach (ListViewItem item in _targetList.Items) if (((AppTarget)item.Tag).IsInstalled) { first = item; break; }
                first.Selected = true; first.Focused = true;
            }
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
                try { _shortcutRecords = NativeCliApps.IsCommandTarget(target)?NativeCliApps.Current.VerifiedCount(target.Id,out _shortcutIssue):NativeShortcuts.Current.VerifiedCount(target.Id, out _shortcutIssue); }
                catch (Exception) { _shortcutIssue = "Не удалось прочитать настройки ярлыков. Откройте журнал для проверки."; }
                _selectedPersistentPatch=IsTargetPatched(target);
            }
            QueueClientStatus();
            UpdateAppActionState();
        }

        private void UpdateNativeStatus()
        {
            if (_nativeStatus == null) return;
            int active = 0;
            foreach (var sessionEntry in _nativeSessions.Values) if (sessionEntry.RunningProcess != null) active++;
            _nativeStatus.Text = "Приложений: "+_cachedTargets.Count+" · Запущено через Dagami: "+active;
            var currentDns=DnsProviders.ForServer(ConfigManager.Instance.DnsHost);
            // connection summary updated below
            SyncRouteCheckboxes();
            // connection summary updated below
            if (_connectionSummary != null) {
                bool flagsActive = ConfigManager.Instance.UseProxy || ConfigManager.Instance.UseDagamiDns;
                string proxyText = ConfigManager.Instance.UseProxy ? (!string.IsNullOrEmpty(ConfigManager.Instance.LastProxy) && !IsDirectProxyValue(ConfigManager.Instance.LastProxy) ? "Прокси: включён" : "Прокси: прямой (direct)") : "Прокси: выключен";
                string dnsText = ConfigManager.Instance.UseDagamiDns ? (currentDns==null?"Свой DNS":currentDns.Length>0&&currentDns[0].Provider=="magicdns"?"DNS: MagicDNS":"DNS: Dagami → MagicDNS") : "DNS: выключен";
                _connectionSummary.Text = !flagsActive ? (_bridgePort > 0 ? "Мост 127.0.0.1:" + _bridgePort + "  ·  " : "") + "Режим не выбран (прокси и DNS выключены)  ·  Включите флаги ниже.\nПараметры подключения — в настройках. Системный DNS — в «DNS»." : (_bridgePort > 0 ? "Мост 127.0.0.1:" + _bridgePort + "  ·  " : "Проверьте подключение  ·  ") + proxyText + "  ·  " + dnsText + "\nПараметры подключения — в настройках. Системный DNS — в «DNS».";
            }



            if(!_nativeBusy)UpdateDnsPage();
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
                    RefreshNativeTargets();
                    foreach (ListViewItem item in _targetList.Items) item.Selected = ((AppTarget)item.Tag).Id == id;
                    Logger.Log("[✓] Приложение добавлено: " + target.DisplayName);
                }
            }
        }

        private void RemoveNativeTarget()
        {
            AppTarget target = SelectedNativeTarget();
            if (!target.IsCustom) throw new InvalidOperationException("Из списка можно удалить только добавленные вручную приложения.");
            NativeSession active;
            if(_nativeSessions.TryGetValue(target.Id,out active)&&active.RunningProcess!=null)throw new InvalidOperationException("Сначала остановите приложение через «Действия → Остановить».");
            if(IsTargetPatched(target))throw new InvalidOperationException("Сначала нажмите «Отменить патч». Удаление записи не должно оставлять управляемые ярлыки или команду CLI без возможности отката.");
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

        private void ApplyNativeTargets(bool restore)
        {
            var targets = new List<AppTarget>();
            foreach (ListViewItem item in _targetList.CheckedItems) targets.Add((AppTarget)item.Tag);
            if (targets.Count == 0) throw new InvalidOperationException("Отметьте приложения флажками в списке.");
            if (!restore && !SaveNativeSettings()) return;
            foreach (AppTarget target in targets) {
                string message;
                bool ok = restore ? ProxyEngine.RestoreTarget(target, out message) : ProxyEngine.ApplyProxyToTarget(target, GetIdeProxyUrl(), out message);
                Logger.Log((ok ? "[✓] " : "[-] ") + target.DisplayName + ": " + message);
            }
            if (restore) ProxyEngine.ClearSystemEnvironment();
            else if (ConfigManager.Instance.SetSystemEnv) ProxyEngine.ApplySystemEnvironment(GetIdeProxyUrl());
        }

        private void ShowNativeWindow()
        {
            if (!_preview && !LicenseManager.IsActivated()) {
                using (var activation = new ActivationForm()) {
                    if (activation.ShowDialog() != DialogResult.OK) {
                        return;
                    }
                }
            }
            Opacity = 1;
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = _nativeRestoreState;
            Show();
            Activate();
        }
    }
}
