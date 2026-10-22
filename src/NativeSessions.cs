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
            try {RestoreSettings(target);return restoreLauncher();}
            catch(Exception error) {
                try {RestoreFiles(before);}catch(Exception rollback){throw new IOException(error.Message+" "+rollback.Message,error);}
                throw;
            }
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
            bool binaryPrepared=false, binaryChanged=false;
            string binaryMessage = null;
            bool antigravity = string.Equals(Path.GetFileName(target.ExePath), "Antigravity.exe", StringComparison.OrdinalIgnoreCase);

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
                if (antigravity && binaryPrepared) parts.Add("бинарная подготовка проверена");
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
            if(NativeCliApps.IsCommandTarget(target)) {
                if(restore)NativeCliApps.Current.Restore(target.Id);
                else NativeCliApps.Current.Enable(target,Process.GetCurrentProcess().MainModule.FileName);
                UpdateNativeDetails();
                _appFeedback.Text=restore?"Прямой запуск CLI восстановлен. Перезапустите терминал.":"Обычная команда CLI настроена через прокси. Полностью перезапустите терминал.";
                return;
            }
            var store = NativeShortcuts.Current;
            List<string> paths = restore ? NativeAppPatcher.RestoreWithLauncher(target,()=>store.Restore(target.Id)) : store.EnablePrepared(target, Process.GetCurrentProcess().MainModule.FileName, NativeShortcuts.UserFolders(), () => {}, () => {}, true);
            if (paths.Count == 0) throw new InvalidOperationException(restore ? "Настроенных обычных ярлыков нет." : "Ярлыки не найдены. Используйте «Ещё → Найти или создать ярлык».");
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
