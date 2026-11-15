using System;
using System.Threading.Tasks;

namespace DevProxyApp {
 // One shared, bounded probe per bridge. It verifies a transport, never an
 // account's region, model access or the availability of every AI service.
 public sealed class RouteHealth {
  readonly object sync=new object(); Task<bool> pending; DateTime checkedAt;
  public bool Check(Func<bool> probe,int waitMilliseconds=12000) {
   Task<bool> current;
   lock(sync) {
    if(pending==null||(pending.IsCompleted&&DateTime.UtcNow-checkedAt>TimeSpan.FromSeconds(20))) {
     pending=Task.Run(()=>{try{return probe();}catch{return false;}});
     checkedAt=DateTime.UtcNow;
    }
    current=pending;
   }
   return current.Wait(waitMilliseconds)&&current.Result;
  }
 }
}
