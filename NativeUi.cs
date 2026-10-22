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
        private FluentButton _moreAppActions;
        private FluentButton _patchAppAction;
        private bool _patchInProgress;
        private Label _selectedAppHeading;
        private Label _shortcutExplanation;
        private Label _appFeedback;
        private CheckBox _shortcutToggle;
        private ContextMenuStrip _appActions;
        private ToolStripMenuItem _stopAppItem, _removeAppItem, _restoreShortcutItem;
        private Panel[] _pageControls;
        private string[] _pageNames, _pageDescriptions;
        private bool _updatingShortcutToggle, _proxyCheckFailed;
        private CheckBox _startWithWindows;
        private Label _startupStatus;
        private bool _updatingStartup;
        private string _shortcutIssue, _proxyCheckMessage;
        private int _shortcutRecords;
        private int _shortcutRefreshTicks;

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
            if (index == 1) { _proxyBox.Focus(); _proxyBox.SelectAll(); }
        }

        private void ExecuteNativeAction(Action action)
        {
            if (_nativeBusy) return;
            try { action(); }
            catch (Exception ex) {
                Logger.Log("[-] " + ex.Message);
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
            _appActions.Items.Add(AppMenuItem("Найти или создать ярлык", () => {
                var target = SelectedNativeTarget(); SaveNativeSettings();
                if (!target.IsInstalled) throw new InvalidOperationException("Исполняемый файл не найден.");
                foreach (string path in CreateManagedShortcuts(target)) Logger.Log("[✓] Ярлык создан: " + path);
                UpdateNativeDetails();
                _appFeedback.Text = "Ярлыки найдены или созданы и проверены. Удалённые записи восстановлению не мешают.";
            }));
            _appActions.Items.Add(AppMenuItem("Проверить прокси", CheckFluentProxy));
            _appActions.Items.Add(AppMenuItem("Изменить прокси", () => ShowFluentPage(1)));
            _appActions.Items.Add(new ToolStripSeparator());
            _stopAppItem = AppMenuItem("Остановить", () => {
                if (MessageBox.Show(this, "Завершить «" + SelectedNativeTarget().DisplayName + "»? Сохраните работу в приложении перед остановкой.",
                    "Остановить приложение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                StopSelectedNativeSession(); _appFeedback.Text = "Приложение остановлено.";
            });
            _appActions.Items.Add(_stopAppItem);
            var advanced = new ToolStripMenuItem("Дополнительно");
            advanced.DropDownItems.Add(AppMenuItem("Записать настройки приложения", () => ApplySelectedNativeTarget(false)));
            advanced.DropDownItems.Add(AppMenuItem("Восстановить исходные настройки (заменить ручные правки)", () => ApplySelectedNativeTarget(true)));
            advanced.DropDownItems.Add(AppMenuItem("Проверить ярлыки", UpdateNativeDetails));
            _restoreShortcutItem = AppMenuItem("Восстановить обычные ярлыки", () => ConfigureNativeShortcuts(true));
            advanced.DropDownItems.Add(_restoreShortcutItem);
            _appActions.Items.Add(advanced);
            _removeAppItem = AppMenuItem("Удалить из списка", () => {
                if (_shortcutRecords > 0 || _shortcutIssue != null) throw new InvalidOperationException("Сначала восстановите обычные ярлыки через «Ещё → Дополнительно».");
                NativeSession session;
                if (_nativeSessions.TryGetValue(SelectedNativeTarget().Id, out session) && session.RunningProcess != null)
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
            if (!restore) SaveNativeSettings();
            string message;
            bool ok = restore ? ProxyEngine.RestoreTarget(target, out message) : ProxyEngine.ApplyProxyToTarget(target, GetIdeProxyUrl(), out message);
            Logger.Log((ok ? "[✓] " : "[-] ") + target.DisplayName + ": " + message);
            if (!ok) throw new InvalidOperationException(message);
            if (!restore && ConfigManager.Instance.SetSystemEnv) ProxyEngine.ApplySystemEnvironment(GetIdeProxyUrl());
            // Restoring one application must not clear shared Windows environment for other apps.
            _appFeedback.Text = target.DisplayName + ": " + message;
        }

        private void ToggleNativeShortcuts()
        {
            if (_updatingShortcutToggle || _nativeBusy) return;
            bool enable = _shortcutToggle.Checked;
            ExecuteNativeAction(() => ConfigureNativeShortcuts(!enable));
            UpdateNativeDetails();
        }

        private void PatchSelectedNativeTarget()
        {
            Uri proxy;
            if (!TryParseSupportedProxy(_proxyBox.Text.Trim(), out proxy)) { ShowFluentPage(1); return; }
            var target = SelectedNativeTarget(); SaveNativeSettings();
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
                    _nativeBusy = false; _patchInProgress = false; _patchAppAction.Text = "Пропатчить"; _proxyCheckMessage = null;
                    SetNativeButtonsEnabled(true); UpdateNativeDetails(); _appFeedback.Text = result;
                    if (!success) MessageBox.Show(this, result, "Пропатчить — не завершено", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                })); } catch (InvalidOperationException) { }
            });
            worker.IsBackground = true; worker.SetApartmentState(System.Threading.ApartmentState.STA); worker.Start();
        }

        private void RunPrimaryNativeAction()
        {
            Uri parsed;
            if (!TryParseSupportedProxy(ConfigManager.Instance.LastProxy, out parsed)) { ShowFluentPage(1); return; }
            var target = SelectedNativeTarget(); NativeSession session;
            if (_nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null) {
                FocusSession(session.RunningProcess); return;
            }
            if (_proxyCheckFailed) { CheckFluentProxy(); return; }
            SaveNativeSettings();
            _nativeBusy = true; SetNativeButtonsEnabled(false);
            _primaryAppAction.Text = "Запуск…"; _primaryAppAction.Refresh();
            try {
                string error;
                if (!LaunchTarget(target, out error)) throw new InvalidOperationException(error);
                _proxyCheckMessage = null;
                string notice;
                _appFeedback.Text = _nativeClientNotices.TryGetValue(target.Id,out notice) ? "Приложение запущено через прокси. "+notice : "Приложение запущено. Закрытие окна Dagami Agent Proxy оставляет прокси в трее.";
            } finally { _nativeBusy = false; SetNativeButtonsEnabled(true); UpdateNativeStatus(); }
        }

        private void UpdateAppActionState()
        {
            if (_primaryAppAction == null || _nativeBusy) return;
            AppTarget target = _targetList.SelectedItems.Count == 0 ? null : (AppTarget)_targetList.SelectedItems[0].Tag;
            Uri parsed; bool configured = TryParseSupportedProxy(ConfigManager.Instance.LastProxy, out parsed);
            NativeSession session = null;
            bool running = target != null && _nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null;
            _selectedAppHeading.Text = target == null ? "Выберите приложение" : target.DisplayName;
            _primaryAppAction.Text = !configured ? "Настроить" : running ? "Открыть" : _proxyCheckFailed ? "Проверить" : "Запустить";
            _primaryAppAction.Enabled = !configured || (target != null && target.IsInstalled);
            _moreAppActions.Enabled = target != null;
            _patchAppAction.Enabled = target != null && target.IsInstalled;
            _shortcutToggle.Enabled = target != null && target.IsInstalled;
            _stopAppItem.Enabled = running;
            _removeAppItem.Enabled = target != null && target.IsCustom;
            _restoreShortcutItem.Enabled = _shortcutRecords > 0 || _shortcutIssue != null;
            string state = target == null ? "Выберите приложение" : !target.IsInstalled ? "Не установлено" :
                running ? "Работает через Dagami Agent Proxy" : "Сессия Dagami Agent Proxy не запущена";
            _targetDetails.Text = _proxyCheckFailed ? "Ошибка подключения к прокси" : state;
            _shortcutExplanation.Text = target == null ? "Выберите приложение, чтобы настроить его ярлыки." : _shortcutIssue != null ?
                _shortcutIssue + " Для удалённого или заново установленного ярлыка нажмите «Пропатчить»." :
                (_shortcutRecords > 0 ? "Включено · ярлыков: " + _shortcutRecords + ". Dagami Agent Proxy запускается в фоне до выключения режима." :
                "Выключено. Для запуска привычным ярлыком нажмите «Пропатчить».");
            bool cli=NativeCliApps.IsCommandTarget(target);
            _shortcutToggle.Text=cli?"Запускать обычную команду через прокси":"Запускать через прокси из обычного ярлыка";
            if(target!=null&&ApplicationSupport.Kind(target)=="git")_shortcutToggle.Text="Команда dagami-git через прокси";
            if(cli&&_shortcutIssue==null)_shortcutExplanation.Text=_shortcutRecords>0?"Включено для команды. После изменения полностью перезапустите терминал. Явный путь к исходному EXE обходит запускатель.":"Выключено. «Пропатчить» настроит команду CLI в пользовательском PATH; ярлыки не нужны.";
            _restoreShortcutItem.Text=cli?"Восстановить прямой запуск команды":"Восстановить обычные ярлыки";
            if (_proxyCheckMessage != null) _appFeedback.Text = _proxyCheckMessage;
        }


        private void AddNativeButton(Control parent, string text, Action action)
        {
            var button = new FluentButton { Text = text, AutoSize = true, Height = 34, Primary = true };
            button.Click += (s, e) => {
                ExecuteNativeAction(action);
            };
            _nativeButtons.Add(button);
            parent.Controls.Add(button);
        }

        private void SetNativeButtonsEnabled(bool enabled)
        {
            foreach (Button button in _nativeButtons) button.Enabled = enabled;
            if (_shortcutToggle != null) _shortcutToggle.Enabled = enabled;
            if (_appActions != null) _appActions.Enabled = enabled;
            if (enabled) UpdateAppActionState();
        }

        private void SaveNativeSettings()
        {
            Uri parsed;
            string proxy = _proxyBox.Text.Trim();
            if (!TryParseSupportedProxy(proxy, out parsed))
                throw new InvalidOperationException("Укажите HTTP или HTTPS proxy URL, например http://host:8080.");
            string dns = _dnsBox.Text.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(dns)) dns = "dns.dagami.ru";
            if (Uri.CheckHostName(dns) == UriHostNameType.Unknown)
                throw new InvalidOperationException("Укажите корректное имя DNS сервера.");
            bool proxyChanged = proxy != ConfigManager.Instance.LastProxy;
            bool routeChanged = dns != ConfigManager.Instance.DnsHost || _dnsEnabled.Checked != ConfigManager.Instance.UseDagamiDns;
            if ((proxyChanged || routeChanged) && HasRunningSessions())
                throw new InvalidOperationException("Завершите активную сессию перед сменой прокси или DNS.");
            ConfigManager.Instance.LastProxy = proxy;
            ConfigManager.Instance.DnsHost = dns;
            ConfigManager.Instance.UseDagamiDns = _dnsEnabled.Checked;
            ConfigManager.Instance.SetSystemEnv = _systemEnvironment.Checked;
            ConfigManager.Instance.PrepareAntigravityClient = _prepareAntigravity.Checked;
            ConfigManager.Instance.Save();
            if (proxyChanged || routeChanged || _bridgePort <= 0) RestartRescueBridge();
            if (_bridgePort <= 0) throw new InvalidOperationException("Не удалось запустить локальный прокси-мост.");
            UpdateNativeStatus();
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
                int remaining = _targetList.ClientSize.Width - _targetList.Columns[0].Width - _targetList.Columns[1].Width - 4;
                _targetList.Columns[2].Width = Math.Max(80, remaining);
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
                    item.SubItems.Add(target.IsInstalled ? "Установлено" : "Не найдено");
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
            _updatingShortcutToggle = true;
            try {
                _shortcutRecords = 0; _shortcutIssue = null;
                if (_targetList.SelectedItems.Count > 0) {
                    AppTarget target = (AppTarget)_targetList.SelectedItems[0].Tag;
                    try { _shortcutRecords = NativeCliApps.IsCommandTarget(target)?NativeCliApps.Current.VerifiedCount(target.Id,out _shortcutIssue):NativeShortcuts.Current.VerifiedCount(target.Id, out _shortcutIssue); }
                    catch (Exception) { _shortcutIssue = "Не удалось прочитать настройки ярлыков. Откройте журнал для проверки."; }
                }
                _shortcutToggle.Checked = _shortcutRecords > 0 && _shortcutIssue == null;
            } finally { _updatingShortcutToggle = false; }
            UpdateAppActionState();
        }

        private void UpdateNativeStatus()
        {
            if (_nativeStatus == null) return;
            int active = 0;
            foreach (var sessionEntry in _nativeSessions.Values) if (sessionEntry.RunningProcess != null) active++;
            string session = " | Активных приложений: " + active;
            _nativeStatus.Text = (_bridgePort > 0 ? "Прокси-мост: 127.0.0.1:" + _bridgePort : "Прокси не настроен") +
                " | Приложений: " + _cachedTargets.Count + session;
            if (_connectionSummary != null)
                _connectionSummary.Text = (_bridgePort > 0 ? "Прокси настроен" : "Настройте подключение") +
                    "  ·  " + (ConfigManager.Instance.UseDagamiDns ? "Smart DNS: " + ConfigManager.Instance.DnsHost : "DNS-маршрут отключён") +
                    "\nСистемный DNS: " + SystemDnsManager.BriefStatus() + " · запись в разделе «Подключение»";
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
                string id = StableTargetId(path);
                using (var dialog = new Form {
                    Text = "Добавить приложение", ClientSize = new Size(440, 130),
                    StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                    MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
                }) {
                    var name = new TextBox { Left = 16, Top = 38, Width = 408, Text = Path.GetFileNameWithoutExtension(path), MaxLength = 80 };
                    var ok = new Button { Left = 210, Top = 84, Width = 100, Text = "Добавить", DialogResult = DialogResult.OK };
                    var cancel = new Button { Left = 320, Top = 84, Width = 104, Text = "Отмена", DialogResult = DialogResult.Cancel };
                    dialog.Controls.AddRange(new Control[] {
                        new Label { Text = "Название приложения", Left = 16, Top = 12, Width = 400 }, name, ok, cancel
                    });
                    dialog.AcceptButton = ok;
                    dialog.CancelButton = cancel;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    AppTarget target = ConfigManager.Instance.CustomTargets.Find(x => x.Id == id);
                    if (target == null) {
                        target = new AppTarget { Id = id, ExePath = path, IsCustom = true };
                        ConfigManager.Instance.CustomTargets.Add(target);
                    }
                    target.DisplayName = string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileNameWithoutExtension(path) : name.Text.Trim();
                    ConfigManager.Instance.Save();
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
            ConfigManager.Instance.CustomTargets.RemoveAll(x => x.Id == target.Id);
            ConfigManager.Instance.CheckedIds.Remove(target.Id);
            ConfigManager.Instance.Save();
            RefreshNativeTargets();
            Logger.Log("[✓] Приложение удалено из списка: " + target.DisplayName);
        }

        private void ApplyNativeTargets(bool restore)
        {
            var targets = new List<AppTarget>();
            foreach (ListViewItem item in _targetList.CheckedItems) targets.Add((AppTarget)item.Tag);
            if (targets.Count == 0) throw new InvalidOperationException("Отметьте приложения флажками в списке.");
            if (!restore) SaveNativeSettings();
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
