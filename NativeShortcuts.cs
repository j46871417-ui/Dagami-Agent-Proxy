using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DevProxyApp
{
    public sealed class ShortcutRecord
    {
        public string Id { get; set; }
        public string ApplicationId { get; set; }
        public string LinkPath { get; set; }
        public string Executable { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string BackupPath { get; set; }
        public string OriginalHash { get; set; }
        public string PatchedHash { get; set; }
    }

    public sealed class NativeShortcuts
    {
        readonly string _root;
        readonly string _journal;
        public static NativeShortcuts Current {
            get { return new NativeShortcuts(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devproxy", "shortcuts")); }
        }
        public NativeShortcuts(string root) { _root = Path.GetFullPath(root); _journal = Path.Combine(_root, "journal.dat"); }
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DevProxy.Native.Shortcuts.v1");
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
        static void Notify(string path) { SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero); }
        public static string Quote(string value) {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
        static string Hash(byte[] data) {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }
        List<ShortcutRecord> Load() {
            if (!File.Exists(_journal)) return new List<ShortcutRecord>();
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_journal), Entropy, DataProtectionScope.CurrentUser);
            return new JavaScriptSerializer().Deserialize<List<ShortcutRecord>>(Encoding.UTF8.GetString(plain));
        }
        void Save(List<ShortcutRecord> records) {
            Directory.CreateDirectory(_root);
            byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(records));
            Atomic(_journal, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        static void Atomic(string path, byte[] data) {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, data); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        static object Open(object shell, string path) {
            return shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        }
        static string Get(object link, string property) { return Convert.ToString(link.GetType().InvokeMember(property, BindingFlags.GetProperty, null, link, null)); }
        static void Set(object link, string property, object value) { link.GetType().InvokeMember(property, BindingFlags.SetProperty, null, link, new object[] { value }); }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        static bool Same(string a, string b) { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        static IEnumerable<string> FindLinks(string folder, int depth) {
            if (!Directory.Exists(folder) || depth > 8 || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) yield break;
            string[] links, dirs;
            try { links = Directory.GetFiles(folder, "*.lnk"); dirs = Directory.GetDirectories(folder); } catch (UnauthorizedAccessException) { yield break; }
            foreach (string link in links) yield return link;
            foreach (string dir in dirs) foreach (string link in FindLinks(dir, depth + 1)) yield return link;
        }
        T Locked<T>(Func<T> action) {
            string name = @"Local\DevProxy.ShortcutStore." + Hash(Encoding.UTF8.GetBytes(_root.ToLowerInvariant()));
            using (var mutex = new Mutex(false, name)) {
                bool owns = false;
                try { try { owns = mutex.WaitOne(10000); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new IOException("Другая операция с ярлыками ещё выполняется.");
                    return action();
                } finally { if (owns) mutex.ReleaseMutex(); }
            }
        }
        static bool EnsureShortcutExists(AppTarget target, List<string> roots) {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string targetFolder = roots.Find(r => Directory.Exists(r) && (
                string.Equals(r, desktop, StringComparison.OrdinalIgnoreCase) ||
                r.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0));
            if (string.IsNullOrEmpty(targetFolder) && roots.Count > 0 && Directory.Exists(roots[0])) targetFolder = roots[0];
            if (string.IsNullOrEmpty(targetFolder)) targetFolder = desktop;

            if (string.IsNullOrEmpty(targetFolder)) return false;
            Directory.CreateDirectory(targetFolder);

            string autoLink = Path.Combine(targetFolder, target.DisplayName + ".lnk");
            object shellNew = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try {
                if (File.Exists(autoLink)) {
                    object test = null;
                    try {
                        test = Open(shellNew, autoLink);
                        string exe = Get(test, "TargetPath");
                        if (!string.IsNullOrEmpty(exe) && Same(exe, target.ExePath)) {
                            if (!roots.Exists(r => Same(r, targetFolder))) roots.Add(targetFolder);
                            return true;
                        }
                    } catch { } finally { Release(test); }
                    autoLink = Path.Combine(targetFolder, target.DisplayName + " (Proxy).lnk");
                }
                object link = Open(shellNew, autoLink);
                Set(link, "TargetPath", Path.GetFullPath(target.ExePath));
                Set(link, "WorkingDirectory", Path.GetDirectoryName(target.ExePath));
                Set(link, "IconLocation", target.ExePath + ",0");
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Release(link);
            } finally { Release(shellNew); }

            if (!roots.Exists(r => Same(r, targetFolder))) roots.Add(targetFolder);
            return true;
        }
        public List<string> EnablePrepared(AppTarget target, string manager, IEnumerable<string> folders, Action prepare, Action rollback) {
            return EnablePrepared(target, manager, folders, prepare, rollback, false);
        }
        public List<string> EnablePrepared(AppTarget target, string manager, IEnumerable<string> folders, Action prepare, Action rollback, bool autoCreateShortcut) {
            return Locked(() => {
                if (!target.IsInstalled || !File.Exists(manager)) throw new InvalidOperationException("Приложение или DevProxy не найдены.");
                var roots = new List<string>(folders);
                var original = Load(); var previous = new HashSet<string>();
                foreach (var record in original) {
                    previous.Add(record.Id);
                    if (record.ApplicationId == target.Id && (!Same(record.Executable, target.ExePath) || !File.Exists(record.LinkPath) || Hash(File.ReadAllBytes(record.LinkPath)) != record.PatchedHash))
                        throw new InvalidOperationException("Обычный ярлык удалён или изменён: " + Path.GetFileName(record.LinkPath) + ". Проверьте ярлыки через «Ещё».");
                }
                bool found = false;
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                try {
                    foreach (string folder in roots) foreach (string path in FindLinks(folder, 0)) {
                        var record = original.Find(x => Same(x.LinkPath, path));
                        if (record != null) { if (record.ApplicationId == target.Id) found = true; continue; }
                        object link = null;
                        try { link = Open(shell, path); string exe = Get(link, "TargetPath"); if (!string.IsNullOrEmpty(exe) && Same(exe, target.ExePath)) found = true; }
                        finally { Release(link); }
                    }
                } finally { Release(shell); }
                if (!found && autoCreateShortcut) {
                    found = EnsureShortcutExists(target, roots);
                }
                if (!found) throw new InvalidOperationException("Обычные ярлыки приложения не найдены на рабочем столе, в меню «Пуск» и закреплениях. Создайте обычный ярлык приложения и повторите «Пропатчить». Изменения не внесены.");
                bool preparationStarted = false;
                try {
                    preparationStarted = true; prepare();
                    var paths = EnableLocked(target, manager, roots);
                    string issue;
                    if (paths.Count == 0 || VerifiedCount(target.Id, out issue) == 0 || issue != null)
                        throw new InvalidOperationException("Проверка настроенных ярлыков не пройдена.");
                    return paths;
                } catch (Exception error) {
                    var failures = new List<string>();
                    try {
                        var added = new HashSet<string>();
                        foreach (var record in Load()) if (!previous.Contains(record.Id)) added.Add(record.Id);
                        RestoreLocked(target.Id, added);
                    } catch (Exception ex) { failures.Add("Ярлыки: " + ex.Message); }
                    if (preparationStarted) try { rollback(); } catch (Exception ex) { failures.Add("Клиент: " + ex.Message); }
                    throw new InvalidOperationException(error.Message + (failures.Count == 0 ? "\r\nНовые изменения отменены." :
                        "\r\nОткат выполнен не полностью: " + string.Join("; ", failures.ToArray())), error);
                }
            });
        }
        public List<string> Enable(AppTarget target, string manager, IEnumerable<string> folders) { return Locked(() => EnableLocked(target, manager, folders)); }
        List<string> EnableLocked(AppTarget target, string manager, IEnumerable<string> folders) {
            if (!target.IsInstalled || !File.Exists(manager)) throw new InvalidOperationException("Приложение или DevProxy не найдены.");
            var records = Load(); var changed = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try {
                foreach (string folder in folders) foreach (string path in FindLinks(folder, 0)) {
                    if (!seen.Add(Path.GetFullPath(path))) continue;
                    var existing = records.Find(x => Same(x.LinkPath, path));
                    if (existing != null) {
                        if (existing.ApplicationId == target.Id) {
                            if (Hash(File.ReadAllBytes(path)) != existing.PatchedHash) throw new InvalidOperationException("Ярлык изменён после настройки: " + Path.GetFileName(path) + ". Изменения не перезаписаны.");
                            changed.Add(path);
                        }
                        continue;
                    }
                    object link = null;
                    try {
                        link = Open(shell, path); string exe = Get(link, "TargetPath");
                        if (string.IsNullOrEmpty(exe) || !Same(exe, target.ExePath)) continue;
                        // WScript hotkeys and icons remain in the copied shell link.
                        byte[] original = File.ReadAllBytes(path);
                        var record = new ShortcutRecord { Id = Guid.NewGuid().ToString("N"), ApplicationId = target.Id,
                            LinkPath = Path.GetFullPath(path), Executable = Path.GetFullPath(exe), Arguments = Get(link, "Arguments"),
                            WorkingDirectory = Get(link, "WorkingDirectory"), OriginalHash = Hash(original) };
                        Directory.CreateDirectory(_root);
                        record.BackupPath = Path.Combine(_root, record.Id + ".lnk.bak");
                        using (var backup = new FileStream(record.BackupPath, FileMode.CreateNew, FileAccess.Write)) backup.Write(original, 0, original.Length);
                        string temp = Path.Combine(Path.GetDirectoryName(path), ".devproxy-" + record.Id + ".lnk");
                        object patched = null;
                        try {
                            File.WriteAllBytes(temp, original); patched = Open(shell, temp);
                            Set(patched, "TargetPath", Path.GetFullPath(manager));
                            Set(patched, "Arguments", "--shortcut-launch " + Quote(target.Id) + " --shortcut-key " + record.Id);
                            if (string.IsNullOrEmpty(Get(patched, "IconLocation"))) Set(patched, "IconLocation", exe + ",0");
                            patched.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, patched, null);
                            Release(patched); patched = null;
                            byte[] replacement = File.ReadAllBytes(temp); record.PatchedHash = Hash(replacement);
                            // Persist recovery data before replacing the original.
                            records.Add(record); Save(records);
                            if (Hash(File.ReadAllBytes(path)) != record.OriginalHash) throw new IOException("Ярлык изменился во время настройки.");
                            File.Replace(temp, path, null); Notify(path); changed.Add(path);
                        } finally { Release(patched); if (File.Exists(temp)) File.Delete(temp); }
                    } finally { Release(link); }
                }
            } finally { Release(shell); }
            return changed;
        }
        public ShortcutRecord Resolve(string id, string applicationId) {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new InvalidOperationException("Некорректный идентификатор ярлыка.");
            var record = Load().Find(x => x.Id == id && x.ApplicationId == applicationId);
            if (record == null) throw new InvalidOperationException("Настройка ярлыка не найдена. Включите прокси для ярлыков заново.");
            return record;
        }
        public int VerifiedCount(string applicationId, out string issue) {
            issue = null; int count = 0;
            foreach (var record in Load()) {
                if (record.ApplicationId != applicationId) continue;
                count++;
                if (!File.Exists(record.LinkPath) || Hash(File.ReadAllBytes(record.LinkPath)) != record.PatchedHash)
                    issue = "Ярлык удалён или изменён. Проверьте ярлыки в меню «Ещё».";
            }
            return count;
        }
        public int Count(string applicationId) { return Load().FindAll(x => x.ApplicationId == applicationId).Count; }
        public List<string> Restore(string applicationId) { return Locked(() => RestoreLocked(applicationId)); }
        List<string> RestoreLocked(string applicationId, HashSet<string> onlyIds = null) {
            var records = Load(); var restored = new List<string>();
            foreach (var record in records.ToArray()) {
                if (record.ApplicationId != applicationId || (onlyIds != null && !onlyIds.Contains(record.Id))) continue;
                if (!File.Exists(record.LinkPath)) throw new InvalidOperationException("Ярлык удалён или перенесён: " + Path.GetFileName(record.LinkPath));
                string current = Hash(File.ReadAllBytes(record.LinkPath));
                if (current != record.PatchedHash && current != record.OriginalHash) throw new InvalidOperationException("Ярлык изменён другим приложением: " + Path.GetFileName(record.LinkPath) + ". Откат остановлен.");
                if (!File.Exists(record.BackupPath) || Hash(File.ReadAllBytes(record.BackupPath)) != record.OriginalHash) throw new InvalidOperationException("Резервная копия ярлыка повреждена.");
                if (current != record.OriginalHash) { Atomic(record.LinkPath, File.ReadAllBytes(record.BackupPath)); Notify(record.LinkPath); }
                records.Remove(record); Save(records); restored.Add(record.LinkPath);
            }
            return restored;
        }
        public static string[] UserFolders() { return new string[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar") }; }
    }
}
