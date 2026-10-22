using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DevProxyApp {
 public sealed class UpdatePlan {
  public string Stage{get;set;}public string GuiPath{get;set;}public string CliPath{get;set;}
  public string OldGuiHash{get;set;}public string OldCliHash{get;set;}
  public UpdateRelease Release{get;set;}public int ParentId{get;set;}public long ParentStartTicks{get;set;}
  public bool Restart{get;set;}public bool RefreshCli{get;set;}
 }
 public sealed class UpdateResult {public bool Success{get;set;}public string Message{get;set;}public string Backup{get;set;}public DateTime Utc{get;set;}}
 public static class UpdateInstaller {
  public static string ResultPath {get{return Path.Combine(NativeUpdates.Root,"last-result.json");}}
  public static UpdateResult LastResult(){try{return new JavaScriptSerializer().Deserialize<UpdateResult>(File.ReadAllText(ResultPath));}catch{return null;}}
  public static bool CanWrite(string directory){string probe=Path.Combine(directory,".dagami-write-"+Guid.NewGuid().ToString("N"));try{using(File.Create(probe)){}File.Delete(probe);return true;}catch(UnauthorizedAccessException){return false;}catch(IOException){return false;}}
  public static UpdatePlan CreatePlan(string stage,string guiPath,bool restart){
   if(NativeUpdates.CliBusy())throw new IOException("Работает CLI через Dagami. Завершите его перед установкой.");
   var release=NativeUpdates.ReadProtected<UpdateRelease>(Path.Combine(stage,"release.dat"));NativeUpdates.ValidatePayload(release,stage);
   if(!NativeUpdates.IsNewer(release))throw new InvalidOperationException("Этот релиз не новее установленной сборки.");
   // Every attempt has fresh readiness/cancellation markers; retrying never
   // observes a previous helper's ready file or overwrites a running helper.
   string attempt=Path.Combine(NativeUpdates.Root,"staged",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(attempt);
   foreach(string name in new[]{"DagamiAgentProxy.exe","release.dat"})File.Copy(Path.Combine(stage,name),Path.Combine(attempt,name));
   var process=Process.GetCurrentProcess();
   return new UpdatePlan{Stage=attempt,GuiPath=Path.GetFullPath(guiPath),OldGuiHash=NativeUpdates.FileHash(guiPath),Release=release,ParentId=process.Id,ParentStartTicks=process.StartTime.ToUniversalTime().Ticks,Restart=restart,RefreshCli=true};
  }
  static void ValidatePaths(UpdatePlan plan){
   if(plan==null||string.IsNullOrEmpty(plan.Stage)||string.IsNullOrEmpty(plan.GuiPath))throw new InvalidDataException("Неполный план обновления.");
   string directory=Path.GetDirectoryName(Path.GetFullPath(plan.GuiPath));
   if(!Path.GetExtension(plan.GuiPath).Equals(".exe",StringComparison.OrdinalIgnoreCase)||string.Equals(directory,Path.GetFullPath(plan.Stage),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Некорректные пути программы и загрузки.");
  }
  static void Replace(string source,string target){string temp=Path.Combine(Path.GetDirectoryName(target),".dagami-new-"+Guid.NewGuid().ToString("N")+".exe");try{File.Copy(source,temp);if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);}finally{if(File.Exists(temp))File.Delete(temp);}}
  static void CheckOld(string path,string expected){if(expected==null){if(File.Exists(path))throw new IOException("Целевой файл появился после подготовки обновления.");}else if(!File.Exists(path)||NativeUpdates.FileHash(path)!=expected)throw new IOException("Установленные файлы изменились после подготовки. Повторите проверку обновления.");}
  public static string InstallCore(UpdatePlan plan,Action<int> checkpoint,Action refreshCli){
   ValidatePaths(plan);NativeUpdates.ValidatePayload(plan.Release,plan.Stage);CheckOld(plan.GuiPath,plan.OldGuiHash);
   string backup=Path.Combine(plan.Stage,"backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
   string[] targets={plan.GuiPath},names={"DagamiAgentProxy.exe"};string[] originals={plan.OldGuiHash};
   for(int i=0;i<targets.Length;i++)if(originals[i]!=null)File.Copy(targets[i],Path.Combine(backup,names[i]));
   int changed=0;
   try{
    for(int i=0;i<targets.Length;i++){if(checkpoint!=null)checkpoint(i);CheckOld(targets[i],originals[i]);Replace(Path.Combine(plan.Stage,names[i]),targets[i]);changed++;}
    if(NativeUpdates.FileHash(plan.GuiPath)!=plan.Release.Gui.Sha256.ToLowerInvariant())throw new IOException("Проверка файла после записи не пройдена.");
    if(refreshCli!=null)refreshCli();return backup;
   }catch(Exception error){var rollbackErrors=new List<string>();for(int i=changed-1;i>=0;i--)try{string applied=i==0?plan.Release.Gui.Sha256:plan.Release.Cli.Sha256;if(!File.Exists(targets[i])||NativeUpdates.FileHash(targets[i])!=applied.ToLowerInvariant())throw new IOException("Файл изменён после записи; откат не перезаписывает чужие изменения.");if(originals[i]==null)File.Delete(targets[i]);else Replace(Path.Combine(backup,names[i]),targets[i]);}catch(Exception ex){rollbackErrors.Add(Path.GetFileName(targets[i])+": "+ex.GetType().Name);}
    if(rollbackErrors.Count>0)throw new IOException("Обновление не установлено; откат требует проверки. Копии: "+backup+". "+string.Join("; ",rollbackErrors.ToArray()),error);
    throw new IOException("Обновление отменено, прежний файл программы восстановлен. "+error.Message,error);
   }
  }
  public static string PrepareHelper(UpdatePlan plan){
   ValidatePaths(plan);string file=Path.Combine(plan.Stage,"plan.dat");NativeUpdates.SaveProtected(file,plan);
   string runner=typeof(UpdateInstaller).Assembly.Location;
   if(!File.Exists(runner))throw new IOException("Не найден файл программы для обновления.");
   File.Copy(runner,Path.Combine(plan.Stage,"dagami-update-helper.exe"),true);return file;
  }
  public static Process StartHelper(UpdatePlan plan,string file){var start=new ProcessStartInfo(Path.Combine(plan.Stage,"dagami-update-helper.exe"),"--update-helper "+NativeShortcuts.Quote(file));if(CanWrite(Path.GetDirectoryName(plan.GuiPath))){start.UseShellExecute=false;start.CreateNoWindow=true;}else{start.UseShellExecute=true;start.Verb="runas";start.WindowStyle=ProcessWindowStyle.Hidden;}return Process.Start(start);}
  static bool ParentAlive(UpdatePlan plan){try{using(var parent=Process.GetProcessById(plan.ParentId))return !parent.HasExited&&parent.StartTime.ToUniversalTime().Ticks==plan.ParentStartTicks;}catch(ArgumentException){return false;}}
  static void SaveResult(UpdateResult result){NativeUpdates.Atomic(ResultPath,Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(result)));}
  public static int RunHelper(string planPath){
   UpdatePlan plan=null;bool installed=false;string backup=null;
   try{
    string path=Path.GetFullPath(planPath),prefix=Path.GetFullPath(Path.Combine(NativeUpdates.Root,"staged")).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(path)!="plan.dat")throw new InvalidDataException("План должен находиться в каталоге загрузки Dagami.");
    plan=NativeUpdates.ReadProtected<UpdatePlan>(path);ValidatePaths(plan);
    if(!string.Equals(Path.GetDirectoryName(path),Path.GetFullPath(plan.Stage),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Каталог плана не совпадает со скачанными файлами.");
    NativeUpdates.ValidatePayload(plan.Release,plan.Stage);
    NativeUpdates.Atomic(Path.Combine(plan.Stage,"ready"),Encoding.ASCII.GetBytes("ready"));
    var clock=Stopwatch.StartNew();while(ParentAlive(plan)){if(File.Exists(Path.Combine(plan.Stage,"cancel")))throw new IOException("Установка отменена пользователем.");if(clock.Elapsed.TotalSeconds>60)throw new IOException("Программа не завершилась. Принудительная остановка не выполнялась.");Thread.Sleep(100);}
    if(File.Exists(Path.Combine(plan.Stage,"cancel")))throw new IOException("Установка отменена.");
    using(NativeUpdates.ActivityLock("activity.lock",true)){
     if(NativeUpdates.CliBusy())throw new IOException("Работает CLI через Dagami. Установка отложена; процессы не завершались.");
     backup=InstallCore(plan,null,plan.RefreshCli?(Action)(()=>NativeCliApps.Current.RefreshRunner(Path.Combine(plan.Stage,"DagamiAgentProxy.exe"),Path.Combine(plan.Stage,"cli-backup"))):null);installed=true;
    }
    SaveResult(new UpdateResult{Success=true,Message="Установлено "+plan.Release.Tag+". Резервная копия: "+backup,Backup=backup,Utc=DateTime.UtcNow});
   }catch(Exception ex){try{SaveResult(new UpdateResult{Success=false,Message=ex.Message,Backup=backup,Utc=DateTime.UtcNow});}catch{} }
   if(plan!=null&&plan.Restart&&File.Exists(plan.GuiPath))try{Process.Start(new ProcessStartInfo(plan.GuiPath){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(plan.GuiPath)});}catch(Exception ex){try{SaveResult(new UpdateResult{Success=installed,Message=(installed?"Обновление установлено. ":"")+"Автозапуск не удался: "+ex.GetType().Name+". Запустите GUI вручную.",Backup=backup,Utc=DateTime.UtcNow});}catch{}}
   return installed?0:1;
  }
 }
}
