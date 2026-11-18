using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DevProxyApp {
 internal sealed class AutomaticRouteRollbackException:IOException {
  internal AutomaticRouteRollbackException(string message,Exception inner):base(message,inner){}
 }
 // Pure transaction coordinator: fixtures replace every system operation.
 internal static class AutomaticSystemRoute {
  internal static async Task Apply(Func<Task<bool>> dns,Func<bool> proxy,Func<Task<string>> verify,
   Func<Task> restoreDns,Action restoreProxy,Action persist) {
   bool changedDns=false,changedProxy=false;
   Exception failure=null;
   try {
    changedDns=await dns();
    changedProxy=proxy();
    string checkFailure=await verify();
    if(checkFailure!=null)throw new IOException("Проверка через настройки Windows не прошла: "+checkFailure);
    persist();
    return;
   } catch(Exception original) {failure=original;}
    var errors=new List<string>();
    if(changedProxy)try{restoreProxy();}catch(Exception error){errors.Add("прокси: "+error.GetType().Name);}
    if(changedDns)try{await restoreDns();}catch(Exception error){errors.Add("DNS: "+error.GetType().Name);}
    if(errors.Count>0)throw new AutomaticRouteRollbackException(failure.Message+" Откат требует внимания: "+string.Join(", ",errors)+". Откройте «DNS» / «Восстановление»; копия сохранена.",failure);
    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
  }
 }
}
