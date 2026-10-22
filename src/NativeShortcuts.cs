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
        public string ConfigPath { get; set; }
        public string DisplayName { get; set; }
    }

    public sealed class NativeShortcuts
    {
        readonly string _root;
        readonly string _journal;
        public int SkippedReadOnlyLinks {get;private set;}
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
            var records=new JavaScriptSerializer().Deserialize<List<ShortcutRecord>>(Encoding.UTF8.GetString(plain));
            if(records==null||records.Exists(record=>record==null||string.IsNullOrEmpty(record.Id)||string.IsNullOrEmpty(record.ApplicationId)||string.IsNullOrEmpty(record.LinkPath)||string.IsNullOrEmpty(record.Executable)||string.IsNullOrEmpty(record.BackupPath)||string.IsNullOrEmpty(record.OriginalHash)||string.IsNullOrEmpty(record.PatchedHash)))throw new InvalidDataException("Повреждена структура журнала ярлыков; файл не изменён.");
            return records;
        }
        void Save(List<ShortcutRecord> records) {
            Directory.CreateDirectory(_root);
            byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(records));
            Atomic(_journal, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        static void Atomic(string path, byte[] data) {
            string temp = Path.Combine(Path.GetDirectoryName(path),".dagami-"+Guid.NewGuid().ToString("N")+".tmp");
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
            if (string.IsNullOrEmpty(folder) || depth > 8 || !Directory.Exists(folder)) yield break;
            string[] links, dirs;
            try {
                // Known Windows folders may themselves be redirected to OneDrive.
                // Do not recurse through nested junctions, which can form cycles.
                if (depth > 0 && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) yield break;
                links = Directory.GetFiles(folder, "*.lnk"); dirs = Directory.GetDirectories(folder);
            } catch (UnauthorizedAccessException) { yield break; } catch (IOException) { yield break; }
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
        static bool CanEditLink(string path) {
            if((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0)return false;
            string probe=Path.Combine(Path.GetDirectoryName(path),".dagami-"+Guid.NewGuid().ToString("N")+".tmp");
            try {using(var stream=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}return true;}
            catch(UnauthorizedAccessException){return false;}
            finally{if(File.Exists(probe))File.Delete(probe);}
        }
        static bool EnsureShortcutExists(AppTarget target, List<string> roots,string template=null) {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string targetFolder = roots.Find(r => Directory.Exists(r) && (
                string.Equals(r, desktop, StringComparison.OrdinalIgnoreCase) ||
                r.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0));
            if (string.IsNullOrEmpty(targetFolder) && roots.Count > 0 && Directory.Exists(roots[0])) targetFolder = roots[0];
            if (string.IsNullOrEmpty(targetFolder)) targetFolder = desktop;

            if (string.IsNullOrEmpty(targetFolder)) return false;
            Directory.CreateDirectory(targetFolder);

            string name = target.DisplayName ?? Path.GetFileNameWithoutExtension(target.ExePath);
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length > 90) name = name.Substring(0, 90);
            if(System.Text.RegularExpressions.Regex.IsMatch(name,@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))name="_"+name;
            if (name.Length == 0) name = "Application";
            string autoLink = Path.Combine(targetFolder, name + ".lnk");
            object shellNew = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try {
                if (File.Exists(autoLink)) {
                    object test = null;
                    try {
                        test = Open(shellNew, autoLink);
                        string exe = Get(test, "TargetPath");
                        if (!string.IsNullOrEmpty(exe) && Same(exe, target.ExePath) && CanEditLink(autoLink)) {
                            if (!roots.Exists(r => Same(r, targetFolder))) roots.Add(targetFolder);
                            return true;
                        }
                    } catch { } finally { Release(test); }
                    int suffix = 1;
                    do { autoLink = Path.Combine(targetFolder, name + " (Dagami " + suffix++ + ").lnk"); }
                    while (File.Exists(autoLink));
                }
                if(template!=null){File.Copy(template,autoLink,false);File.SetAttributes(autoLink,File.GetAttributes(autoLink)&~FileAttributes.ReadOnly);}
                object link = Open(shellNew, autoLink);
                try {
                    Set(link, "TargetPath", Path.GetFullPath(target.ExePath));
                    if(template==null){Set(link, "WorkingDirectory", Path.GetDirectoryName(target.ExePath));Set(link, "IconLocation", target.ExePath + ",0");}
                    link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                }finally{Release(link);}
            } finally { Release(shellNew); }

            if (!roots.Exists(r => Same(r, targetFolder))) roots.Add(targetFolder);
            return true;
        }
        void Reconcile(AppTarget target, List<string> roots) {
            var records = Load(); bool changed = false;
            byte[] before = File.Exists(_journal) ? File.ReadAllBytes(_journal) : null;
            var candidates = new List<string>();
            foreach (string folder in roots) foreach (string path in FindLinks(folder, 0)) candidates.Add(path);
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try {
                foreach (var record in records.ToArray()) {
                    if (record.ApplicationId != target.Id) continue;
                    if (!File.Exists(record.LinkPath)) {
                        string moved = candidates.Find(path => {
                            try { return Hash(File.ReadAllBytes(path)) == record.PatchedHash; }
                            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
                        });
                        if (moved != null) { record.LinkPath = Path.GetFullPath(moved); changed = true; continue; }
                        // A deleted shortcut must not prevent creating a replacement.
                        // Its protected journal snapshot and backup remain recoverable.
                        records.Remove(record); changed = true; continue;
                    }
                    string current = Hash(File.ReadAllBytes(record.LinkPath));
                    if (Same(record.Executable, target.ExePath) && current == record.PatchedHash) continue;
                    // A reinstalled app can recreate its original shortcut at the
                    // same path. Adopt it as a fresh original on an explicit patch.
                    object link = null;
                    try {
                        link = Open(shell, record.LinkPath);
                        string executable = Get(link, "TargetPath");
                        if (!string.IsNullOrEmpty(executable) && Same(executable, target.ExePath)) {
                            records.Remove(record); changed = true;
                        } else {
                            throw new InvalidOperationException("Ярлык изменён другим приложением: " + Path.GetFileName(record.LinkPath) + ". Переместите его или восстановите из копии; чужой ярлык не перезаписан.");
                        }
                    } finally { Release(link); }
                }
            } finally { Release(shell); }
            if (changed) {
                if (before != null) {
                    string history = Path.Combine(_root, "history"); Directory.CreateDirectory(history);
                    Atomic(Path.Combine(history, Guid.NewGuid().ToString("N") + ".dat"), before);
                }
                Save(records);
            }
        }
        public List<string> EnablePrepared(AppTarget target, string manager, IEnumerable<string> folders, Action prepare, Action rollback) {
            return EnablePrepared(target, manager, folders, prepare, rollback, false);
        }
        public List<string> EnablePrepared(AppTarget target, string manager, IEnumerable<string> folders, Action prepare, Action rollback, bool autoCreateShortcut) {
            return Locked(() => {
                if (!target.IsInstalled || !File.Exists(manager)) throw new InvalidOperationException("Приложение или DevProxy не найдены.");
                var roots = new List<string>(folders);
                SkippedReadOnlyLinks=0;
                Reconcile(target, roots);
                var original = Load(); var previous = new HashSet<string>();
                foreach (var record in original) {
                    previous.Add(record.Id);
                    if (record.ApplicationId == target.Id && (!Same(record.Executable, target.ExePath) || !File.Exists(record.LinkPath) || Hash(File.ReadAllBytes(record.LinkPath)) != record.PatchedHash))
                        throw new InvalidOperationException("Обычный ярлык удалён или изменён: " + Path.GetFileName(record.LinkPath) + ". Проверьте ярлыки через «Ещё».");
                }
                bool found = false;string template=null;
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                try {
                    foreach (string folder in roots) foreach (string path in FindLinks(folder, 0)) {
                        var record = original.Find(x => Same(x.LinkPath, path));
                        if (record != null) { if (record.ApplicationId == target.Id) found = true; continue; }
                        object link = null;
                        try { link = Open(shell, path); string exe = Get(link, "TargetPath"); if (!string.IsNullOrEmpty(exe) && Same(exe, target.ExePath)) {if(CanEditLink(path))found=true;else if(template==null)template=path;} }
                        catch (System.Runtime.InteropServices.COMException) { Logger.Log("[Shortcut] Не удалось прочитать: " + Path.GetFileName(path)); }
                        catch (IOException) { Logger.Log("[Shortcut] Ярлык недоступен: " + Path.GetFileName(path)); }
                        finally { Release(link); }
                    }
                } finally { Release(shell); }
                if (!found && autoCreateShortcut) {
                    found = EnsureShortcutExists(target, roots,template);
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
                            object currentLink = Open(shell, path);
                            try {
                                if (!Same(Get(currentLink, "TargetPath"), manager)) {
                                    string temp = Path.Combine(Path.GetDirectoryName(path), ".devproxy-" + Guid.NewGuid().ToString("N") + ".lnk");
                                    string oldHash = existing.PatchedHash; object updated = null;
                                    try {
                                        File.Copy(path, temp); updated = Open(shell, temp); Set(updated, "TargetPath", Path.GetFullPath(manager));
                                        updated.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, updated, null);
                                        Release(updated); updated = null;
                                        existing.PatchedHash = Hash(File.ReadAllBytes(temp)); Save(records);
                                        if (Hash(File.ReadAllBytes(path)) != oldHash) throw new IOException("Ярлык изменился во время перенастройки.");
                                        File.Replace(temp, path, null); Notify(path);
                                    } catch { existing.PatchedHash = oldHash; Save(records); throw; }
                                    finally { Release(updated); if (File.Exists(temp)) File.Delete(temp); }
                                }
                            } finally { Release(currentLink); }
                            changed.Add(path);
                        }
                        continue;
                    }
                    object link = null;
                    try {
                        link = Open(shell, path); string exe = Get(link, "TargetPath");
                        if (string.IsNullOrEmpty(exe) || !Same(exe, target.ExePath)) continue;
                        if(!CanEditLink(path)){SkippedReadOnlyLinks++;Logger.Log("[Shortcut] Нет прав записи; используйте настроенную пользовательскую копию: "+Path.GetFileName(path));continue;}
                        // WScript hotkeys and icons remain in the copied shell link.
                        byte[] original = File.ReadAllBytes(path);
                        var record = new ShortcutRecord { Id = Guid.NewGuid().ToString("N"), ApplicationId = target.Id,
                            ConfigPath=target.ConfigPath,DisplayName=target.DisplayName,
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
                    } catch (System.Runtime.InteropServices.COMException) { Logger.Log("[Shortcut] Не удалось обработать: " + Path.GetFileName(path)); }
                    finally { Release(link); }
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
        public List<AppTarget> RegisteredTargets() {
            var targets=new List<AppTarget>();
            foreach(var record in Load())if(!targets.Exists(target=>target.Id==record.ApplicationId))targets.Add(new AppTarget{Id=record.ApplicationId,DisplayName=(record.DisplayName??Path.GetFileNameWithoutExtension(record.Executable))+" (сохранённая настройка)",ExePath=record.Executable,ConfigPath=record.ConfigPath});
            return targets;
        }
        public List<string> Restore(string applicationId) { return Locked(() => RestoreLocked(applicationId)); }
        List<string> RestoreLocked(string applicationId, HashSet<string> onlyIds = null) {
            var records = Load(); var selected = records.FindAll(record => record.ApplicationId == applicationId && (onlyIds == null || onlyIds.Contains(record.Id)));
            var restored = new List<string>();
            if(selected.Count==0)return restored;
            byte[] journalBefore=File.ReadAllBytes(_journal);
            var before=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
            var originals=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
            bool missing=false;
            // Validate the whole operation before changing the first link or record.
            foreach (var record in selected) {
                if (!File.Exists(record.LinkPath)) {
                    var roots = new List<string> { Path.GetDirectoryName(record.LinkPath) };
                    foreach (string root in UserFolders()) if (!string.IsNullOrEmpty(root) &&
                        Path.GetFullPath(record.LinkPath).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {roots.AddRange(UserFolders());break;}
                    bool located=false;
                    foreach(string root in roots) {
                        foreach(string candidate in FindLinks(root,0))try {
                            string hash=Hash(File.ReadAllBytes(candidate));if(hash!=record.PatchedHash&&hash!=record.OriginalHash)continue;
                            record.LinkPath=Path.GetFullPath(candidate);located=true;break;
                        }catch(IOException){}catch(UnauthorizedAccessException){}
                        if(located)break;
                    }
                }
                if(!File.Exists(record.LinkPath)){missing=true;continue;}
                byte[] current=File.ReadAllBytes(record.LinkPath);string currentHash=Hash(current);
                if(currentHash!=record.PatchedHash&&currentHash!=record.OriginalHash)throw new InvalidOperationException("Ярлык изменён другим приложением: "+Path.GetFileName(record.LinkPath)+". Откат остановлен до изменения файлов.");
                if(!File.Exists(record.BackupPath))throw new InvalidOperationException("Резервная копия ярлыка отсутствует.");
                byte[] original=File.ReadAllBytes(record.BackupPath);
                if(Hash(original)!=record.OriginalHash)throw new InvalidOperationException("Резервная копия ярлыка повреждена.");
                if(before.ContainsKey(record.LinkPath))throw new InvalidDataException("В журнале повторяется путь ярлыка.");
                before[record.LinkPath]=current;originals[record.LinkPath]=original;
            }
            if(missing){string history=Path.Combine(_root,"history");Directory.CreateDirectory(history);Atomic(Path.Combine(history,Guid.NewGuid().ToString("N")+".dat"),journalBefore);}
            var changed=new List<string>();
            try {
                foreach(var pair in before) {
                    string path=pair.Key;
                    if(Hash(File.ReadAllBytes(path))!=Hash(pair.Value))throw new IOException("Ярлык изменился во время восстановления: "+Path.GetFileName(path));
                    if(Hash(pair.Value)==Hash(originals[path]))continue;
                    changed.Add(path);Atomic(path,originals[path]);Notify(path);
                }
                foreach(var record in selected){records.Remove(record);restored.Add(record.LinkPath);}
                Save(records);return restored;
            }catch(Exception error) {
                var failures=new List<string>();
                for(int i=changed.Count-1;i>=0;i--)try {
                    string path=changed[i],hash=Hash(File.ReadAllBytes(path));
                    if(hash==Hash(before[path]))continue;
                    if(hash!=Hash(originals[path]))throw new IOException("Постороннее изменение");
                    Atomic(path,before[path]);Notify(path);
                }catch(Exception ex){failures.Add(Path.GetFileName(changed[i])+": "+ex.Message);}
                try{Atomic(_journal,journalBefore);}catch(Exception ex){failures.Add("Журнал: "+ex.Message);}
                if(failures.Count>0)throw new IOException(error.Message+" Восстановить предыдущее состояние удалось не полностью: "+string.Join("; ",failures.ToArray()),error);
                throw;
            }
        }
        public static string[] UserFolders() { return new string[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }; }
    }
}
