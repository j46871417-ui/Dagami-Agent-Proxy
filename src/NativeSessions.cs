using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DevProxyApp
{
    public sealed class NativeSession : IDisposable {
        // Keep GUI applications alive if the proxy process crashes. Explicit
        // session disposal still terminates the job and its descendants.
        public readonly JobObjectManager Job = new JobObjectManager(false);
        public readonly List<Process> Processes = new List<Process>();
        public Process RunningProcess {
            get {
                Process first=null;
                for (int i = Processes.Count - 1; i >= 0; i--) try { if (Processes[i].HasExited) { Processes[i].Dispose(); Processes.RemoveAt(i); } } catch (InvalidOperationException) { Processes.RemoveAt(i); }
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
            try { Job.Terminate(); } catch (System.ComponentModel.Win32Exception) { }
            Job.Dispose();
            foreach (var process in Processes) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); }
            Processes.Clear();
        }
        public void StopAndWait(int timeoutMilliseconds = 5000) {
            if (timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            var elapsed = Stopwatch.StartNew();
            // Keep the Job until exit is verified, so failure does not lose
            // ownership of remaining descendants or allow a route switch.
            Job.Terminate();
            var failures = new List<string>();
            foreach (var process in Processes) {
                try { if (!process.HasExited) process.Kill(); }
                catch (System.ComponentModel.Win32Exception) { if (!process.HasExited) failures.Add("PID " + process.Id); }
                catch (InvalidOperationException) { }
            }
            foreach (var process in Processes) {
                try {
                    int remaining = Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds);
                    if (!process.WaitForExit(remaining)) failures.Add("PID " + process.Id);
                } catch (InvalidOperationException) { }
            }
            while (Job.ProcessIds().Length > 0 && elapsed.ElapsedMilliseconds < timeoutMilliseconds)
                System.Threading.Thread.Sleep(25);
            if (failures.Count > 0 || Job.ProcessIds().Length > 0)
                throw new IOException("Не удалось дождаться завершения процессов сессии. " + string.Join(", ", failures.ToArray()));
            Dispose();
        }
    }
    public static class NativeAppPatcher {
        static void RestoreFiles(Dictionary<string,byte[]> files) {
            var failures=new List<string>();
            foreach(var pair in files) {
              try {
                if(pair.Value!=null&&File.Exists(pair.Key)&&AntigravityIntegration.Hash(File.ReadAllBytes(pair.Key))==AntigravityIntegration.Hash(pair.Value))continue;
                if(pair.Value==null) {if(File.Exists(pair.Key))File.Delete(pair.Key);continue;}
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key));
                string temp=Path.Combine(Path.GetDirectoryName(pair.Key),".dagami-"+Guid.NewGuid().ToString("N")+".tmp");
                try {File.WriteAllBytes(temp,pair.Value);if(File.Exists(pair.Key))File.Replace(temp,pair.Key,null);else File.Move(temp,pair.Key);}finally{if(File.Exists(temp))File.Delete(temp);}
              }catch(Exception ex){failures.Add(Path.GetFileName(pair.Key)+": "+ex.Message);}
            }
            if(failures.Count>0)throw new IOException("Не удалось вернуть файлы: "+string.Join("; ",failures.ToArray()));
        }
        public static void RestoreSettings(AppTarget target) {
            if(!ApplicationSupport.SupportsSettings(target))return;
            ApplicationSupport.ResolveSettings(target);
            if(!string.IsNullOrEmpty(target.ConfigPath)&&File.Exists(target.ConfigPath+".devproxy.bak")) {
                string message;if(!ProxyEngine.RestoreTarget(target,out message))throw new IOException(message);
                Logger.Log("[Restore] "+message);
            }
        }
        public static List<string> RestoreWithLauncher(AppTarget target,Func<List<string>> restoreLauncher) {
            var before=new Dictionary<string,byte[]>();
            if(ApplicationSupport.SupportsSettings(target)) {
                ApplicationSupport.ResolveSettings(target);
                if(!string.IsNullOrEmpty(target.ConfigPath)&&File.Exists(target.ConfigPath+".devproxy.bak"))
                    foreach(string path in new[]{target.ConfigPath,target.ConfigPath+".devproxy.bak"})before[path]=File.Exists(path)?File.ReadAllBytes(path):null;
            }
            List<string> restored;
            try {RestoreSettings(target);restored=restoreLauncher();}
            catch(Exception error) {
                try {RestoreFiles(before);}catch(Exception rollback){throw new IOException(error.Message+" "+rollback.Message,error);}
                throw;
            }
            if(target.IsInstalled && AntigravityIntegration.Supports(target.ExePath)) {
                try { Logger.Log("[Restore] " + AntigravityIntegration.RestoreClient(target.ExePath)); }
                catch(Exception error) { throw new IOException("Ярлыки/команда и настройки восстановлены, но бинарный патч снять не удалось. Повторите отмену патча после устранения причины. " + error.Message,error); }
            }
            return restored;
        }
        public static List<string> Apply(AppTarget target, string manager, IEnumerable<string> folders, NativeShortcuts shortcuts) {
            string summary;
            return Apply(target, manager, folders, shortcuts, null, out summary, false);
        }

        public static List<string> Apply(AppTarget target, string manager, IEnumerable<string> folders, NativeShortcuts shortcuts, string proxyUrl, out string summary, bool autoCreateShortcut = true) {
            if (target == null || !target.IsInstalled) throw new InvalidOperationException("Приложение не найдено.");
            if(NativeCliApps.UsesCommandShim(target)) {
                Dictionary<string,string> snapshot = null;
                string preparation = "";
                try {
                    if (AntigravityIntegration.Supports(target.ExePath)) {
                        snapshot = AntigravityIntegration.Snapshot(target.ExePath);
                        preparation = AntigravityIntegration.PrepareClient(target.ExePath).Message;
                    }
                    string shim=NativeCliApps.Current.Enable(target,manager);
                    summary="Команда «"+NativeCliApps.CommandName(target)+"» настроена через прокси. Полностью перезапустите терминал." + (preparation.Length > 0 ? "\r\n" + preparation : " Исходный CLI не изменялся.");
                    return new List<string>{shim};
                } catch { if (snapshot != null) AntigravityIntegration.Rollback(snapshot); throw; }
            }
            Dictionary<string,string> before = null;
            bool binaryPrepared=false, binaryComplete=false, binaryChanged=false;
            string binaryMessage = null;
            bool antigravity = AntigravityIntegration.IsIde(target.ExePath);

            string configResult = null;
            var configBefore=new Dictionary<string,byte[]>();
            ApplicationSupport.ResolveSettings(target);
            try {
            if (!string.IsNullOrEmpty(proxyUrl) && ApplicationSupport.SupportsSettings(target) && !string.IsNullOrEmpty(target.ConfigPath) && target.ConfigPath != "git://global") {
                foreach(string file in new[]{target.ConfigPath,target.ConfigPath+".devproxy.bak"})configBefore[file]=File.Exists(file)?File.ReadAllBytes(file):null;
                string msg;
                if (ProxyEngine.ApplyProxyToTarget(target, ApplicationSupport.LocalSettingsProxy(proxyUrl), out msg)) {
                    configResult = "прокси записан в " + Path.GetFileName(target.ConfigPath);
                } else {
                    throw new IOException(msg);
                }
            }

                var paths = shortcuts.EnablePrepared(target, manager, folders, () => {
                    if (!antigravity) return;
                    try { before = AntigravityIntegration.Snapshot(target.ExePath); }
                    catch(UnsupportedAntigravityBinaryException) { before=null; }
                    // Unexpected write/verification failures still enter the guarded
                    // rollback path. Only a classified incompatibility is optional.
                    binaryChanged=true;
                    var result=AntigravityIntegration.PrepareClient(target.ExePath);
                    binaryPrepared=result.Prepared;
                    binaryComplete=result.Complete;
                    binaryMessage=result.Message;
                    binaryChanged=false;
                    if (before != null) foreach (var pair in before) if (pair.Value != AntigravityIntegration.Hash(File.ReadAllBytes(pair.Key))) binaryChanged=true;
                    Logger.Log("[Patch] " + result.Message);
                }, () => {
                    if (binaryChanged && before != null) AntigravityIntegration.Rollback(before);
                }, autoCreateShortcut);

                var parts = new List<string>();
                parts.Add("ярлыков: " + paths.Count);
                if(shortcuts.SkippedReadOnlyLinks>0)parts.Add("недоступных ярлыков пропущено: "+shortcuts.SkippedReadOnlyLinks+"; используйте настроенную пользовательскую копию");
                if (!string.IsNullOrEmpty(configResult)) parts.Add(configResult);
                if (antigravity && binaryPrepared) parts.Add(binaryComplete ? "бинарная подготовка проверена" : "бинарная подготовка частичная; проверьте отчёт");
                if (AppProxyPolicy.IsGemini(target)) parts.Add("параметры Chromium/WebView2 при запуске; доступ к сервису проверяется отдельно");
                summary = "Готово (" + string.Join(", ", parts.ToArray()) + "). Запускайте приложение привычным ярлыком.";
                if(antigravity && !binaryPrepared)summary="Прокси и ярлыки настроены: "+paths.Count+". Бинарная подготовка пропущена — несовместимые сигнатуры, backend не изменялся. Проверьте вход и модели в Antigravity.";
                if (antigravity && !string.IsNullOrEmpty(binaryMessage)) summary += "\r\n" + binaryMessage;
                if(antigravity&&shortcuts.SkippedReadOnlyLinks>0)summary+="\r\nНедоступные ярлыки остались без изменений; используйте настроенную пользовательскую копию.";
                return paths;
            } catch (Exception error) {
                try {RestoreFiles(configBefore);}catch(Exception rollback){throw new IOException(error.Message+" "+rollback.Message,error);}
                throw;
            }
        }
    }
    public partial class MainForm
    {
        internal string InitialShortcutKey;
        readonly Dictionary<string, NativeSession> _nativeSessions = new Dictionary<string, NativeSession>();
        readonly Dictionary<string,string> _nativeClientNotices = new Dictionary<string,string>();
        bool _routeChangeInProgress;
        bool HasRunningSessions() { foreach (var session in _nativeSessions.Values) if (session.RunningProcess != null) return true; return false; }
        internal bool EnsureSessionsStoppedForRouteChange(Func<string, bool> confirm = null, Action<NativeSession> stop = null) {
            var active = new List<KeyValuePair<string, NativeSession>>();
            var names = new List<string>();
            foreach (var entry in _nativeSessions) if (entry.Value.RunningProcess != null) {
                active.Add(entry);
                var target = _cachedTargets.Find(x => x.Id == entry.Key);
                names.Add(target == null ? entry.Key : target.DisplayName);
            }
            if (active.Count == 0) return true;
            if (_routeChangeInProgress) throw new InvalidOperationException("Завершение сессий уже выполняется.");
            _routeChangeInProgress = true;
            try {
                string message = "Перед сменой прокси / DNS нужно завершить активные сессии Dagami:\r\n\r\n" +
                    string.Join("\r\n", names.ToArray()) +
                    "\r\n\r\nПринудительно закрыть эти приложения и их фоновые процессы, затем применить настройки?" +
                    "\r\nНесохранённые данные могут быть потеряны. После смены режима приложения нужно запустить заново.";
                bool accepted = confirm != null ? confirm(message) :
                    System.Windows.Forms.MessageBox.Show(this, message, "Смена режима подключения",
                        System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning,
                        System.Windows.Forms.MessageBoxDefaultButton.Button2) == System.Windows.Forms.DialogResult.Yes;
                if (!accepted) return false;
                var failures = new List<string>();
                for (int i = 0; i < active.Count; i++) {
                    var entry = active[i];
                    try {
                        if (stop == null) entry.Value.StopAndWait(); else stop(entry.Value);
                        if (entry.Value.RunningProcess != null) throw new IOException("Процесс сессии остался активным.");
                        if (object.ReferenceEquals(_activeJobMgr, entry.Value.Job)) { _activeSessionProc = null; _activeJobMgr = null; }
                        _nativeSessions.Remove(entry.Key);
                        Logger.Log("[Session] Принудительно остановлена перед сменой маршрута: " + names[i]);
                    } catch (Exception error) {
                        failures.Add(names[i] + " (" + error.GetType().Name + ")");
                    }
                }
                UpdateNativeStatus();
                if (failures.Count > 0) throw new InvalidOperationException("Не удалось закрыть приложения: " + string.Join(", ", failures.ToArray()) + ". Настройки подключения не изменены.");
                return true;
            } finally { _routeChangeInProgress = false; }
        }
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
        static void FocusSession(Process process) {
            IntPtr window = process.MainWindowHandle;
            if (window != IntPtr.Zero) { ShowWindowAsync(window, 9); SetForegroundWindow(window); }
        }
        public string HandleShortcutRequest(NativeLaunchRequest request) {
            try {
                if (_routeChangeInProgress) return "Идёт смена режима подключения. Повторите запуск после её завершения.";
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
                if(_targetList.SelectedItems.Count>0&&((AppTarget)_targetList.SelectedItems[0].Tag).Id==target.Id)QueueClientStatus(true);
                UpdateNativeStatus(); return null;
            } catch (Exception ex) { Logger.Log("[Shortcut] " + ex.GetType().Name); return ex.Message; }
        }
        void ConfigureNativeShortcuts(bool restore) {
            var target = SelectedNativeTarget();
            if (!restore && !SaveNativeSettings()) return;
            if(NativeCliApps.IsCommandTarget(target)) {
                if(restore)NativeCliApps.Current.Restore(target.Id);
                else NativeCliApps.Current.Enable(target,Process.GetCurrentProcess().MainModule.FileName);
                UpdateNativeDetails();
                _appFeedback.Text=restore?"Прямой запуск CLI восстановлен. Перезапустите терминал.":"Обычная команда CLI настроена через прокси. Полностью перезапустите терминал.";
                return;
            }
            var store = NativeShortcuts.Current;
            List<string> paths = restore ? NativeAppPatcher.RestoreWithLauncher(target,()=>store.Restore(target.Id)) : store.EnablePrepared(target, Process.GetCurrentProcess().MainModule.FileName, NativeShortcuts.UserFolders(), () => {}, () => {}, true);
            if (paths.Count == 0) throw new InvalidOperationException(restore ? "Настроенных обычных ярлыков нет." : "Ярлык не найден или не создан. Проверьте права на рабочий стол и повторите настройку.");
            foreach (string path in paths) Logger.Log("[Shortcut] " + (restore ? "Восстановлен: " : "Прокси включён: ") + path);
            UpdateNativeDetails();
            System.Windows.Forms.MessageBox.Show(this, restore ? "Обычные ярлыки восстановлены. Запуск снова выполняется напрямую." :
                "Готово. Открывайте приложение его обычным ярлыком на рабочем столе или в меню «Пуск». Dagami Agent Proxy запустится в фоне автоматически.\r\n\r\nПервый запуск после настройки: закройте копию приложения, открытую без Dagami Agent Proxy.",
                "Dagami Agent Proxy — ярлыки", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
        }
        void StopSelectedNativeSession() {
            var target = SelectedNativeTarget();StopNativeSession(target.Id);UpdateNativeStatus();
            Logger.Log("[Session] Остановлена выбранная сессия: " + target.DisplayName);
        }
        void StopNativeSession(string id) {
            NativeSession session;
            if(!_nativeSessions.TryGetValue(id,out session))throw new InvalidOperationException("У приложения нет активной сессии Dagami.");
            session.StopAndWait();
            if(object.ReferenceEquals(_activeJobMgr,session.Job)){_activeSessionProc=null;_activeJobMgr=null;}
            _nativeSessions.Remove(id);
        }
        void DisposeNativeSessions() {
            foreach (var session in _nativeSessions.Values) session.Dispose();
            _nativeSessions.Clear(); _activeJobMgr = null; _activeSessionProc = null;
        }
        Process AntigravitySession() {
            foreach (var target in _cachedTargets) {
                NativeSession session;
                if (AntigravityIntegration.IsIde(target.ExePath) &&
                    _nativeSessions.TryGetValue(target.Id, out session) && session.RunningProcess != null) return session.RunningProcess;
            }
            return null;
        }
    }
}
