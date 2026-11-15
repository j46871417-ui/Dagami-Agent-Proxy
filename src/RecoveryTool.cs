using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DevProxyApp {
 public sealed class RecoveryStep {
  public string Name; public Action Restore;
  public RecoveryStep(string name,Action restore) {Name=name;Restore=restore;}
 }
 public sealed class RecoveryReport {
  public readonly List<string> Completed=new List<string>();
  public readonly List<string> Errors=new List<string>();
  public bool Success {get{return Errors.Count==0;}}
  public override string ToString() {return (Success?"Восстановление завершено.":"Восстановление требует внимания. Удаление отменено.")+"\r\n\r\n"+string.Join("\r\n",Completed)+
   (Errors.Count==0?"":"\r\n\r\nНе выполнено:\r\n"+string.Join("\r\n",Errors))+"\r\n\r\nОткрытые приложения не завершались. Перезапустите их, чтобы убрать уже унаследованные настройки прокси.";}
 }
 public static class RecoveryTool {
  public static RecoveryReport Execute(IEnumerable<RecoveryStep> steps,Action<string> progress=null) {
   var report=new RecoveryReport();
   foreach(var step in steps) {
    try {if(progress!=null)progress(step.Name);step.Restore();report.Completed.Add("✓ "+step.Name);}
    catch(Exception failure){report.Errors.Add(step.Name+": "+BugReports.Clean(failure.Message));}
   }
   return report;
  }
  public static List<RecoveryStep> Plan(bool uninstall) {
   var config=ConfigManager.Instance;var steps=new List<RecoveryStep>();
   string loadError=config.LastLoadError;
   if(!string.IsNullOrEmpty(loadError))steps.Add(new RecoveryStep("Журнал настроек Dagami",()=>{throw new IOException("Настройки повреждены или недоступны. Исходный файл сохранён; автоматическое удаление запрещено.");}));
   steps.Add(new RecoveryStep("Прокси Windows",()=>WindowsSelectiveProxy.Disable()));
   if(config.EnvironmentBackup.Count>0)steps.Add(new RecoveryStep("Переменные прокси Windows",()=>ProxyEngine.ClearSystemEnvironment()));
   steps.Add(new RecoveryStep("Сохранённые настройки приложений",RestoreApps));
   steps.Add(new RecoveryStep("Собственные настройки DNS",()=> {
    if(SystemDnsManager.NeedsRestore())SystemDnsManager.Execute(new SystemDnsRequest {JobId=Guid.NewGuid().ToString("N"),Operation="Restore",Adapters=new SystemDnsAdapter[0]});
   }));
   steps.Add(new RecoveryStep("Прямой маршрут в Dagami",()=>{if(!string.IsNullOrEmpty(loadError))throw new IOException("Повреждённый конфиг сохранён для восстановления.");config.ChangeConnection(c=>{c.UseProxy=false;c.UseDagamiDns=false;});}));
   if(uninstall) {
    steps.Add(new RecoveryStep("Автозапуск Dagami",()=>NativeStartup.Current.SetEnabled(false)));
    steps.Add(new RecoveryStep("Служба DNS Dagami",RemoveDnsService));
   }
   return steps;
  }
  static void RemoveDnsService() {
   if(!new WindowsDnsServiceControl().Exists())return;
   bool admin=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
   if(admin){DnsServiceHost.RemoveOwned();return;}
   var start=new ProcessStartInfo(typeof(RecoveryTool).Assembly.Location,"--remove-dns-service"){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
   using(var helper=Process.Start(start)) {
    if(helper==null||!helper.WaitForExit(90000))throw new IOException("Удаление службы не подтверждено. Настройки и файлы сохранены.");
    if(helper.ExitCode!=0)throw new IOException("Служба DNS не удалена: проверьте откат DNS и права администратора. Файлы сохранены.");
   }
  }
  static void RestoreApps() {
   var targets=new Dictionary<string,AppTarget>();var errors=new List<string>();
   foreach(var target in ProxyEngine.ScanAllTargets())targets[target.Id]=target;
   var shortcuts=NativeShortcuts.Current;var cli=NativeCliApps.Current;
   var recorded=new HashSet<string>();
   foreach(var target in shortcuts.RegisteredTargets()) {recorded.Add(target.Id);if(!targets.ContainsKey(target.Id))targets[target.Id]=target;}
   foreach(var target in cli.RegisteredTargets()) {recorded.Add(target.Id);if(!targets.ContainsKey(target.Id))targets[target.Id]=target;}
   foreach(var target in targets.Values) {
    bool backup=!string.IsNullOrEmpty(target.ConfigPath)&&File.Exists(target.ConfigPath+".devproxy.bak");
    bool binary=target.IsInstalled&&AntigravityIntegration.Supports(target.ExePath)&&AntigravityIntegration.BackendPaths(target.ExePath).Exists(AntigravityIntegration.HasActivePatchRecord);
    if(!recorded.Contains(target.Id)&&!backup&&!binary)continue;
    try {NativeAppPatcher.RestoreWithLauncher(target,()=>NativeCliApps.IsCommandTarget(target)?cli.Restore(target.Id):shortcuts.Restore(target.Id));}
    catch(Exception failure){errors.Add(target.DisplayName+": "+failure.Message);}
   }
   if(errors.Count>0)throw new IOException(string.Join("; ",errors));
  }
  public static bool Opens(string image,string[] args) {
   // Elevated DNS/update helpers must be dispatched normally even when launched
   // from a recovery alias of the same binary.
   return IsCommand(args)||((args==null||args.Length==0)&&
    (image.Equals("DagamiRecovery",StringComparison.OrdinalIgnoreCase)||image.Equals("DagamiUninstall",StringComparison.OrdinalIgnoreCase)));
  }
  public static bool IsCommand(string[] args) {
   if(args==null||args.Length==0)return false;
   return args[0]=="--recover"||args[0]=="recovery"||args[0]=="--restore-all"||args[0]=="--uninstall";
  }
  public static void Run(string[] args,bool uninstall=false) {
   uninstall=uninstall||(args!=null&&args.Length>0&&args[0]=="--uninstall");
   if(args!=null&&Array.IndexOf(args,"--dry-run")>=0) {
    Program.EnsureRecoveryConsole();
    Console.WriteLine(new JavaScriptSerializer().Serialize(new {requiresKey=false,uninstall=uninstall,steps=Plan(uninstall).ConvertAll(s=>s.Name)}));return;
   }
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   using(var window=new RecoveryForm(uninstall))Application.Run(window);
  }
  public static IDisposable AcquireIdle() {
   var mutex=new Mutex(false,@"Local\"+NativeLaunchBroker.UserName);bool owned=false;
   try {
    try {owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
    if(!owned) {
     string error=NativeLaunchBroker.Send(NativeLaunchBroker.UserName,new NativeLaunchRequest {Recovery=true},3000);
     if(!string.IsNullOrEmpty(error))throw new IOException(error);
     try {owned=mutex.WaitOne(12000);}catch(AbandonedMutexException){owned=true;}
     if(!owned)throw new IOException("Dagami ещё завершает операцию. Повторите восстановление после её окончания.");
    }
    if(NativeUpdates.CliBusy())throw new IOException("Работает CLI через Dagami. Завершите его перед восстановлением. Процесс не остановлен принудительно.");
    return new IdleLease(mutex);
   }catch {if(owned)mutex.ReleaseMutex();mutex.Dispose();throw;}
  }
  sealed class IdleLease:IDisposable {
   Mutex mutex;public IdleLease(Mutex value){mutex=value;}
   public void Dispose(){if(mutex!=null){mutex.ReleaseMutex();mutex.Dispose();mutex=null;}}
  }
 }
 public sealed class UninstallFile {public string Path{get;set;} public string Sha256{get;set;}}
 public sealed class UninstallPlan {
  public string Directory{get;set;} public List<UninstallFile> Files{get;set;}
  public int ParentId{get;set;}public long ParentStartTicks{get;set;}
 }
 public static class OwnedUninstaller {
  static readonly string[] Names={"DagamiAgentProxy.exe","DagamiRecovery.exe","DagamiUninstall.exe"};
  public static void Validate(UninstallPlan plan) {
   if(plan==null||plan.Files==null||plan.Files.Count==0||plan.Files.Count>3||plan.ParentId<=0)throw new InvalidDataException("Некорректный план удаления.");
   string root=Path.GetFullPath(plan.Directory).TrimEnd(Path.DirectorySeparatorChar);
   var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var file in plan.Files) {
    string path=Path.GetFullPath(file.Path);
    if(!string.Equals(Path.GetDirectoryName(path),root,StringComparison.OrdinalIgnoreCase)||!Array.Exists(Names,n=>n.Equals(Path.GetFileName(path),StringComparison.OrdinalIgnoreCase))||!unique.Add(path)||
      !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256??"",@"\A[a-f0-9]{64}\z"))throw new InvalidDataException("План содержит посторонний файл. Удаление запрещено.");
   }
  }
  public static UninstallPlan Create(string executable) {
   string directory=Path.GetDirectoryName(Path.GetFullPath(executable));string hash=NativeUpdates.FileHash(executable);var process=Process.GetCurrentProcess();
   var plan=new UninstallPlan {Directory=directory,Files=new List<UninstallFile>(),ParentId=process.Id,ParentStartTicks=process.StartTime.ToUniversalTime().Ticks};
   foreach(string name in Names) {string path=Path.Combine(directory,name);if(!File.Exists(path))continue;
    if(NativeUpdates.FileHash(path)!=hash)throw new IOException("Компонент "+name+" отличается от этого пакета. Автоматическое удаление отменено; данные восстановления сохранены.");
    plan.Files.Add(new UninstallFile {Path=path,Sha256=hash});
   }
   Validate(plan);return plan;
  }
  public static void Start(UninstallPlan plan) {
   Validate(plan);
   if(!UpdateInstaller.CanWrite(plan.Directory))throw new IOException("Нет прав удаления из папки приложения. Восстановление выполнено; удалите пакет вручную или запустите удаление с нужными правами.");
   string folder=Path.Combine(Path.GetTempPath(),"Dagami-uninstall-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(folder);
   string data=Path.Combine(folder,"plan.json"),script=Path.Combine(folder,"cleanup.ps1");
   File.WriteAllText(data,new JavaScriptSerializer().Serialize(plan));
   File.WriteAllText(script,@"param([string]$PlanPath)
$ErrorActionPreference='Stop'
try {
$p=Get-Content -Raw -Encoding UTF8 -LiteralPath $PlanPath | ConvertFrom-Json
$root=[IO.Path]::GetFullPath($p.Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$allowed=@('DagamiAgentProxy.exe','DagamiRecovery.exe','DagamiUninstall.exe')
if(@($p.Files).Count -lt 1 -or @($p.Files).Count -gt 3){throw 'Invalid cleanup plan'}
$parent=Get-Process -Id $p.ParentId -ErrorAction SilentlyContinue
if($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $p.ParentStartTicks){Wait-Process -Id $p.ParentId -Timeout 60}
foreach($f in $p.Files){
 $path=[IO.Path]::GetFullPath($f.Path)
 if([IO.Path]::GetDirectoryName($path) -ne $root -or [IO.Path]::GetFileName($path) -notin $allowed){throw 'Foreign cleanup path'}
 if(Test-Path -LiteralPath $path){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $f.Sha256){throw 'File changed; cleanup cancelled'}}
}
foreach($f in $p.Files){if(Test-Path -LiteralPath $f.Path){Remove-Item -LiteralPath $f.Path -Force}}
Remove-Item -LiteralPath $PlanPath -Force
Remove-Item -LiteralPath $PSCommandPath -Force
} catch {
 $message='Не удалось завершить удаление файлов Dagami: '+$_.Exception.Message
 try {[IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($PlanPath)) 'error.txt'),$message,[Text.Encoding]::UTF8)}catch{}
 try {Add-Type -AssemblyName System.Windows.Forms;[Windows.Forms.MessageBox]::Show($message,'Удаление Dagami')|Out-Null}catch{}
 exit 1
}
",new System.Text.UTF8Encoding(true));
   string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   // The helper only deletes reviewed files in one exact directory, never recursively.
   var start=new ProcessStartInfo(powershell,"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "+NativeShortcuts.Quote(script)+" -PlanPath "+NativeShortcuts.Quote(data)) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
   start.EnvironmentVariables["PSModulePath"]=Path.Combine(Path.GetDirectoryName(powershell),"Modules");
   using(var helper=Process.Start(start)) {if(helper==null)throw new IOException("Не удалось запустить помощник удаления.");}
  }
 }
 internal sealed class RecoveryForm:Form {
  readonly bool uninstall; readonly TextBox report;readonly Button action; bool busy;
  public RecoveryForm(bool remove) {
   uninstall=remove;Text=uninstall?"Удаление Dagami":"Восстановление интернета — Dagami";Size=new Size(760,540);MinimumSize=new Size(540,410);StartPosition=FormStartPosition.CenterScreen;
   BackColor=FluentTheme.Background;ForeColor=FluentTheme.Text;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
   var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=3};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   layout.RowStyles.Add(new RowStyle(SizeType.Absolute,76));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
   layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text=uninstall?"Сначала вернём внесённые Dagami настройки, затем удалим файлы приложения. Личные файлы и история восстановления сохранятся.":"Вернём настройки приложений, собственные DNS-правила и прокси Windows. Ключ и подключение к серверу не требуются. Открытые IDE останутся запущенными."},0,0);
   report=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=FluentTheme.Surface,ForeColor=FluentTheme.Text};layout.Controls.Add(report,0,1);
   var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};
   action=new FluentButton {Text=uninstall?"Восстановить и удалить Dagami":"Восстановить обычный интернет",AutoSize=true,Primary=true};action.Click+=async(s,e)=>await Restore();
   var close=new FluentButton {Text="Закрыть",AutoSize=true};close.Click+=(s,e)=>Close();buttons.Controls.Add(action);buttons.Controls.Add(close);layout.Controls.Add(buttons,0,2);Controls.Add(layout);FormClosing+=(s,e)=>{if(busy&&e.CloseReason==CloseReason.UserClosing)e.Cancel=true;};
  }
  async Task Restore() {
   if(uninstall&&MessageBox.Show(this,"Восстановить внесённые настройки и удалить исполняемые файлы Dagami? При незавершённом откате удаление не начнётся.",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
   action.Enabled=false;busy=true;
   try {
    var result=await Task.Run(()=>{using(RecoveryTool.AcquireIdle())return RecoveryTool.Execute(RecoveryTool.Plan(uninstall),name=>{try{BeginInvoke(new Action(()=>report.AppendText(name+"…\r\n")));}catch(InvalidOperationException){}});});
    if(IsDisposed)return;report.Text=result.ToString();
    if(uninstall&&result.Success) {
     if(DnsServiceHost.IsRunning())throw new IOException("Служба DNS ещё работает. Восстановите прежний DNS и повторите удаление; файлы не удалены.");
     OwnedUninstaller.Start(OwnedUninstaller.Create(typeof(RecoveryTool).Assembly.Location));busy=false;Close();Application.Exit();
    }
   }catch(Exception failure){if(!IsDisposed)report.Text=BugReports.Clean(failure.Message);}
   finally{busy=false;if(!IsDisposed)action.Enabled=true;}
  }
 }
}
