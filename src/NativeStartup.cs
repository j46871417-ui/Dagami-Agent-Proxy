using System;
using System.IO;
using System.Security.Principal;
using System.Xml;

namespace DevProxyApp {
    public interface IStartupTaskStore {
        string Read(string name);
        void Write(string name, string xml, string userSid);
        void Delete(string name);
    }
    public sealed class WindowsStartupTaskStore : IStartupTaskStore {
        // Use the same built-in PowerShell host as DNS helpers. Task operations
        // stay hidden and inherit the current elevated Windows identity.
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
  $null=$folder.RegisterTask([string]$p.Name,[string]$p.Xml,6,[string]$p.Sid,$null,3,$null)
 } elseif($p.Operation -eq 'Delete') {
  $folder.DeleteTask([string]$p.Name,0)
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
                if(process.ExitCode!=0 || !Convert.ToBoolean(result["Success"])) throw new IOException("Ошибка планировщика: "+Convert.ToString(result["Message"]));
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
        bool Matches(string xml,bool currentPath,bool enabledOnly=false) {
            var doc=Parse(xml);var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t",Namespace);
            return Value(doc,ns,"t:RegistrationInfo/t:Description")==Marker &&
                Value(doc,ns,"t:Principals/t:Principal/t:UserId")==sid &&
                Value(doc,ns,"t:Principals/t:Principal/t:LogonType")=="InteractiveToken" &&
                Value(doc,ns,"t:Principals/t:Principal/t:RunLevel")=="HighestAvailable" &&
                Value(doc,ns,"t:Triggers/t:LogonTrigger/t:UserId")==sid &&
                Value(doc,ns,"t:Actions/t:Exec/t:Arguments")=="--autostart" &&
                doc.SelectNodes("/t:Task/t:Actions/*",ns).Count==1 &&
                doc.SelectNodes("/t:Task/t:Triggers/*",ns).Count==1 &&
                (!currentPath || string.Equals(Value(doc,ns,"t:Actions/t:Exec/t:Command"),exe,StringComparison.OrdinalIgnoreCase)) &&
                (!enabledOnly || Value(doc,ns,"t:Settings/t:Enabled")!="false");
        }
        public bool IsEnabled() { string xml=store.Read(TaskName); return xml!=null && Matches(xml,true,true); }
        public string TaskXml() {
            var doc=new XmlDocument();var task=doc.CreateElement("Task",Namespace);task.SetAttribute("version","1.2");doc.AppendChild(task);
            Action<XmlElement,string,string> text=(parent,name,value)=>{var e=doc.CreateElement(name,Namespace);e.InnerText=value;parent.AppendChild(e);};
            Func<XmlElement,string,XmlElement> node=(parent,name)=>{var e=doc.CreateElement(name,Namespace);parent.AppendChild(e);return e;};
            var info=node(task,"RegistrationInfo");text(info,"Description",Marker);
            var trigger=node(node(task,"Triggers"),"LogonTrigger");text(trigger,"Enabled","true");text(trigger,"UserId",sid);text(trigger,"Delay","PT10S");
            var principal=node(node(task,"Principals"),"Principal");principal.SetAttribute("id","User");text(principal,"UserId",sid);text(principal,"LogonType","InteractiveToken");text(principal,"RunLevel","HighestAvailable");
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
