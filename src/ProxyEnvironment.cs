using System;
using System.Collections.Generic;
using System.IO;

namespace DevProxyApp {
 public interface IProxyEnvironmentStore {
  string Read(string name, EnvironmentVariableTarget target);
  void Write(string name, string value, EnvironmentVariableTarget target);
  void Notify();
 }
 public sealed class WindowsProxyEnvironmentStore : IProxyEnvironmentStore {
  public string Read(string name, EnvironmentVariableTarget target) { return Environment.GetEnvironmentVariable(name,target); }
  public void Write(string name,string value,EnvironmentVariableTarget target) { Environment.SetEnvironmentVariable(name,value,target); }
  public void Notify() { ProxyEngine.BroadcastProxyEnvironmentChange(); }
 }
 public static class ProxyEnvironment {
  public static readonly string[] Names={"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","AGY_PROXY_URL","NO_PROXY"};
  static Dictionary<string,string> Canonical(Dictionary<string,string> journal) {
   var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(var item in journal) {
    string name=item.Key.ToUpperInvariant();
    if(!Array.Exists(Names,candidate=>candidate==name))throw new InvalidDataException("Неизвестное поле журнала прокси. Исходные данные сохранены.");
    string value=item.Value??"__DEVPROXY_NULL__",previous;
    if(result.TryGetValue(name,out previous)&&previous!=value)throw new InvalidDataException("Копии переменной "+name+" противоречат друг другу. Журнал сохранён для ручного восстановления.");
    result[name]=value;
   }
   return result;
  }
  public static void Apply(ConfigManager config,IProxyEnvironmentStore store,string proxy) {
   if(config==null||store==null)throw new ArgumentNullException();
   if(string.IsNullOrWhiteSpace(proxy))throw new ArgumentException("Укажите адрес прокси.");
   var original=new Dictionary<string,string>(config.EnvironmentBackup,StringComparer.Ordinal);
   config.EnvironmentBackup=Canonical(original);
   var user=new Dictionary<string,string>();var process=new Dictionary<string,string>();
   foreach(string name in Names) {
    user[name]=store.Read(name,EnvironmentVariableTarget.User);
    process[name]=store.Read(name,EnvironmentVariableTarget.Process);
    if(!config.EnvironmentBackup.ContainsKey(name))config.EnvironmentBackup[name]=user[name]??"__DEVPROXY_NULL__";
   }
   if(!config.Save()) {
    config.EnvironmentBackup=original;
    throw new IOException("Копия переменных не сохранена. Настройки Windows не менялись. "+config.LastSaveError);
   }
   try {
    foreach(string name in Names) {
     string value=name=="NO_PROXY"?"localhost,127.0.0.1,::1,127.0.0.0/8,*.local":proxy;
     store.Write(name,value,EnvironmentVariableTarget.User);
     store.Write(name,value,EnvironmentVariableTarget.Process);
    }
    store.Notify();
   } catch(Exception failure) {
    var errors=new List<string>();
    foreach(string name in Names)foreach(var target in new[]{EnvironmentVariableTarget.User,EnvironmentVariableTarget.Process})
     try {store.Write(name,target==EnvironmentVariableTarget.User?user[name]:process[name],target);}catch(Exception error){errors.Add(name+": "+error.GetType().Name);}
    try {store.Notify();}catch(Exception error){errors.Add(error.GetType().Name);}
    throw new IOException("Не удалось записать переменные прокси. Копия сохранена."+(errors.Count>0?" Откат требует проверки: "+string.Join(", ",errors):" Прежние значения возвращены."),failure);
   }
  }
  public static void Restore(ConfigManager config,IProxyEnvironmentStore store) {
   var original=new Dictionary<string,string>(config.EnvironmentBackup,StringComparer.Ordinal);
   var saved=Canonical(original);
   if(saved.Count==0)return;
   var errors=new List<string>();
   foreach(var item in saved) {
    // A journal cannot write arbitrary Windows variables.
    if(!Array.Exists(Names,name=>name.Equals(item.Key,StringComparison.OrdinalIgnoreCase))) {errors.Add("Неизвестное поле журнала");continue;}
    foreach(var target in new[]{EnvironmentVariableTarget.User,EnvironmentVariableTarget.Process})
     try {store.Write(item.Key,item.Value=="__DEVPROXY_NULL__"?null:item.Value,target);}catch(Exception error){errors.Add(item.Key+": "+error.GetType().Name);}
   }
   try {store.Notify();}catch(Exception error){errors.Add(error.GetType().Name);}
   if(errors.Count>0)throw new IOException("Не все переменные восстановлены. Журнал сохранён: "+string.Join(", ",errors));
   config.EnvironmentBackup.Clear();
   if(!config.Save()) {config.EnvironmentBackup=original;throw new IOException("Переменные возвращены, но журнал не закрыт. Копия сохранена. "+config.LastSaveError);}
  }
 }
}
