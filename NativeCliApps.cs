using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DevProxyApp {
 public interface ICliPathEnvironment {
  string UserPath {get;set;}
  string ProcessPath {get;set;}
  string MachinePath {get;}
  void Notify();
 }
 public sealed class WindowsCliPathEnvironment : ICliPathEnvironment {
  public string UserPath {
   get {using(var k=Registry.CurrentUser.OpenSubKey("Environment"))return k==null?"":Convert.ToString(k.GetValue("Path","",RegistryValueOptions.DoNotExpandEnvironmentNames));}
   set {using(var k=Registry.CurrentUser.CreateSubKey("Environment")){var kind=k.GetValue("Path")==null?RegistryValueKind.ExpandString:k.GetValueKind("Path");k.SetValue("Path",value,kind);}}
  }
  public string ProcessPath {get{return Environment.GetEnvironmentVariable("PATH")??"";}set{Environment.SetEnvironmentVariable("PATH",value);}}
  public string MachinePath {get{return Environment.GetEnvironmentVariable("PATH",EnvironmentVariableTarget.Machine)??"";}}
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,string lParam,uint flags,uint timeout,out IntPtr result);
  public void Notify(){IntPtr result;SendMessageTimeout(new IntPtr(0xffff),0x001a,IntPtr.Zero,"Environment",2,2000,out result);}
 }
 public sealed class CliAppRecord {
  public string AppId {get;set;}
  public string Command {get;set;}
  public string Original {get;set;}
  public string Shim {get;set;}
  public string Hash {get;set;}
 }
 public sealed class CliAppState {
  public bool AddedPath {get;set;}
  public List<CliAppRecord> Apps {get;set;}
  public CliAppState(){Apps=new List<CliAppRecord>();}
 }
 public sealed class NativeCliApps {
  readonly string root,bin,journal;
  readonly ICliPathEnvironment environment;
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Dagami.CliApps.v1");
  public static NativeCliApps Current {get{return new NativeCliApps(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DagamiAgentProxy","cli"),new WindowsCliPathEnvironment());}}
  public string BinDirectory {get{return bin;}}
  public NativeCliApps(string directory,ICliPathEnvironment paths){root=Path.GetFullPath(directory);bin=Path.Combine(root,"bin");journal=Path.Combine(root,"state.dat");environment=paths;}
  static string Hash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");}
  static bool Same(string a,string b){return string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}
  static void Atomic(string path,byte[] data){string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(tmp,data);if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
  CliAppState Load(){if(!File.Exists(journal))return new CliAppState();var state=new JavaScriptSerializer().Deserialize<CliAppState>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(journal),Entropy,DataProtectionScope.CurrentUser)));if(state==null||state.Apps==null)throw new InvalidDataException("Повреждён журнал CLI.");return state;}
  void Save(CliAppState state){Directory.CreateDirectory(root);Atomic(journal,ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(state)),Entropy,DataProtectionScope.CurrentUser));}
  T Locked<T>(Func<T> action){using(var mutex=new Mutex(false,@"Local\Dagami.CliApps."+Hash(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))){bool owns=false;try{try{owns=mutex.WaitOne(10000);}catch(AbandonedMutexException){owns=true;}if(!owns)throw new IOException("Другая операция CLI ещё выполняется.");return action();}finally{if(owns)mutex.ReleaseMutex();}}}
  bool IsBin(string path){try{return Same(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')),bin);}catch{return false;}}
  string WithoutBin(string path){var parts=new List<string>();foreach(string p in (path??"").Split(';'))if(!IsBin(p))parts.Add(p);return string.Join(";",parts.ToArray());}
  bool HasBin(string path){foreach(string p in (path??"").Split(';'))if(IsBin(p))return true;return false;}
  static bool Under(string path,string folder){return Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
  public static bool IsCli(AppTarget target){
   if(target==null||!target.IsInstalled)return false;
   string ext=Path.GetExtension(target.ExePath).ToLowerInvariant();if(ext==".cmd"||ext==".bat"||ext==".ps1")return true;
   try{using(var reader=new BinaryReader(File.OpenRead(target.ExePath))){if(reader.ReadUInt16()!=0x5a4d)return false;reader.BaseStream.Position=0x3c;int pe=reader.ReadInt32();if(pe<0||pe>reader.BaseStream.Length-96)return false;reader.BaseStream.Position=pe;if(reader.ReadUInt32()!=0x4550)return false;reader.BaseStream.Position=pe+24+68;return reader.ReadUInt16()==3;}}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
  }
  public static string FindCommand(string command,string path,string exclude=null){
   foreach(string part in (path??"").Split(';')){
    if(string.IsNullOrWhiteSpace(part))continue;
    string folder=Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
    foreach(string ext in new[]{".exe",".cmd",".bat",".ps1"})try{string file=Path.GetFullPath(Path.Combine(folder,command+ext));if((exclude==null||!Under(file,exclude))&&File.Exists(file))return file;}catch(ArgumentException){}catch(NotSupportedException){}
   }return null;
  }
  public string Enable(AppTarget target,string manager){return Locked(()=>{
   if(!IsCli(target))throw new InvalidOperationException("Это приложение не распознано как CLI.");
   if(Under(target.ExePath,root))throw new InvalidOperationException("Выбран собственный CLI-запускатель. Добавьте исходный файл приложения.");
   string command=Path.GetFileNameWithoutExtension(target.ExePath);
   if(!Regex.IsMatch(command,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")||Array.Exists(new[]{"cmd","powershell","pwsh","node","python","git","dagami-proxy","dagamiagentproxy","devproxy"},x=>x.Equals(command,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Нельзя подменять эту системную команду.");
   if(FindCommand(command,environment.MachinePath)!=null)throw new InvalidOperationException("Команда найдена в системном PATH, который имеет приоритет над пользовательским. Автоматическая настройка без изменения системного PATH невозможна; используйте запуск из Dagami.");
   // Validate script support before changing PATH or writing launchers.
   CreateStart(target.ExePath,new string[0],"http://127.0.0.1:1",Environment.CurrentDirectory);
   string runner=Path.Combine(Path.GetDirectoryName(manager),"dagami-proxy.exe");
   if(!File.Exists(runner))throw new FileNotFoundException("Рядом с GUI нужен dagami-proxy.exe из того же архива.");
   if(!IsCli(new AppTarget{ExePath=runner})||FileVersionInfo.GetVersionInfo(runner).FileVersion!=FileVersionInfo.GetVersionInfo(typeof(NativeCliApps).Assembly.Location).FileVersion)throw new InvalidOperationException("Версия dagami-proxy.exe не совпадает с GUI. Распакуйте оба EXE из одного архива.");
   var state=Load();var old=state.Apps.Find(x=>x.AppId==target.Id);
   if(state.Apps.Exists(x=>x.AppId!=target.Id&&x.Command.Equals(command,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Команда уже назначена другому приложению.");
   if(old!=null&&!old.Command.Equals(command,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Имя команды изменилось. Сначала отмените прежнюю настройку.");
   string shim=Path.Combine(bin,command+".exe");
   if(File.Exists(shim)&&(old==null||Hash(File.ReadAllBytes(shim))!=old.Hash))throw new InvalidOperationException("CLI-запускатель изменён вне Dagami. Файл не перезаписан.");
   if(!File.Exists(shim)&&old!=null)throw new InvalidOperationException("CLI-запускатель удалён. Сначала отмените настройку и повторите её.");
   byte[] original=File.Exists(shim)?File.ReadAllBytes(shim):null,oldJournal=File.Exists(journal)?File.ReadAllBytes(journal):null;
   string userPath=environment.UserPath,processPath=environment.ProcessPath;
   Directory.CreateDirectory(bin);
   try{
    byte[] bytes=File.ReadAllBytes(runner);Atomic(shim,bytes);
    if(old!=null)state.Apps.Remove(old);
    state.Apps.Add(new CliAppRecord{AppId=target.Id,Command=command,Original=Path.GetFullPath(target.ExePath),Shim=shim,Hash=Hash(bytes)});
    if(!HasBin(userPath))state.AddedPath=true;
    Save(state);
    environment.UserPath=bin+";"+WithoutBin(userPath);environment.ProcessPath=bin+";"+WithoutBin(processPath);environment.Notify();
   }catch{if(original==null){if(File.Exists(shim))File.Delete(shim);}else Atomic(shim,original);if(oldJournal==null){if(File.Exists(journal))File.Delete(journal);}else Atomic(journal,oldJournal);environment.UserPath=userPath;environment.ProcessPath=processPath;throw;}
   return shim;
  });}
  public int VerifiedCount(string id,out string issue){
   issue=null;int count=0;foreach(var app in Load().Apps)if(app.AppId==id){count++;if(!Same(app.Shim,Path.Combine(bin,app.Command+".exe"))||!File.Exists(app.Shim)||Hash(File.ReadAllBytes(app.Shim))!=app.Hash)issue="CLI-запускатель удалён или изменён.";else if(!File.Exists(app.Original))issue="Исходный CLI удалён или перемещён.";else if(!HasBin(environment.UserPath))issue="Каталог CLI отсутствует в пользовательском PATH.";else{string first=FindCommand(app.Command,environment.UserPath);if(FindCommand(app.Command,environment.MachinePath)!=null||first==null||!Same(first,app.Shim))issue="Другая команда в PATH имеет приоритет перед CLI-запускателем.";}}
   return count;
  }
  public List<string> Restore(string id){return Locked(()=>{
   var state=Load();var apps=state.Apps.FindAll(x=>x.AppId==id);var result=new List<string>();
   foreach(var app in apps){if(!Same(app.Shim,Path.Combine(bin,app.Command+".exe")))throw new InvalidDataException("Некорректный путь CLI-запускателя.");if(File.Exists(app.Shim)&&Hash(File.ReadAllBytes(app.Shim))!=app.Hash)throw new InvalidOperationException("CLI-запускатель изменён вне Dagami. Откат остановлен.");}
   byte[] oldJournal=File.Exists(journal)?File.ReadAllBytes(journal):null;string userPath=environment.UserPath,processPath=environment.ProcessPath;var removed=new Dictionary<string,byte[]>();
   try{foreach(var app in apps){if(File.Exists(app.Shim)){removed[app.Shim]=File.ReadAllBytes(app.Shim);File.Delete(app.Shim);}state.Apps.Remove(app);result.Add(app.Shim);}if(state.Apps.Count==0&&state.AddedPath){environment.UserPath=WithoutBin(userPath);environment.ProcessPath=WithoutBin(processPath);state.AddedPath=false;}Save(state);environment.Notify();}
   catch{foreach(var pair in removed)Atomic(pair.Key,pair.Value);if(oldJournal!=null)Atomic(journal,oldJournal);environment.UserPath=userPath;environment.ProcessPath=processPath;throw;}return result;
  });}
  public static bool IsShimLocation(){string folder=Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);return Path.GetFileName(folder).Equals("bin",StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(Path.GetDirectoryName(folder),"state.dat"));}
  public int RefreshRunner(string runner,string backupDirectory=null){return Locked(()=>{
   var state=Load();if(state.Apps.Count==0)return 0;
   byte[] bytes=File.ReadAllBytes(runner);string next=Hash(bytes);var old=new Dictionary<string,byte[]>();
   foreach(var app in state.Apps){if(!Same(app.Shim,Path.Combine(bin,app.Command+".exe"))||!File.Exists(app.Shim)||Hash(File.ReadAllBytes(app.Shim))!=app.Hash)throw new IOException("CLI-запускатель изменён: "+app.Command+". Обновление отменено.");old[app.Shim]=File.ReadAllBytes(app.Shim);}
   string backup=backupDirectory??Path.Combine(root,"backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);byte[] journalBefore=File.ReadAllBytes(journal);File.WriteAllBytes(Path.Combine(backup,"state.dat"),journalBefore);foreach(var pair in old)File.WriteAllBytes(Path.Combine(backup,Path.GetFileName(pair.Key)),pair.Value);
   var changed=new List<string>();
   try{foreach(var app in state.Apps){if(Hash(File.ReadAllBytes(app.Shim))!=app.Hash)throw new IOException("CLI-запускатель изменился во время обновления.");Atomic(app.Shim,bytes);changed.Add(app.Shim);app.Hash=next;}Save(state);return changed.Count;}
   catch(Exception error){var failed=new List<string>();foreach(string path in changed)try{if(Hash(File.ReadAllBytes(path))!=next)throw new IOException("Чужое изменение");Atomic(path,old[path]);}catch{failed.Add(Path.GetFileName(path));}Atomic(journal,journalBefore);if(failed.Count>0)throw new IOException("Не удалось откатить CLI: "+string.Join(", ",failed.ToArray())+". Копии: "+backup,error);throw;}
  });}
  public static bool TryRunShim(string[] args){
   string current=Process.GetCurrentProcess().MainModule.FileName,folder=Path.GetDirectoryName(current);
   if(!Path.GetFileName(folder).Equals("bin",StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(Path.GetDirectoryName(folder),"state.dat")))return false;
   var store=new NativeCliApps(Path.GetDirectoryName(folder),new WindowsCliPathEnvironment());
   var app=store.Load().Apps.Find(x=>Same(x.Shim,current));
   if(app==null)throw new InvalidOperationException("Для этого CLI-запускателя нет сохранённой настройки. Повторите «Пропатчить» в Dagami.");
   Console.OutputEncoding=new UTF8Encoding(false);
   if(Hash(File.ReadAllBytes(current))!=app.Hash)throw new InvalidOperationException("CLI-запускатель не соответствует сохранённой настройке.");
   if(!LicenseManager.IsActivated())throw new InvalidOperationException("Dagami не активирован. Откройте GUI для активации.");
   var config=ConfigManager.Instance;
   Environment.ExitCode=Run(app.Original,args,config.LastProxy,config.UseDagamiDns?config.DnsHost:null);
   return true;
  }
  static string Join(string[] args){return string.Join(" ",Array.ConvertAll(args,NativeShortcuts.Quote));}
  public static ProcessStartInfo CreateStart(string executable,string[] args,string proxy,string directory){
   string file=executable,arguments=Join(args),ext=Path.GetExtension(file).ToLowerInvariant();
   if(ext==".cmd"||ext==".bat"||ext==".ps1"){
    string source=File.ReadAllText(file);
    var npm=Regex.Match(source,@"[""'](?:%dp0%[\\/]|\$basedir[\\/])([^""'\r\n]+\.(?:[cm]?js))[""']",RegexOptions.IgnoreCase);
    if(npm.Success){string entry=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file),npm.Groups[1].Value.Replace('/',Path.DirectorySeparatorChar)));if(!Under(entry,Path.GetDirectoryName(file))||!File.Exists(entry))throw new FileNotFoundException("Не найден Node entrypoint CLI.");string node=Path.Combine(Path.GetDirectoryName(file),"node.exe");if(!File.Exists(node))node=FindCommand("node",Environment.GetEnvironmentVariable("PATH"));if(node==null)throw new FileNotFoundException("Не найден node.exe для CLI.");file=node;arguments=NativeShortcuts.Quote(entry)+" "+arguments;}
    else if(ext==".ps1"){file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");arguments="-NoLogo -NoProfile -File "+NativeShortcuts.Quote(executable)+" "+arguments;}
    else throw new InvalidOperationException("Эта CMD/BAT-обёртка не распознана. Выберите исходный EXE или стандартный npm CMD/PS1. Скрипт не изменён.");
   }
   var start=new ProcessStartInfo(file,arguments){UseShellExecute=false,CreateNoWindow=false,WorkingDirectory=directory};
   foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy"})start.EnvironmentVariables[key]=proxy;
   start.EnvironmentVariables["NO_PROXY"]=start.EnvironmentVariables["no_proxy"]="localhost,127.0.0.1,::1";
   start.EnvironmentVariables["AGY_PROXY_URL"]=start.EnvironmentVariables["agy_proxy_url"]=start.EnvironmentVariables["AG_LS_PROXY"]=proxy;
   start.EnvironmentVariables["NODE_USE_ENV_PROXY"]="1";
   return start;
  }
  public static int Run(string executable,string[] args,string upstream,string dnsHost){
   Uri uri;if(!Uri.TryCreate(upstream,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https"))throw new ArgumentException("Для CLI сначала настройте HTTP/HTTPS прокси в Dagami.");
   using(var activity=NativeUpdates.AcquireActivity(true))using(var bridge=new LocalProxyBridge()){
    if(!bridge.Start(0,upstream,dnsHost))throw new IOException("Не удалось запустить локальный мост CLI.");
    using(var job=new JobObjectManager())using(var child=Process.Start(CreateStart(executable,args,"http://127.0.0.1:"+bridge.BoundPort,Environment.CurrentDirectory))){
     if(child==null)throw new IOException("Процесс CLI не создан.");
     if(!job.AssignProcess(child.Handle))Logger.Log("[CLI] Job Object unavailable; descendant cleanup is not guaranteed.");
     ConsoleCancelEventHandler cancel=(s,e)=>{e.Cancel=true;};Console.CancelKeyPress+=cancel;
     try{child.WaitForExit();return child.ExitCode;}finally{Console.CancelKeyPress-=cancel;}
    }
   }
  }
 }
}
