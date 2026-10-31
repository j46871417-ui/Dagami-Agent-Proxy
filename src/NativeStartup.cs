using System;
using System.IO;
using System.Security.Principal;
using System.Xml;

namespace DevProxyApp {

    public static class WindowsExecutionContext {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int needed);
        [System.Runtime.InteropServices.DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [System.Runtime.InteropServices.DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
        public static int IntegrityRid() {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 8, out token)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            try {
                int needed;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out needed);
                if (needed <= 0 || needed > 65536) throw new IOException("Invalid process integrity information.");
                IntPtr buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(needed);
                try {
                    if (!GetTokenInformation(token, 25, buffer, needed, out needed)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    IntPtr sid=System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer);
                    int count=System.Runtime.InteropServices.Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if(count==0)throw new IOException("Invalid process integrity SID.");
                    return System.Runtime.InteropServices.Marshal.ReadInt32(GetSidSubAuthority(sid,count-1));
                } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            } finally { CloseHandle(token); }
        }
        public static string StartupProblem(int integrityRid) {
            if (integrityRid >= 8192) return null;
            return "Windows запустила эту копию Dagami с ограниченными правами (Low Integrity). Она не сможет сохранять файлы в обычные папки, а запущенные через неё приложения получат те же ограничения.\r\n\r\n"+
                "Закройте эту копию. Через Проводник скопируйте программу в обычную пользовательскую папку вне рабочей среды Codex либо откройте проверенную сборку из LocalAppData. Запустите её из Проводника.\r\n\r\n"+
                "Права папок и параметры Windows изменять не требуется.";
        }
    }

    public interface IStartupTaskStore {
        string Read(string name);
        void Write(string name, string xml, string userSid);
        void Delete(string name);
    }
    public sealed class WindowsStartupTaskStore : IStartupTaskStore {
        // Use the same built-in PowerShell host as DNS helpers. Task operations
        // stay hidden and inherit the current Windows identity.
        string Call(string operation,string name,string xml,string sid) {
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength=2097152 };
            string payload=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(serializer.Serialize(new { Operation=operation,Name=name,Xml=xml,Sid=sid })));
            string script=@"
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
try {
 $p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('PAYLOAD')) | ConvertFrom-Json
 $service=New-Object -ComObject Schedule.Service
 $service.Connect()
 $folder=$service.GetFolder('\')
 $xml=$null
 if($p.Operation -eq 'Read') {
  try { $task=$folder.GetTask([string]$p.Name);$xml=[string]$task.Xml }
  catch {
   $cause=$_.Exception
   while($cause.InnerException){$cause=$cause.InnerException}
   if(($cause.HResult -band 0xffff) -notin @(2,3)){throw}
  }
 } elseif($p.Operation -eq 'Write') {
  try {
   $null=$folder.RegisterTask([string]$p.Name,[string]$p.Xml,6,[string]$p.Sid,$null,3,$null)
  } catch {
   $hr=$_.Exception.HResult
   if(($hr -band 0xffff) -eq 5) { throw 'ELEVATION_REQUIRED: Access is denied when updating legacy elevated task.' }
   throw
  }
 } elseif($p.Operation -eq 'Delete') {
  try {
   $folder.DeleteTask([string]$p.Name,0)
  } catch {
   $hr=$_.Exception.HResult
   if(($hr -band 0xffff) -eq 5) { throw 'ELEVATION_REQUIRED: Access is denied when deleting legacy elevated task.' }
   throw
  }
 } else {throw 'Invalid startup operation.'}
 [pscustomobject]@{Success=$true;Xml=$xml} | ConvertTo-Json -Compress -Depth 4
} catch {
 [pscustomobject]@{Success=$false;Message=$_.Exception.Message} | ConvertTo-Json -Compress
 exit 1
}
".Replace("PAYLOAD",payload);
            var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"));
            start.Arguments="-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput=true;start.RedirectStandardError=true;start.StandardOutputEncoding=System.Text.Encoding.UTF8;
            start.WorkingDirectory=Path.GetDirectoryName(start.FileName);
            using(var process=System.Diagnostics.Process.Start(start)) {
                var output=System.Threading.Tasks.Task.Factory.StartNew(()=>process.StandardOutput.ReadToEnd());
                var errors=System.Threading.Tasks.Task.Factory.StartNew(()=>process.StandardError.ReadToEnd());
                if(!process.WaitForExit(20000)){process.Kill();throw new IOException("Планировщик не ответил вовремя. Проверьте состояние автозапуска и повторите действие.");}
                System.Threading.Tasks.Task.WaitAll(output,errors);
                if(string.IsNullOrWhiteSpace(output.Result)) throw new IOException("Планировщик завершился без отчёта. Код: "+process.ExitCode);
                var result=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(output.Result);
                if(process.ExitCode!=0 || !Convert.ToBoolean(result["Success"])) {
                    string msg = Convert.ToString(result["Message"]);
                    if (msg != null && msg.Contains("ELEVATION_REQUIRED"))
                        throw new IOException("Недостаточно прав для изменения прежнего автозапуска. Автоматическое удаление задачи отменено; требуется отдельная перенастройка с правами администратора.");
                    throw new IOException("Ошибка планировщика: " + msg);
                }
                return result.ContainsKey("Xml")?Convert.ToString(result["Xml"]):null;
            }
        }
        public string Read(string name) { string xml=Call("Read",name,null,null);return string.IsNullOrEmpty(xml)?null:xml; }
        public void Write(string name,string xml,string userSid) { Call("Write",name,xml,userSid); }
        public void Delete(string name) { Call("Delete",name,null,null); }
    }
    public sealed class NativeStartup {
        const string Namespace="http://schemas.microsoft.com/windows/2004/02/mit/task";
        const string Marker="Dagami Agent Proxy user startup v1";
        readonly IStartupTaskStore store;
        readonly string sid,exe;
        public string TaskName { get { return "DagamiAgentProxy-Startup-"+sid; } }
        public static NativeStartup Current { get { return new NativeStartup(new WindowsStartupTaskStore(),WindowsIdentity.GetCurrent().User.Value,typeof(NativeStartup).Assembly.Location); } }
        public NativeStartup(IStartupTaskStore tasks,string userSid,string executable) {
            sid=new SecurityIdentifier(userSid).Value; exe=Path.GetFullPath(executable); store=tasks;
            if(!Path.GetExtension(exe).Equals(".exe",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Для автозапуска нужен EXE программы.");
        }
        static XmlDocument Parse(string xml) {
            var doc=new XmlDocument { XmlResolver=null };
            using(var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576 })) doc.Load(reader);
            return doc;
        }
        static string Value(XmlDocument doc,XmlNamespaceManager ns,string path) { var node=doc.SelectSingleNode("/t:Task/"+path,ns);return node==null?"":node.InnerText; }
        bool UserMatches(string value) { if(string.IsNullOrEmpty(value))return false; if(string.Equals(value,sid,StringComparison.OrdinalIgnoreCase))return true; if(value.StartsWith("S-1-",StringComparison.OrdinalIgnoreCase))return false; try{return string.Equals(new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value,sid,StringComparison.OrdinalIgnoreCase);}catch{return false;} }
        bool Matches(string xml,bool currentPath,bool enabledOnly=false) {
            var doc=Parse(xml);var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t",Namespace);
            string runLevel=Value(doc,ns,"t:Principals/t:Principal/t:RunLevel"),logonType=Value(doc,ns,"t:Principals/t:Principal/t:LogonType"),triggerUser=Value(doc,ns,"t:Triggers/t:LogonTrigger/t:UserId"),command=Value(doc,ns,"t:Actions/t:Exec/t:Command").Trim().Trim('"');
            return Value(doc,ns,"t:RegistrationInfo/t:Description")==Marker &&
                UserMatches(Value(doc,ns,"t:Principals/t:Principal/t:UserId")) &&
                (string.IsNullOrEmpty(logonType) || logonType=="InteractiveToken") &&
                (string.IsNullOrEmpty(runLevel) || runLevel=="LeastPrivilege" || (!currentPath && runLevel=="HighestAvailable")) &&
                (string.IsNullOrEmpty(triggerUser) || UserMatches(triggerUser)) &&
                Value(doc,ns,"t:Actions/t:Exec/t:Arguments")=="--autostart" &&
                doc.SelectNodes("/t:Task/t:Actions/*",ns).Count==1 &&
                doc.SelectNodes("/t:Task/t:Triggers/*",ns).Count==1 &&
                (!currentPath || string.Equals(command,exe,StringComparison.OrdinalIgnoreCase)) &&
                (!enabledOnly || Value(doc,ns,"t:Settings/t:Enabled")!="false");
        }
        public bool IsEnabled() { string xml=store.Read(TaskName); return xml!=null && Matches(xml,true,true); }
        public bool HasLegacyElevatedTask() {
            string xml=store.Read(TaskName);if(xml==null||!Matches(xml,false))return false;
            var doc=Parse(xml);var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t",Namespace);
            string runLevel=Value(doc,ns,"t:Principals/t:Principal/t:RunLevel"),logonType=Value(doc,ns,"t:Principals/t:Principal/t:LogonType"),triggerUser=Value(doc,ns,"t:Triggers/t:LogonTrigger/t:UserId"),command=Value(doc,ns,"t:Actions/t:Exec/t:Command").Trim().Trim('"');
            return Value(doc,ns,"t:Principals/t:Principal/t:RunLevel")=="HighestAvailable";
        }
        public string TaskXml() {
            var doc=new XmlDocument();var task=doc.CreateElement("Task",Namespace);task.SetAttribute("version","1.2");doc.AppendChild(task);
            Action<XmlElement,string,string> text=(parent,name,value)=>{var e=doc.CreateElement(name,Namespace);e.InnerText=value;parent.AppendChild(e);};
            Func<XmlElement,string,XmlElement> node=(parent,name)=>{var e=doc.CreateElement(name,Namespace);parent.AppendChild(e);return e;};
            var info=node(task,"RegistrationInfo");text(info,"Description",Marker);
            var trigger=node(node(task,"Triggers"),"LogonTrigger");text(trigger,"Enabled","true");text(trigger,"UserId",sid);text(trigger,"Delay","PT10S");
            var principal=node(node(task,"Principals"),"Principal");principal.SetAttribute("id","User");text(principal,"UserId",sid);text(principal,"LogonType","InteractiveToken");text(principal,"RunLevel","LeastPrivilege");
            var settings=node(task,"Settings");text(settings,"MultipleInstancesPolicy","IgnoreNew");text(settings,"DisallowStartIfOnBatteries","false");text(settings,"StopIfGoingOnBatteries","false");text(settings,"StartWhenAvailable","true");text(settings,"AllowStartOnDemand","true");text(settings,"Enabled","true");text(settings,"ExecutionTimeLimit","PT0S");
            var actions=node(task,"Actions");actions.SetAttribute("Context","User");var exec=node(actions,"Exec");text(exec,"Command",exe);text(exec,"Arguments","--autostart");text(exec,"WorkingDirectory",Path.GetDirectoryName(exe));
            return doc.OuterXml;
        }
        public void SetEnabled(bool enabled) {
            string old=store.Read(TaskName);
            if(old!=null && !Matches(old,false)) throw new InvalidOperationException("Задача автозапуска изменена вне Dagami. Она не была перезаписана или удалена.");
            if(enabled) {
                if(!File.Exists(exe)) throw new FileNotFoundException("Файл программы для автозапуска не найден.");
                store.Write(TaskName,TaskXml(),sid);
                if(!IsEnabled()) throw new IOException("Автозапуск записан, но проверка задачи не прошла.");
            } else if(old!=null) { store.Delete(TaskName); if(store.Read(TaskName)!=null) throw new IOException("Удаление задачи автозапуска не подтверждено."); }
        }
    }
}
