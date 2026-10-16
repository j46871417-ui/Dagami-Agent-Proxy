using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DevProxyApp
{
    public sealed class NativeSession : IDisposable {
        public readonly JobObjectManager Job = new JobObjectManager();
        public readonly List<Process> Processes = new List<Process>();
        public Process RunningProcess {
            get {
                Process first=null;
                foreach (var process in Processes) try { if (!process.HasExited) { if(process.MainWindowHandle!=IntPtr.Zero)return process; if(first==null)first=process; } } catch (InvalidOperationException) { }
                // Launchers often exit after starting the actual app. The Job owns
                // its descendants, so do not lose proxy/session state with the parent.
                foreach(int id in Job.ProcessIds()) {
                    if(Processes.Exists(p=>p.Id==id))continue;
                    try { var process=Process.GetProcessById(id); if(process.HasExited){process.Dispose();continue;} Processes.Add(process); if(process.MainWindowHandle!=IntPtr.Zero)return process; if(first==null)first=process; }
                    catch(ArgumentException){} catch(System.ComponentModel.Win32Exception){}
                }
                return first;
            }
        }
        public void Dispose() {
            Job.Dispose();
            foreach (var process in Processes) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); }
            Processes.Clear();
        }
    }
    public static class NativeAppPatcher {
        public static List<string> Apply(AppTarget target, string manager, IEnumerable<string> folders, NativeShortcuts shortcuts) {
            string summary;
            return Apply(target, manager, folders, shortcuts, null, out summary, false);
        }

        public static List<string> Apply(AppTarget target, string manager, IEnumerable<string> folders, NativeShortcuts shortcuts, string proxyUrl, out string summary, bool autoCreateShortcut = true) {
            if (target == null || !target.IsInstalled) throw new InvalidOperationException("Приложение не найдено.");
            if(NativeCliApps.IsCli(target)) {
                string shim=NativeCliApps.Current.Enable(target,manager);
                summary="Команда «"+Path.GetFileNameWithoutExtension(target.ExePath)+"» настроена через прокси. Полностью перезапустите терминал. Исходный CLI не изменялся.";
                return new List<string>{shim};
            }
            string backend = null, before = null;
            bool binaryPrepared=false, binaryChanged=false;
            bool antigravity = string.Equals(Path.GetFileName(target.ExePath), "Antigravity.exe", StringComparison.OrdinalIgnoreCase);

            string configResult = null;
            bool configApplied = false;
            if (!string.IsNullOrEmpty(proxyUrl) && !string.IsNullOrEmpty(target.ConfigPath) && target.ConfigPath != "git://global") {
                string msg;
                if (ProxyEngine.ApplyProxyToTarget(target, proxyUrl, out msg)) {
                    configApplied = true;
                    configResult = "прокси записан в " + Path.GetFileName(target.ConfigPath);
                } else {
                    configResult = msg;
                }
            }

            try {
                var paths = shortcuts.EnablePrepared(target, manager, folders, () => {
                    if (!antigravity) return;
                    try { backend = AntigravityIntegration.BackendPath(target.ExePath); }
                    catch(UnsupportedAntigravityBinaryException) { backend=null; }
                    if(backend!=null)before = AntigravityIntegration.Hash(File.ReadAllBytes(backend));
                    // Unexpected write/verification failures still enter the guarded
                    // rollback path. Only a classified incompatibility is optional.
                    binaryChanged=true;
                    var result=AntigravityIntegration.PrepareForProxy(target.ExePath);
                    binaryPrepared=result.Prepared;
                    binaryChanged=binaryPrepared && backend!=null && before!=AntigravityIntegration.Hash(File.ReadAllBytes(backend));
                    Logger.Log("[Patch] " + result.Message);
                }, () => {
                    if (binaryChanged && backend != null && before != null && before != AntigravityIntegration.Hash(File.ReadAllBytes(backend))) {
                        AntigravityIntegration.Restore(target.ExePath);
                        if (before != AntigravityIntegration.Hash(File.ReadAllBytes(backend))) throw new IOException("Не удалось вернуть исходное состояние клиента.");
                    }
                }, autoCreateShortcut);

                var parts = new List<string>();
                parts.Add("ярлыков: " + paths.Count);
                if (!string.IsNullOrEmpty(configResult)) parts.Add(configResult);
                if (antigravity && binaryPrepared) parts.Add("бинарная подготовка проверена");
                if (AppProxyPolicy.IsGemini(target)) parts.Add("параметры Chromium/WebView2 при запуске; доступ к сервису проверяется отдельно");
                summary = "Готово (" + string.Join(", ", parts.ToArray()) + "). Запускайте приложение привычным ярлыком.";
                if(antigravity && !binaryPrepared)summary="Прокси и ярлыки настроены: "+paths.Count+". Бинарная подготовка пропущена — несовместимые сигнатуры, backend не изменялся. Проверьте вход и модели в Antigravity.";
                return paths;
            } catch (Exception) {
                if (configApplied) {
                    string rollbackMsg;
                    ProxyEngine.RestoreTarget(target, out rollbackMsg);
                }
                throw;
            }
        }
    }
    public partial class MainForm
    {
        internal string InitialShortcutKey;
        readonly Dictionary<string, NativeSession> _nativeSessions = new Dictionary<string, NativeSession>();
        readonly Dictionary<string,string> _nativeClientNotices = new Dictionary<string,string>();
        bool HasRunningSessions() { foreach (var session in _nativeSessions.Values) if (session.RunningProcess != null) return true; return false; }
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
        static void FocusSession(Process process) {
            IntPtr window = process.MainWindowHandle;
            if (window != IntPtr.Zero) { ShowWindowAsync(window, 9); SetForegroundWindow(window); }
        }
        public string HandleShortcutRequest(NativeLaunchRequest request) {
            try {
                if (string.IsNullOrEmpty(request.TargetId)) { ShowNativeWindow(); return null; }
                if(_updatePreparing)return "Готовится обновление Dagami. Повторите запуск после перезапуска.";
                if (_patchInProgress) return "Идёт подготовка приложения. Дождитесь завершения и снова откройте ярлык.";
                var target = _cachedTargets.Find(x => x.Id == request.TargetId);
                if (target == null) { _cachedTargets = ProxyEngine.ScanAllTargets(); target = _cachedTargets.Find(x => x.Id == request.TargetId); }
                string arguments = "", directory = null;
                if (!string.IsNullOrEmpty(request.ShortcutKey)) {
                    var record = NativeShortcuts.Current.Resolve(request.ShortcutKey, request.TargetId);
                    if (target == null || !string.Equals(Path.GetFullPath(target.ExePath), record.Executable, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Путь приложения изменился. Заново настройте его обычные ярлыки в DevProxy.");
                    arguments = record.Arguments; directory = record.WorkingDirectory;
                }
                string error;
                if (!LaunchTarget(target, arguments, directory, out error)) return error;
                UpdateNativeStatus(); return null;
            } catch (Exception ex) { Logger.Log("[Shortcut] " + ex.GetType().Name); return ex.Message; }
        }
        void ConfigureNativeShortcuts(bool restore) {
            var target = SelectedNativeTarget();
            if (!restore) SaveNativeSettings();
            if(NativeCliApps.IsCli(target)) {
                if(restore)NativeCliApps.Current.Restore(target.Id);
                else NativeCliApps.Current.Enable(target,Process.GetCurrentProcess().MainModule.FileName);
                UpdateNativeDetails();
                _appFeedback.Text=restore?"Прямой запуск CLI восстановлен. Перезапустите терминал.":"Обычная команда CLI настроена через прокси. Полностью перезапустите терминал.";
                return;
            }
            var store = NativeShortcuts.Current;
            List<string> paths = restore ? store.Restore(target.Id) : store.Enable(target, Process.GetCurrentProcess().MainModule.FileName, NativeShortcuts.UserFolders());
            if (paths.Count == 0) throw new InvalidOperationException(restore ? "Настроенных обычных ярлыков нет." : "Обычные ярлыки приложения на рабочем столе и в меню «Пуск» не найдены. Используйте «Ещё → Создать ярлык».");
            foreach (string path in paths) Logger.Log("[Shortcut] " + (restore ? "Восстановлен: " : "Прокси включён: ") + path);
            UpdateNativeDetails();
            System.Windows.Forms.MessageBox.Show(this, restore ? "Обычные ярлыки восстановлены. Запуск снова выполняется напрямую." :
                "Готово. Открывайте приложение его обычным ярлыком на рабочем столе или в меню «Пуск». DevProxy запустится в фоне автоматически.\r\n\r\nПервый запуск после настройки: закройте копию приложения, открытую без DevProxy.",
                "DevProxy — ярлыки", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
        }
        void StopSelectedNativeSession() {
            var target = SelectedNativeTarget(); NativeSession session;
            if (!_nativeSessions.TryGetValue(target.Id, out session)) throw new InvalidOperationException("У выбранного приложения нет сессии DevProxy.");
            if (_activeSessionProc != null && session.Processes.Contains(_activeSessionProc)) { _activeSessionProc = null; _activeJobMgr = null; }
            session.Dispose(); _nativeSessions.Remove(target.Id); UpdateNativeStatus();
            Logger.Log("[Session] Остановлена выбранная сессия: " + target.DisplayName);
        }
        void DisposeNativeSessions() {
            foreach (var session in _nativeSessions.Values) session.Dispose();
            _nativeSessions.Clear(); _activeJobMgr = null; _activeSessionProc = null;
        }
        Process AntigravitySession() {
            foreach (var target in _cachedTargets) {
                NativeSession session;
                if (Path.GetFileName(target.ExePath).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase) &&
                    _nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null) return session.RunningProcess;
            }
            return null;
        }
    }
}
