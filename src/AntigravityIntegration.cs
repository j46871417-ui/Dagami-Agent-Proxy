using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DevProxyApp
{
    public sealed class UnsupportedAntigravityBinaryException : InvalidOperationException {
        public UnsupportedAntigravityBinaryException(string message) : base(message) { }
    }
    public sealed class AntigravityPreparationResult {
        public bool Prepared { get; private set; }
        public bool Complete { get; private set; }
        public string Message { get; private set; }
        internal AntigravityPreparationResult(bool prepared, string message, bool complete = true) { Prepared=prepared; Complete=prepared && complete; Message=message; }
    }
    // Independent implementation. Changes are limited to supported metadata/proxy
    // literals and a verified local CLI eligibility test. TLS and server-side
    // authentication/entitlement checks are not changed.
    public static class AntigravityIntegration
    {
        private const string BackupSuffix = ".devproxy-region.bak";
        private const string ManifestSuffix = ".devproxy-region.json";
        private static readonly string[] Patterns = new string[] {
            "\n\x10ineligible_tiers", "R\x0fineligibleTiers", "\n\nineligible\x18\x03",
            "R\nineligible", "name=ineligible,proto3", "name=ineligible_tiers,",
            "json=ineligibleTiers,", "json:\"ineligible_tiers,"
        };

        public static string BackendPath(string executable)
        {
            string name = Path.GetFileName(executable);
            if (name.Equals("agy.exe", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) {
                if (!File.Exists(executable)) throw new UnsupportedAntigravityBinaryException("Файл Antigravity не найден: " + name);
                return Path.GetFullPath(executable);
            }
            if (!IsIde(executable)) throw new UnsupportedAntigravityBinaryException("Неподдерживаемое приложение Antigravity.");
            var paths = BackendPaths(executable);
            if (paths.Count != 1) throw new UnsupportedAntigravityBinaryException("Найдено несколько backend. Используйте подготовку всего клиента или выберите backend отдельно.");
            return paths[0];
        }

        public static bool IsIde(string executable) {
            string name = Path.GetFileName(executable ?? "");
            return name.Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase);
        }
        public static bool Supports(string executable) {
            string name = Path.GetFileName(executable ?? "");
            return IsIde(executable) || name.Equals("agy.exe", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        public static List<string> BackendPaths(string executable) {
            return BackendPaths(executable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        public static List<string> BackendPaths(string executable, string localAppData) {
            return BackendPaths(executable, localAppData, null);
        }
        public static List<string> BackendPaths(string executable, string localAppData, string userProfile) {
            if (!File.Exists(executable)) throw new UnsupportedAntigravityBinaryException("Клиент Antigravity не найден.");
            if (!IsIde(executable))
                return new List<string> { BackendPath(executable) };
            var paths = new List<string>(); string root = Path.GetDirectoryName(executable);
            foreach (string folder in new[] { Path.Combine(root, @"resources\bin"), Path.Combine(root, @"resources\app\extensions\antigravity\bin"), Path.Combine(root, @"resources\app\extensions\agy\bin") }) {
                if (!Directory.Exists(folder)) continue;
                var files = new List<string>(Directory.GetFiles(folder, "language_server*.exe"));
                string cli = Path.Combine(folder, "agy.exe"); if (File.Exists(cli)) files.Add(cli);
                foreach (string file in files)
                    if (!paths.Exists(path => string.Equals(path, file, StringComparison.OrdinalIgnoreCase))) paths.Add(file);
            }
            bool externalLayout = Path.GetFileName(executable).Equals("Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(root, @"resources\app\extensions\agy"));
            if (externalLayout && !string.IsNullOrEmpty(localAppData)) {
                string cli = Path.Combine(localAppData, "agy", "bin", "agy.exe");
                if (File.Exists(cli) && !paths.Exists(path => string.Equals(path, Path.GetFullPath(cli), StringComparison.OrdinalIgnoreCase))) paths.Add(Path.GetFullPath(cli));
            }
            if (externalLayout && !string.IsNullOrEmpty(userProfile)) {
                string cli = Path.Combine(userProfile, ".gemini", "bin", "agy.exe");
                if (File.Exists(cli) && !paths.Exists(path => string.Equals(path, Path.GetFullPath(cli), StringComparison.OrdinalIgnoreCase))) paths.Add(Path.GetFullPath(cli));
            }
            if (paths.Count == 0) throw new UnsupportedAntigravityBinaryException(externalLayout ?
                "IDE использует внешний agy.exe, но он не найден в %USERPROFILE%\\.gemini\\bin или %LOCALAPPDATA%\\agy\\bin. Дайте IDE завершить загрузку backend либо установите Antigravity CLI для этого пользователя. Прокси и ярлыки можно настроить отдельно." :
                "В Antigravity не найден language_server*.exe. Прокси можно использовать отдельно от бинарной подготовки.");
            paths.Sort(StringComparer.OrdinalIgnoreCase); return paths;
        }

        public static Dictionary<string, string> Snapshot(string executable) {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in BackendPaths(executable)) result[path] = Hash(File.ReadAllBytes(path));
            return result;
        }
        public static void Rollback(Dictionary<string, string> before) {
            var errors = new List<string>();
            foreach (var pair in before) try {
                if (Hash(File.ReadAllBytes(pair.Key)) == pair.Value) continue;
                Restore(pair.Key);
                if (Hash(File.ReadAllBytes(pair.Key)) != pair.Value) throw new IOException("Исходный SHA-256 не совпал.");
            } catch (Exception ex) { errors.Add(Path.GetFileName(pair.Key) + ": " + ex.Message); }
            if (errors.Count > 0) throw new IOException("Откат клиента не завершён: " + string.Join("; ", errors.ToArray()));
        }
        public static AntigravityPreparationResult PrepareClient(string executable) {
            return PrepareClient(executable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        public static AntigravityPreparationResult PrepareClient(string executable, string localAppData, string userProfile) {
            List<string> paths;
            try { paths = BackendPaths(executable,localAppData,userProfile); }
            catch (UnsupportedAntigravityBinaryException ex) { return new AntigravityPreparationResult(false, ex.Message); }
            var before = new Dictionary<string, string>(); var messages = new List<string>(); bool prepared = false; int skipped = 0;
            try {
                foreach (string path in paths) {
                    before[path] = Hash(File.ReadAllBytes(path));
                    var result = PrepareForProxy(path); prepared |= result.Prepared; if (!result.Prepared) skipped++;
                    messages.Add(Path.GetFileName(path) + ": " + result.Message);
                }
                return new AntigravityPreparationResult(prepared, (prepared && skipped > 0 ? "Частичная подготовка: есть неподдержанные backend.\r\n" : "") + string.Join(Environment.NewLine, messages.ToArray()), skipped == 0);
            } catch (Exception error) {
                try { Rollback(before); }
                catch (Exception rollback) { throw new IOException(error.Message + " " + rollback.Message, error); }
                return new AntigravityPreparationResult(false, "Подготовка не выполнена; исходные файлы сохранены. Обычный запуск через прокси доступен. Причина: " + error.GetType().Name);
            }
        }
        public static string InspectClient(string executable) {
            bool compatible;return InspectClient(executable,out compatible);
        }
        public static string InspectClient(string executable,out bool compatible) {
            compatible=true;
            var messages = new List<string>();
            foreach (string path in BackendPaths(executable)) {
                try {string detail=Inspect(path);if(detail.StartsWith("Прежняя подготовка CLI"))compatible=false;messages.Add(path + ": " + detail);}
                catch (UnsupportedAntigravityBinaryException ex) {compatible=false;messages.Add(path + ": " + ex.Message); }
            }
            return string.Join(Environment.NewLine, messages.ToArray());
        }
        public static string ClientPreparationStatus(string executable) {
            try {
                int prepared=0,unprepared=0,unsupported=0,upgrade=0;
                foreach(string path in BackendPaths(executable))try{
                    string detail=Inspect(path);
                    if(detail.StartsWith("Клиент подготовлен;"))prepared++;
                    else if(detail.StartsWith("Прежняя подготовка CLI"))upgrade++;
                    else unprepared++;
                }catch(UnsupportedAntigravityBinaryException){unsupported++;}
                if(prepared>0&&(unprepared>0||unsupported>0||upgrade>0))return "Патч: частично применён";
                if(upgrade>0)return "Патч: требуется повторная подготовка CLI / IDE";
                if(unsupported>0)return "Патч: версия или состояние не поддерживается";
                if(unprepared>0)return "Патч: не применён";
                return prepared>0?"Патч: применён, копия проверена":"Патч: backend не найден";
            }catch(UnsupportedAntigravityBinaryException){return "Патч: backend не найден";}
            catch(Exception){return "Патч: не удалось проверить";}
        }
        public static string RestoreClient(string executable) {
            return RestoreClient(executable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        public static string RestoreClient(string executable, string localAppData, string userProfile) {
            var messages = new List<string>(); var errors = new List<string>();
            foreach (string path in BackendPaths(executable,localAppData,userProfile)) {
                if(!File.Exists(path + ManifestSuffix)){messages.Add(Path.GetFileName(path)+": Dagami не изменял этот backend.");continue;}
                try { messages.Add(Path.GetFileName(path) + ": " + Restore(path)); }
                catch (Exception ex) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            if (errors.Count > 0) throw new IOException("Восстановление выполнено частично: " + string.Join("; ", errors.ToArray()));
            return string.Join(Environment.NewLine, messages.ToArray());
        }

        public static string Hash(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static List<int> FindAll(byte[] data, byte[] needle)
        {
            var result = new List<int>();
            if(needle.Length==0)throw new ArgumentException("Empty binary signature.");
            var skip=new int[256];for(int i=0;i<skip.Length;i++)skip[i]=needle.Length;
            for(int i=0;i<needle.Length-1;i++)skip[needle[i]]=needle.Length-1-i;
            for(int offset=0;offset<=data.Length-needle.Length;) {
                int last=data[offset+needle.Length-1],j=needle.Length-1;
                while(j>=0&&data[offset+j]==needle[j])j--;
                if(j<0){result.Add(offset);offset+=needle.Length;}
                else offset+=skip[last];
            }
            return result;
        }

        private static List<int> ValidatePatterns(byte[] original, out List<byte[]> replacements)
        {
            // Validate every signature before touching any byte. Every descriptor
            // and its matching Go tag must agree; partial or ambiguous builds fail.
            var offsets = new List<int>();
            replacements = new List<byte[]>();
            bool legacy = true;
            foreach (string pattern in Patterns) if (FindAll(original, Encoding.ASCII.GetBytes(pattern)).Count == 0) { legacy = false; break; }
            if (!legacy) {
                // Newer Go builds use different protobuf field numbers/layouts.
                // Require both a descriptor and its Go tag, plus Go build data.
                // Unknown/partially modified files are never patched by guesswork.
                bool go = FindAll(original, Encoding.ASCII.GetBytes("Go build ID:")).Count > 0 ||
                    FindAll(original, Encoding.ASCII.GetBytes("Go buildinf:")).Count > 0;
                bool descriptor = FindAll(original, Encoding.ASCII.GetBytes("\n\nineligible\x18")).Count > 0 || FindAll(original, Encoding.ASCII.GetBytes("\n\x10ineligible_tiers")).Count > 0 || FindAll(original, Encoding.ASCII.GetBytes("R\x0fineligibleTiers")).Count > 0;
                bool tag = FindAll(original, Encoding.ASCII.GetBytes("name=ineligible,")).Count > 0 || FindAll(original, Encoding.ASCII.GetBytes("name=ineligible_tiers,")).Count > 0 || FindAll(original, Encoding.ASCII.GetBytes("json:\"ineligible_tiers,")).Count > 0;
                if (go && !descriptor && !tag && FindAll(original, Encoding.ASCII.GetBytes("ineligible")).Count == 0 && FindAll(original, Encoding.ASCII.GetBytes("dpeligible")).Count == 0) {
                    bool legacyInex = (FindAll(original, Encoding.ASCII.GetBytes("\n\ninexigible\x18")).Count > 0 || FindAll(original, Encoding.ASCII.GetBytes("\n\x10inexigible_tiers")).Count > 0) &&
                        (FindAll(original, Encoding.ASCII.GetBytes("name=inexigible,")).Count > 0 || FindAll(original, Encoding.ASCII.GetBytes("name=inexigible_tiers,")).Count > 0);
                    if (legacyInex) {
                        var inexMatches = FindAll(original, Encoding.ASCII.GetBytes("inexigible"));
                        if (inexMatches.Count >= 2 && inexMatches.Count <= 64) {
                            foreach (int offset in inexMatches) { offsets.Add(offset); replacements.Add(Encoding.ASCII.GetBytes("dpeligible")); }
                            return offsets;
                        }
                    }
                }
                if (!go || !descriptor || !tag || FindAll(original, Encoding.ASCII.GetBytes("dpeligible")).Count > 0 ||
                    FindAll(original, Encoding.ASCII.GetBytes("inexigible")).Count > 0)
                    throw new UnsupportedAntigravityBinaryException("Не найдена согласованная структура protobuf Antigravity. Файл не изменён; SHA-256: " + Hash(original) + ".");
                var matches = FindAll(original, Encoding.ASCII.GetBytes("ineligible"));
                if (matches.Count < 2 || matches.Count > 64) throw new UnsupportedAntigravityBinaryException("Неоднозначное количество полей Antigravity; файл не изменён.");
                foreach (int offset in matches) { offsets.Add(offset); replacements.Add(Encoding.ASCII.GetBytes("dpeligible")); }
                return offsets;
            }
            foreach (string pattern in Patterns) {
                byte[] from = Encoding.ASCII.GetBytes(pattern);
                byte[] to = Encoding.ASCII.GetBytes(pattern.Replace("ineligible", "dpeligible"));
                List<int> matches = FindAll(original, from);
                if (matches.Count != 1)
                    throw new UnsupportedAntigravityBinaryException("Бинарная подготовка: несовместимые сигнатуры (шаблон " + (offsets.Count+1) + "/" + Patterns.Length + ", совпадений: " + matches.Count + "). Файл может относиться к другой версии или быть уже изменён. SHA-256: " + Hash(original) + ".");
                offsets.Add(matches[0]); replacements.Add(to);
            }
            return offsets;
        }

        private static List<int> CliHubMetadataOffsets(byte[] data, out List<byte[]> replacements) {
            // CLI and --hub share one executable. The command-line gate alone
            // does not prepare the eight known protobuf metadata fields for IDE.
            var matches = new List<int>[Patterns.Length]; bool hasHubMetadata = false;
            for (int i=0;i<Patterns.Length;i++) {
                matches[i] = FindAll(data, Encoding.ASCII.GetBytes(Patterns[i]));
                if (i!=2 && i!=3 && i!=4 && matches[i].Count>0) hasHubMetadata = true;
            }
            replacements = new List<byte[]>(); var offsets = new List<int>();
            if (!hasHubMetadata) return offsets; // CLI-only shape, no hub schema asserted.
            for (int i=0;i<Patterns.Length;i++) {
                if(matches[i].Count!=1) throw new UnsupportedAntigravityBinaryException("CLI/IDE: неполная или неоднозначная структура региональных protobuf-полей. Файл не изменён.");
                offsets.Add(matches[i][0]); replacements.Add(Encoding.ASCII.GetBytes(Patterns[i].Replace("ineligible", "dpeligible")));
            }
            return offsets;
        }

        private static int ValidatePatchData(byte[] original)
        {
            int? eligibility=CliRegionGate.Find(original);
            var proxySites=FindAll(original,Encoding.ASCII.GetBytes("https_proxy"));
            if(eligibility.HasValue){
                if((proxySites.Count<1||proxySites.Count>32)&&!(proxySites.Count==0&&FindAll(original,Encoding.ASCII.GetBytes("AG_LS_PROXY")).Count>=1))throw new UnsupportedAntigravityBinaryException("CLI: неизвестная структура переменных прокси. Файл не изменён.");
                List<byte[]> hubReplacements;
                return 1+proxySites.Count+CliHubMetadataOffsets(original,out hubReplacements).Count;
            }
            List<byte[]> replacements;int count=ValidatePatterns(original,out replacements).Count;
            if(proxySites.Count>32)throw new UnsupportedAntigravityBinaryException("Неоднозначные переменные прокси Antigravity; файл не изменён.");
            return count+proxySites.Count;
        }

        public static byte[] PatchData(byte[] original, out int changes)
        {
            int? eligibility=CliRegionGate.Find(original);
            if(eligibility.HasValue){
                var proxySites=FindAll(original,Encoding.ASCII.GetBytes("https_proxy"));
                if((proxySites.Count<1||proxySites.Count>32)&&!(proxySites.Count==0&&FindAll(original,Encoding.ASCII.GetBytes("AG_LS_PROXY")).Count>=1))throw new UnsupportedAntigravityBinaryException("CLI: неизвестная структура переменных прокси. Файл не изменён.");
                List<byte[]> hubReplacements;var hubOffsets=CliHubMetadataOffsets(original,out hubReplacements);
                byte[] result=(byte[])original.Clone();byte[] gate={0x48,0x85,0xc0,0x90};Array.Copy(gate,0,result,eligibility.Value,gate.Length);
                byte[] variable=Encoding.ASCII.GetBytes("AG_LS_PROXY");foreach(int site in proxySites)Array.Copy(variable,0,result,site,variable.Length);
                for(int i=0;i<hubOffsets.Count;i++)Array.Copy(hubReplacements[i],0,result,hubOffsets[i],hubReplacements[i].Length);
                changes=1+proxySites.Count+hubOffsets.Count;return result;
            }
            List<byte[]> replacements;
            var offsets=ValidatePatterns(original,out replacements);
            byte[] patched = (byte[])original.Clone();
            for (int i = 0; i < offsets.Count; i++) Array.Copy(replacements[i], 0, patched, offsets[i], replacements[i].Length);
            changes = offsets.Count;
            // A private process environment variable lets managed LS/agy traffic
            // use the existing bridge without changing Windows environment.
            var proxyOffsets = FindAll(original, Encoding.ASCII.GetBytes("https_proxy"));
            if (proxyOffsets.Count > 32) throw new UnsupportedAntigravityBinaryException("Неоднозначные переменные прокси Antigravity; файл не изменён.");
            foreach (int offset in proxyOffsets) { Array.Copy(Encoding.ASCII.GetBytes("AG_LS_PROXY"), 0, patched, offset, 11); changes++; }
            return patched;
        }

        private static Dictionary<string, object> Manifest(string backend)
        {
            string path = backend + ManifestSuffix;
            if (!File.Exists(path)) return null;
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (result == null || !result.ContainsKey("original_sha256") || !result.ContainsKey("patched_sha256"))
                throw new InvalidDataException("Повреждён манифест резервной копии Antigravity.");
            return result;
        }
        public static bool HasActivePatchRecord(string backend) {
            if(!File.Exists(backend+ManifestSuffix))return false;
            try{var manifest=Manifest(backend);object restored;return !manifest.TryGetValue("restored",out restored)||!object.Equals(restored,true);}
            catch{return true;} // Damaged ownership data still needs a recovery action.
        }
        private static void RecordRestored(string backend,Dictionary<string,object> manifest) {
            manifest["restored"]=true;
            AtomicWrite(backend+ManifestSuffix,Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(manifest)));
        }

        public static string Inspect(string executable)
        {
            string backend = BackendPath(executable);
            byte[] current = File.ReadAllBytes(backend);
            string hash = Hash(current);
            Dictionary<string, object> manifest = Manifest(backend);
            if (manifest != null && hash == Convert.ToString(manifest["patched_sha256"])) {
                if (!File.Exists(backend + BackupSuffix) || Hash(File.ReadAllBytes(backend + BackupSuffix)) != Convert.ToString(manifest["original_sha256"]))
                    throw new InvalidDataException("Подготовленный клиент найден, но резервная копия повреждена или отсутствует.");
                if(NeedsCliUpgrade(current))return "Прежняя подготовка CLI не включает все необходимые изменения для CLI/IDE. Восстановите клиент, затем выполните подготовку снова. Резервная копия проверена.";
                bool legacyProxy = FindAll(current, Encoding.ASCII.GetBytes("https_proxy")).Count > 0 &&
                    FindAll(current, Encoding.ASCII.GetBytes("AG_LS_PROXY")).Count == 0;
                return "Клиент подготовлен; резервная копия проверена. Файл: " + Path.GetFileName(backend) + "." +
                    (legacyProxy ? " Прежний патч использует HTTPS_PROXY управляемого процесса. Для отдельной AG_LS_PROXY выполните «Отменить подготовку», затем «Подготовить клиент»." : "");
            }
            if (IsAlreadyPreparedBinary(current) && !NeedsCliUpgrade(current))
                return "Клиент подготовлен; сигнатуры проверены. Файл: " + Path.GetFileName(backend) + ".";
            int count=ValidatePatchData(current);
            return "Antigravity " + FileVersionInfo.GetVersionInfo(executable).ProductVersion + ": найдено " + count + " сигнатур; требуется подготовка клиента.";
        }

        static bool IsAlreadyPreparedBinary(byte[] current) {
            if (current == null || current.Length < 128) return false;
            bool go = FindAll(current, Encoding.ASCII.GetBytes("Go build ID:")).Count > 0 || FindAll(current, Encoding.ASCII.GetBytes("Go buildinf:")).Count > 0;
            if (!go) return false;
            int dpCount = FindAll(current, Encoding.ASCII.GetBytes("dpeligible")).Count;
            int inelCount = FindAll(current, Encoding.ASCII.GetBytes("ineligible")).Count;
            int inexCount = FindAll(current, Encoding.ASCII.GetBytes("inexigible")).Count;
            if (dpCount >= 2 && inelCount == 0 && inexCount == 0) return true;
            if (FindAll(current, Encoding.ASCII.GetBytes("AG_LS_PROXY")).Count >= 1 && FindAll(current, Encoding.ASCII.GetBytes("Eligibility check failed:")).Count > 0 && !NeedsCliUpgrade(current)) return true;
            return false;
        }
        static bool NeedsCliUpgrade(byte[] current) {
            List<byte[]> replacements;
            if(CliHubMetadataOffsets(current,out replacements).Count>0)return true;
            try{return CliRegionGate.Find(current).HasValue;}catch(UnsupportedAntigravityBinaryException){return false;}
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path), ".dagami-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static void EnsureNotRunning(string backend)
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(backend))) {
                try {
                    if (string.Equals(process.MainModule.FileName, backend, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Закройте Antigravity перед подготовкой или восстановлением клиента.");
                } catch (System.ComponentModel.Win32Exception) {
                    throw new InvalidOperationException("Не удалось проверить запущенный language_server. Закройте Antigravity и повторите.");
                } finally { process.Dispose(); }
            }
        }

        public static string Prepare(string executable)
        {
            string backend = BackendPath(executable);
            using (var guard = new FileStream(backend + ".devproxy.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                byte[] original = File.ReadAllBytes(backend);
                string originalHash = Hash(original);
                Dictionary<string, object> previous = Manifest(backend);
                if (previous != null && originalHash == Convert.ToString(previous["patched_sha256"])) return Inspect(executable);
                EnsureNotRunning(backend);
                int changes;
                byte[] patched = PatchData(original, out changes);
                string backup = backend + BackupSuffix;
                if (File.Exists(backup) && Hash(File.ReadAllBytes(backup)) != originalHash) {
                    if (previous == null || Hash(File.ReadAllBytes(backup)) != Convert.ToString(previous["original_sha256"]))
                        throw new InvalidOperationException("Резервная копия Antigravity не соответствует манифесту. Подготовка остановлена.");
                    // Compatible application update: keep the previous generation
                    // and take a new baseline, rather than blocking every upgrade.
                    string archive = backend + ".devproxy-history"; Directory.CreateDirectory(archive);
                    string originalGenerationHash = Convert.ToString(previous["original_sha256"]);
                    string generation = originalGenerationHash.Substring(0, 16);
                    string archivedBackup = Path.Combine(archive, generation + ".bak");
                    if (File.Exists(archivedBackup) && Hash(File.ReadAllBytes(archivedBackup)) != originalGenerationHash)
                        throw new InvalidDataException("Архив старой версии занят другой копией; файлы не перезаписаны.");
                    AtomicWrite(archivedBackup, File.ReadAllBytes(backup));
                    AtomicWrite(Path.Combine(archive, generation + ".json"), File.ReadAllBytes(backend + ManifestSuffix));
                    AtomicWrite(backup, original);
                }
                if (!File.Exists(backup)) AtomicWrite(backup, original);
                if (Hash(File.ReadAllBytes(backup)) != originalHash) throw new InvalidDataException("Проверка резервной копии не пройдена.");
                var manifest = new Dictionary<string, object> {
                    { "schema", 1 }, { "original_sha256", originalHash }, { "patched_sha256", Hash(patched) },
                    { "length", original.Length }, { "changes", changes },
                    { "version", FileVersionInfo.GetVersionInfo(executable).ProductVersion }, { "created_utc", DateTime.UtcNow.ToString("o") }
                };
                AtomicWrite(backend + ManifestSuffix, Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(manifest)));
                if (Hash(File.ReadAllBytes(backend)) != originalHash) throw new InvalidOperationException("Клиент изменился во время подготовки. Повторите после завершения обновления.");
                DateTime originalMtimeUtc = File.GetLastWriteTimeUtc(backend);
                AtomicWrite(backend, patched);
                try { File.SetLastWriteTimeUtc(backend, originalMtimeUtc); } catch { }
                AntigravityUpdateGuard.SyncSidecarMetadata(backend);
                if (Hash(File.ReadAllBytes(backend)) != Convert.ToString(manifest["patched_sha256"]))
                    throw new InvalidDataException("Проверка записанного клиента не пройдена; используйте восстановление.");
                return "Клиент подготовлен: " + Path.GetFileName(backend) + ", изменений: " + changes + ". Оригинал сохранён и проверен по SHA-256.";
            }
        }

        public static AntigravityPreparationResult PrepareForProxy(string executable) {
            try {
                // Classify compatibility using reads only. An unsupported client
                // must not require writable installation files or a binary lock.
                string backend=BackendPath(executable);
                byte[] current=File.ReadAllBytes(backend);
                var manifest=Manifest(backend);
                if(manifest!=null && Hash(current)==Convert.ToString(manifest["patched_sha256"]))
                    return new AntigravityPreparationResult(!NeedsCliUpgrade(current),Inspect(executable));
                if(IsAlreadyPreparedBinary(current) && !NeedsCliUpgrade(current)) return new AntigravityPreparationResult(true,Inspect(executable));
                ValidatePatchData(current);current=null;
                return new AntigravityPreparationResult(true,Prepare(executable));
            }
            catch(UnsupportedAntigravityBinaryException ex) {
                Logger.Log("[Antigravity compatibility] "+ex.Message+" Binary left unchanged; proxy setup may continue.");
                return new AntigravityPreparationResult(false,"Бинарная подготовка пропущена: эта версия или состояние файла не поддерживаются. Backend не изменялся.");
            }
        }

        public static string Restore(string executable)
        {
            string backend = BackendPath(executable);
            using (var guard = new FileStream(backend + ".devproxy.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                Dictionary<string, object> manifest = Manifest(backend);
                if (manifest == null) return "DevProxy не изменял этот клиент.";
                byte[] original = File.ReadAllBytes(backend + BackupSuffix);
                if (Hash(original) != Convert.ToString(manifest["original_sha256"])) throw new InvalidDataException("Резервная копия повреждена.");
                string currentHash = Hash(File.ReadAllBytes(backend));
                if (currentHash == Convert.ToString(manifest["original_sha256"])) {RecordRestored(backend,manifest);return "Оригинальный клиент уже восстановлен.";}
                if (currentHash != Convert.ToString(manifest["patched_sha256"]))
                    throw new InvalidOperationException("Клиент обновлён или изменён другим инструментом. Откат старой версии отменён.");
                EnsureNotRunning(backend);
                DateTime currentMtimeUtc = File.GetLastWriteTimeUtc(backend);
                AtomicWrite(backend, original);
                try { File.SetLastWriteTimeUtc(backend, currentMtimeUtc); } catch { }
                AntigravityUpdateGuard.SyncSidecarMetadata(backend);
                if (Hash(File.ReadAllBytes(backend)) != Convert.ToString(manifest["original_sha256"])) throw new InvalidDataException("Проверка отката не пройдена.");
                RecordRestored(backend,manifest);
                return "Оригинальный клиент восстановлен, SHA-256 совпадает.";
            }
        }
    }

    public static class AntigravityUpdateGuard
    {
        public static readonly string[] UpdaterHosts = new string[] {
            "antigravity-auto-updater-974169037036.us-central1.run.app",
            "antigravity-cli-auto-updater-974169037036.us-central1.run.app",
            "antigravity-ide-auto-updater-974169037036.us-central1.run.app",
            "antigravity-auto-updater-insider-974169037036.us-central1.run.app",
            "antigravity-hub-auto-updater-974169037036.us-central1.run.app"
        };

        public static bool IsUpdaterHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string clean = host.Trim().TrimEnd('.');
            int colon = clean.IndexOf(':');
            if (colon > 0) clean = clean.Substring(0, colon);
            return Array.Exists(UpdaterHosts, x => x.Equals(clean, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasInstalledCli(string localAppData = null, string userProfile = null)
        {
            string local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && File.Exists(Path.Combine(profile, ".gemini", "bin", "agy.exe"))) return true;
            if (!string.IsNullOrEmpty(local) && File.Exists(Path.Combine(local, "agy", "bin", "agy.exe"))) return true;
            return false;
        }

        public static bool ShouldBlockUpdaterHost(string host, bool? blockEnabled = null, string localAppData = null, string userProfile = null)
        {
            bool enabled = blockEnabled.HasValue ? blockEnabled.Value : ConfigManager.Instance.BlockAntigravityUpdates;
            if (!enabled || !IsUpdaterHost(host)) return false;
            string clean = host.Trim().TrimEnd('.');
            int colon = clean.IndexOf(':');
            if (colon > 0) clean = clean.Substring(0, colon);
            if (clean.Equals("antigravity-cli-auto-updater-974169037036.us-central1.run.app", StringComparison.OrdinalIgnoreCase) &&
                !HasInstalledCli(localAppData, userProfile))
                return false;
            return true;
        }

        public static void SyncSidecarMetadata(string backendPath)
        {
            try {
                if (string.IsNullOrEmpty(backendPath) || !File.Exists(backendPath)) return;
                if (!Path.GetFileName(backendPath).Equals("agy.exe", StringComparison.OrdinalIgnoreCase)) return;
                string sidecar = Path.Combine(Path.GetDirectoryName(backendPath), "agy.version.json");
                if (!File.Exists(sidecar)) return;
                var serializer = new JavaScriptSerializer();
                var data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(sidecar, Encoding.UTF8));
                if (data == null) return;
                var info = new FileInfo(backendPath);
                long mtimeMs = (long)Math.Round((info.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
                data["size"] = info.Length;
                data["mtimeMs"] = mtimeMs;
                string temp = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    File.WriteAllText(temp, serializer.Serialize(data), new UTF8Encoding(false));
                    File.Replace(temp, sidecar, null);
                } finally {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            } catch { }
        }

    }

    public sealed class ScopedDnsResolver
    {
        public static readonly string[] Hosts = new string[] { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" };
        // Exact names served by the configured Smart DNS. No suffix/wildcard bypass.
        public static readonly string[] GoogleDependencies = new string[] {
            "accounts.google.com", "oauth2.googleapis.com", "www.googleapis.com",
            "lh3.googleusercontent.com", "gemini.google.com", "gemini.google",
            "www.gstatic.com", "ssl.gstatic.com", "geminiweb-pa.clients6.google.com",
            "apis.google.com", "alkalimakersuite-pa.clients6.google.com", "alkalicore-pa.clients6.google.com",
            "waa-pa.clients6.google.com", "generativelanguage.googleapis.com", "proactivebackend-pa.googleapis.com",
            "robinfrontend-pa.googleapis.com", "aisandbox-pa.googleapis.com",
            "play.googleapis.com", "antigravity-unleash.goog", "content-autofill.googleapis.com", "update.googleapis.com"



        };
        public static string[] ServerCandidates(string server) {
            return DnsProviders.Addresses(server);
        }
        private readonly string _configuredServer;
        private readonly string[] _servers;
        private readonly IPEndPoint[] _testEndpoints;
        private readonly int _timeout = 1000;
        private readonly object _sync = new object();
        private readonly Dictionary<string, KeyValuePair<DateTime, IPAddress>> _cache = new Dictionary<string, KeyValuePair<DateTime, IPAddress>>();
        private readonly Dictionary<string, DateTime> _cooldown = new Dictionary<string, DateTime>();
        public ScopedDnsResolver(string server) { _configuredServer=server; _servers = ServerCandidates(server); }
        internal ScopedDnsResolver(IPEndPoint[] endpoints, int timeout) { _testEndpoints = endpoints; _servers = new string[0]; _timeout = timeout; }

        public static readonly string[] DnsOnlyGoogleDependencies = new string[] { "aicode.googleapis.com", "cloudaicompanion.googleapis.com", "cloudresourcemanager.googleapis.com", "aiplatform.googleapis.com", "developerconnect.googleapis.com" };
        public static bool IsScoped(string host) { return host != null && (Array.Exists(Hosts, x => x.Equals(host, StringComparison.OrdinalIgnoreCase)) || Array.Exists(GoogleDependencies, x => x.Equals(host, StringComparison.OrdinalIgnoreCase))); }
        public static bool IsScoped(string host, bool includeDnsOnly) { if (includeDnsOnly && host != null && host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)) return false; return IsScoped(host) || (includeDnsOnly && host != null && Array.Exists(DnsOnlyGoogleDependencies, x => x.Equals(host, StringComparison.OrdinalIgnoreCase))); }
        internal DnsEndpoint[] PrioritizeEndpoints(DnsEndpoint[] pool) {
            var ready = new List<DnsEndpoint>(); var cooling = new List<DnsEndpoint>();
            lock (_sync) foreach (var endpoint in pool) {
                DateTime until;
                bool delayed = _cooldown.TryGetValue(endpoint.Address + "/" + (endpoint.Protocol ?? "HTTPS"), out until) && until > DateTime.UtcNow;
                (delayed ? cooling : ready).Add(endpoint);
            }
            // A cooldown changes priority; it must not remove reserve nodes.
            // Keep the configured provider order for every AI hostname.
            ready.AddRange(cooling); return ready.ToArray();
        }
        private static int Read16(byte[] data, int offset) { if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("Обрезанный DNS ответ."); return (data[offset] << 8) | data[offset + 1]; }
        private static string ReadName(byte[] data, ref int offset) {
            int position = offset, end = -1; var labels = new List<string>(); var seen = new HashSet<int>();
            for (int count = 0; count < 128; count++) {
                if (position >= data.Length || !seen.Add(position)) throw new InvalidDataException("Некорректное DNS имя.");
                int size = data[position++];
                if (size == 0) { offset = end < 0 ? position : end; return string.Join(".", labels.ToArray()); }
                if ((size & 192) == 192) {
                    if (position >= data.Length) throw new InvalidDataException("Обрезанный DNS указатель.");
                    int pointer = ((size & 63) << 8) | data[position++];
                    if (pointer >= position - 2) throw new InvalidDataException("Некорректный DNS указатель.");
                    if (end < 0) end = position; position = pointer; continue;
                }
                if (size > 63 || position + size > data.Length) throw new InvalidDataException("Некорректная DNS метка.");
                labels.Add(Encoding.ASCII.GetString(data, position, size)); position += size;
            }
            throw new InvalidDataException("Слишком длинное DNS имя.");
        }
        private static byte[] ReadExact(Stream stream, int length, Stopwatch elapsed, int timeout) {
            var bytes = new byte[length]; int count = 0;
            while (count < length) {
                int remaining = timeout - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("DNS TCP timeout.");
                stream.ReadTimeout = remaining;
                int size = stream.Read(bytes, count, length - count);
                if (size == 0) throw new EndOfStreamException("Обрезанный DNS TCP ответ.");
                count += size;
            }
            return bytes;
        }
        private byte[] Exchange(IPEndPoint server, byte[] query, bool tcp) {
            if (!tcp) using (var udp = new UdpClient(server.AddressFamily)) {
                udp.Client.ReceiveTimeout = _timeout; udp.Client.SendTimeout = _timeout; udp.Connect(server);
                udp.Send(query, query.Length); IPEndPoint remote = null; return udp.Receive(ref remote);
            }
            var elapsed = Stopwatch.StartNew();
            using (var client = new TcpClient(server.AddressFamily)) {
                var pending = client.BeginConnect(server.Address, server.Port, null, null);
                using (pending.AsyncWaitHandle) {
                    if (!pending.AsyncWaitHandle.WaitOne(_timeout)) throw new TimeoutException("DNS TCP connect timeout.");
                    client.EndConnect(pending);
                }
                var stream = client.GetStream(); stream.WriteTimeout = Math.Max(1, _timeout - (int)elapsed.ElapsedMilliseconds);
                byte[] framed = new byte[query.Length + 2]; framed[0] = (byte)(query.Length >> 8); framed[1] = (byte)query.Length;
                Array.Copy(query, 0, framed, 2, query.Length); stream.Write(framed, 0, framed.Length);
                int length = Read16(ReadExact(stream, 2, elapsed, _timeout), 0);
                if (length < 12) throw new InvalidDataException("Обрезанный DNS TCP ответ.");
                return ReadExact(stream, length, elapsed, _timeout);
            }
        }
        internal static IPAddress ParseAnswer(byte[] response, byte[] transaction, string host, out int ttl) {
            ttl = 30;
            if (response.Length < 12 || response[0] != transaction[0] || response[1] != transaction[1] ||
                (response[2] & 248) != 128 || (response[2] & 2) != 0 || (response[3] & 15) != 0 || Read16(response, 4) != 1)
                throw new InvalidDataException("DNS сервер вернул ошибку или неполный ответ.");
            int offset = 12;
            if (!ReadName(response, ref offset).Equals(host, StringComparison.OrdinalIgnoreCase) ||
                Read16(response, offset) != 1 || Read16(response, offset + 2) != 1) throw new InvalidDataException("DNS вопрос не совпадает.");
            offset += 4;
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            for (int answer = 0; answer < Read16(response, 6); answer++) {
                string owner = ReadName(response, ref offset);
                int type = Read16(response, offset), dnsClass = Read16(response, offset + 2), length = Read16(response, offset + 8);
                uint lifetime = ((uint)Read16(response, offset + 4) << 16) | (uint)Read16(response, offset + 6);
                ttl = Math.Min(ttl, (int)Math.Min(30U, lifetime)); offset += 10;
                if (offset + length > response.Length) throw new InvalidDataException("Обрезанные DNS данные.");
                if (dnsClass == 1 && type == 5) {
                    int nameOffset = offset; string canonical = ReadName(response, ref nameOffset);
                    if (nameOffset != offset + length) throw new InvalidDataException("Некорректный DNS CNAME.");
                    aliases[owner] = canonical;
                } else if (dnsClass == 1 && type == 1 && length == 4) {
                    addresses[owner] = new IPAddress(new byte[] { response[offset], response[offset+1], response[offset+2], response[offset+3] });
                }
                offset += length;
            }
            string current = host; var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int hops = 0; hops < 16 && visited.Add(current); hops++) {
                IPAddress address;
                if (addresses.TryGetValue(current, out address)) {
                    byte[] bytes = address.GetAddressBytes(); byte first = bytes[0], second = bytes[1];
                    if (first == 0 || first == 10 || first == 127 || first >= 224 || (first == 192 && second == 168) ||
                        (first == 172 && second >= 16 && second <= 31) || (first == 169 && second == 254) ||
                        (first == 198 && (second == 18 || second == 19)) || (first == 100 && second >= 64 && second <= 127))
                        throw new InvalidDataException("DNS вернул локальный или зарезервированный адрес.");
                    return address;
                }
                string next; if (!aliases.TryGetValue(current, out next)) break; current = next;
            }
            throw new InvalidDataException("DNS сервер не вернул IPv4 для запрошенного имени.");
        }

        public IPAddress Resolve(string host)
        {
            // Route selection may exclude an OAuth host in DNS-only mode, but
            // resolution must accept every host admitted by either route mode.
            if (!IsScoped(host) && !IsScoped(host, true)) throw new InvalidOperationException("DNS-маршрут ограничен явным списком Google/Antigravity.");
            lock (_sync) {
                KeyValuePair<DateTime, IPAddress> cached;
                if (_cache.TryGetValue(host, out cached) && cached.Key > DateTime.UtcNow) return cached.Value;
            }
            byte[] transaction = new byte[2]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(transaction);
            byte[] query;
            using (var buffer = new MemoryStream()) {
                buffer.Write(transaction, 0, 2); buffer.Write(new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                foreach (string label in host.Split('.')) { byte[] bytes = Encoding.ASCII.GetBytes(label); buffer.WriteByte((byte)bytes.Length); buffer.Write(bytes, 0, bytes.Length); }
                buffer.Write(new byte[] { 0, 0, 1, 0, 1 }, 0, 5); query = buffer.ToArray();
            }
            var managed = _testEndpoints == null ? DnsProviders.ExtendedPool(_configuredServer) : null;
            if(managed!=null){
                var candidates=PrioritizeEndpoints(managed);
                var deadline=Stopwatch.StartNew();int remainingAttempts=candidates.Length;
                foreach(var endpoint in candidates)try{
                    int remaining=5000-(int)deadline.ElapsedMilliseconds;if(remaining<=0)break;
                    int timeout=DnsProviders.AttemptTimeout(remaining,remainingAttempts--,1600);
                    int ttl;var address=ParseAnswer(DnsHttpsTransport.Exchange(endpoint,query,Math.Min(remaining,timeout)),transaction,host,out ttl);
                    lock(_sync){_cooldown.Remove(endpoint.Address+"/"+(endpoint.Protocol??"HTTPS"));if(ttl>0)_cache[host]=new KeyValuePair<DateTime,IPAddress>(DateTime.UtcNow.AddSeconds(ttl),address);}
                    Logger.Log("[DNS] provider="+endpoint.Provider+" resolver="+endpoint.Address+"/"+(endpoint.Protocol??"HTTPS")+" host="+host+" OK");return address;
                }catch(Exception error){if(!(error is IOException)&&!(error is InvalidDataException)&&!(error is SocketException)&&!(error is TimeoutException)&&!(error is AuthenticationException))throw;lock(_sync)_cooldown[endpoint.Address+"/"+(endpoint.Protocol??"HTTPS")]=DateTime.UtcNow.AddSeconds(30);Logger.Log("[DNS] provider="+endpoint.Provider+" ("+endpoint.Address+") DoH failed: "+error.GetType().Name+" - "+error.Message);}
                throw new IOException("Зашифрованный Smart DNS (Dagami / MagicDNS) временно недоступен. Проверьте подключение.");
            }
            var endpoints = new List<IPEndPoint>();
            if (_testEndpoints != null) endpoints.AddRange(_testEndpoints);
            else foreach (string candidate in _servers) {
                IPAddress server;
                if (IPAddress.TryParse(candidate, out server)) endpoints.Add(new IPEndPoint(server, 53));
                else foreach (IPAddress address in Dns.GetHostAddresses(candidate))
                    if (address.AddressFamily == AddressFamily.InterNetwork) endpoints.Add(new IPEndPoint(address, 53));
            }
            var attempts = new List<KeyValuePair<IPEndPoint, bool>>();
            for (int pass = 0; pass < 2; pass++) foreach (IPEndPoint server in endpoints) foreach (bool tcp in new[] { false, true }) {
                string key = server + (tcp ? "/TCP" : "/UDP"); bool cooling;
                lock (_sync) { DateTime until; cooling = _cooldown.TryGetValue(key, out until) && until > DateTime.UtcNow; }
                if (cooling == (pass == 1)) attempts.Add(new KeyValuePair<IPEndPoint, bool>(server, tcp));
            }
            foreach (var attempt in attempts) {
                IPEndPoint server = attempt.Key; bool tcp = attempt.Value;
                string key = server + (tcp ? "/TCP" : "/UDP");
                try {
                    int ttl; IPAddress address = ParseAnswer(Exchange(server, query, tcp), transaction, host, out ttl);
                    lock (_sync) {
                        _cooldown.Remove(key);
                        if (ttl > 0) _cache[host] = new KeyValuePair<DateTime, IPAddress>(DateTime.UtcNow.AddSeconds(ttl), address);
                    }
                    Logger.Log("[DNS] resolver=" + key + " host=" + host + " OK"); return address;
                } catch (Exception error) {
                    if (!(error is SocketException) && !(error is IOException) && !(error is InvalidDataException) && !(error is TimeoutException)) throw;
                    lock (_sync) _cooldown[key] = DateTime.UtcNow.AddSeconds(30);
                    Logger.Log("[DNS] resolver=" + key + " host=" + host + " failed=" + error.GetType().Name);
                }
            }
            throw new IOException("Smart DNS: все настроенные DNS-узлы недоступны или вернули некорректный ответ.");
        }
    }

    public static class AntigravityDiagnostics
    {
        private static string Header(Stream stream)
        {
            string detail; return Header(stream,out detail);
        }
        private static string Header(Stream stream,out string detail)
        {
            detail="";
            var data = new List<byte>();
            while (data.Count < 32768) {
                int next = stream.ReadByte();
                if (next < 0) throw new IOException("Соединение закрыто до получения HTTP заголовка.");
                data.Add((byte)next);
                int n = data.Count;
                if (n >= 4 && data[n-4] == 13 && data[n-3] == 10 && data[n-2] == 13 && data[n-1] == 10) {
                    string[] lines=Encoding.ASCII.GetString(data.ToArray()).Split(new string[] { "\r\n" },StringSplitOptions.None);
                    foreach(string line in lines) {
                        const string upstream="X-Dagami-Upstream-Status: ";
                        const string phase="X-Dagami-Failure-Phase: ";
                        int code;
                        if(line.StartsWith(upstream,StringComparison.OrdinalIgnoreCase)&&int.TryParse(line.Substring(upstream.Length),out code)&&code>=100&&code<=599)
                            detail+=" "+UpstreamProxyDiagnostics.StatusMessage(code);
                        if(line.StartsWith(phase,StringComparison.OrdinalIgnoreCase)) {
                            string value=line.Substring(phase.Length);
                            if(Array.IndexOf(new[]{"smart-dns-resolve","smart-dns-connect","upstream-connect","upstream-CONNECT","loopback-connect"},value)>=0)detail+=" Этап: "+value+".";
                        }
                    }
                    return lines[0];
                }
            }
            throw new InvalidDataException("HTTP заголовок превышает допустимый размер.");
        }

        public static string Probe(string host, int bridgePort)
        {
            if (!ScopedDnsResolver.IsScoped(host)) throw new ArgumentException("Неподдерживаемый диагностический хост.");
            return ProbeRequest(host, "/v1internal:loadCodeAssist", "application/json", "{}", bridgePort, false);
        }

        public static string ProbeToken(int bridgePort)
        {
            // Deliberately invalid identifiers: no account state or real code is read.
            return ProbeRequest("oauth2.googleapis.com", "/token", "application/x-www-form-urlencoded",
                "grant_type=authorization_code&code=devproxy-invalid-diagnostic&client_id=devproxy-invalid-diagnostic&redirect_uri=http%3A%2F%2Flocalhost", bridgePort, true);
        }

        public static string ProbePublic(string host, int bridgePort) {
            if (!ScopedDnsResolver.IsScoped(host)) throw new ArgumentException("Неподдерживаемый диагностический хост.");
            return ProbeRequest(host, "/", null, null, bridgePort, false);
        }

        public static string ProbeUserInfo(int bridgePort) {
            // The exact endpoint used after OAuth; no account token is read/sent.
            return ProbeRequest("www.googleapis.com", "/oauth2/v2/userinfo", null, null, bridgePort, false);
        }

        private static int HttpStatus(string status) {
            string[] parts=status.Split(' '); int code;
            if(parts.Length<2 || (parts[0]!="HTTP/1.1" && parts[0]!="HTTP/1.0") || parts[1].Length!=3 ||
                !int.TryParse(parts[1],out code) || code<100 || code>599)
                throw new InvalidDataException("Некорректный статус HTTP в диагностике.");
            return code;
        }

        private static string DescribeHttpResult(string host,string path,string status,bool token) {
            int code=HttpStatus(status);
            string endpoint=host+path;
            // Successful CONNECT/TLS is not proof of a healthy inner HTTP route.
            if(code>=500 || code==407 || code==429)
                throw new IOException(endpoint+": TLS проверен, но запрос вернул HTTP "+code+". Маршрут не прошёл проверку; вход не подтверждён.");
            if(path=="/oauth2/v2/userinfo") {
                if(code!=401)throw new IOException(endpoint+": неожиданный HTTP "+code+" без токена (ожидается 401). Доступ аккаунта не проверен.");
                return endpoint+": TLS проверен; HTTP 401 ожидаем без токена. Путь userinfo доступен; вход аккаунта не проверен.";
            }
            if(token && code!=400 && code!=401)
                throw new IOException(endpoint+": неожиданный HTTP "+code+" для вымышленного OAuth-кода (ожидается 400/401).");
            return endpoint+": сертификат TLS проверен; HTTP "+code+(token
                ? ". Тест POST /token с вымышленными данными: ожидаемый отказ, вход не выполнен."
                : ". Запрос без токена аккаунта; доступ аккаунта не проверен.");
        }

        public static string LoginRouteReport(int bridgePort,bool useDns,string dnsHost,string proxy) {
            var report=new StringBuilder();
            report.AppendLine("Dagami Agent Proxy "+Brand.Version+"; FileVersion="+FileVersionInfo.GetVersionInfo(typeof(AntigravityDiagnostics).Assembly.Location).FileVersion);
            report.AppendLine("Проверяемый мост: 127.0.0.1:"+bridgePort+"; Smart DNS: "+(useDns?"включён":"выключен"));
            report.AppendLine("userinfo: GET /oauth2/v2/userinfo, без токена; ожидается HTTP 401. Этот тест не выполняет вход.");
            try {report.AppendLine(ProbeUserInfo(bridgePort));}
            catch(Exception ex) {
                report.AppendLine("ОШИБКА userinfo: "+ex.GetType().Name+": "+ex.Message);
                // Compare only synthetic diagnostics, never replay the user's
                // authenticated request and never change the running bridge.
                bool compareDns=!useDns;
                if(string.IsNullOrWhiteSpace(proxy) || (compareDns && string.IsNullOrWhiteSpace(dnsHost))) {
                    report.AppendLine("Сравнение маршрутов недоступно: нет настроенного прокси или DNS.");
                    return report.ToString();
                }
                string route=compareDns?"Smart DNS":"выбранный upstream без Smart DNS";
                report.AppendLine("Сравнение: "+route+"; отдельный временный мост, настройки не меняются.");
                try {
                    using(var comparison=new LocalProxyBridge()) {
                        if(!comparison.Start(0,proxy,compareDns?dnsHost:null))throw new IOException("Не удалось запустить временный мост.");
                        report.AppendLine(ProbeUserInfo(comparison.BoundPort));
                    }
                    report.AppendLine("Альтернативный маршрут прошёл userinfo без токена. Проверьте режим Smart DNS в «Подключение»; настройки автоматически не изменялись.");
                } catch(Exception alternative) {report.AppendLine("ОШИБКА альтернативного маршрута: "+alternative.GetType().Name+": "+alternative.Message);}
            }
            return report.ToString();
        }

        public static string TokenRouteReport(int bridgePort, bool useDns, string dnsHost, string proxy, Func<int,string> syntheticProbe = null) {
            var report = new StringBuilder("OAuth POST /token: только вымышленные диагностические данные.\r\n");
            var probe = syntheticProbe ?? ProbeToken;
            try { report.AppendLine(probe(bridgePort)); }
            catch (Exception error) {
                report.AppendLine("Ошибка текущего OAuth-маршрута: " + error.GetType().Name + ": " + error.Message);
                bool compareDns = !useDns;
                if (string.IsNullOrWhiteSpace(proxy) || (compareDns && string.IsNullOrWhiteSpace(dnsHost))) {
                    report.AppendLine("Сравнение недоступно: настройте прокси и DNS."); return report.ToString();
                }
                report.AppendLine("Сравнение OAuth: " + (compareDns ? "Smart DNS" : "выбранный upstream без Smart DNS") + "; настройки не меняются.");
                try {
                    using (var alternate = new LocalProxyBridge()) {
                        if (!alternate.Start(0, proxy, compareDns ? dnsHost : null)) throw new IOException("Не удалось открыть диагностический мост.");
                        report.AppendLine(probe(alternate.BoundPort));
                    }
                    report.AppendLine("Альтернативный OAuth-маршрут ответил. Проверьте режим Smart DNS в «Подключении». Вход аккаунта не проверен; реальные OAuth-коды не повторялись.");
                } catch (Exception alternative) { report.AppendLine("Ошибка альтернативного OAuth-маршрута: " + alternative.GetType().Name + ": " + alternative.Message); }
            }
            return report.ToString();
        }

        private static string ProbeRequest(string host, string path, string contentType, string body, int bridgePort, bool token)
        {
            using (var client = new TcpClient()) {
                IAsyncResult connection = client.BeginConnect(IPAddress.Loopback, bridgePort, null, null);
                using (connection.AsyncWaitHandle) {
                    if (!connection.AsyncWaitHandle.WaitOne(3000)) throw new TimeoutException("Локальный мост не отвечает.");
                    client.EndConnect(connection);
                }
                client.ReceiveTimeout = 20000; client.SendTimeout = 10000;
                Stream stream = client.GetStream();
                byte[] connect = Encoding.ASCII.GetBytes("CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\n\r\n");
                stream.Write(connect, 0, connect.Length);
                string detail; string status = Header(stream,out detail);
                if (HttpStatus(status)!=200)
                    throw new IOException("CONNECT "+host+": мост вернул HTTP " + HttpStatus(status) + "."+(detail.Length>0?detail:" DNS или upstream proxy не пропускает маршрут."));
                using (var tls = TlsDiagnostics.Connect(stream, host, "target-service")) {
                    byte[] request = Encoding.ASCII.GetBytes(body == null ? "GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n" : "POST " + path + " HTTP/1.1\r\nHost: " + host + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + Encoding.ASCII.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
                    tls.Write(request, 0, request.Length); tls.Flush();
                    status = Header(tls);
                    return DescribeHttpResult(host,path,status,token);
                }
            }
        }
    }
}
