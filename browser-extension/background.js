importScripts("config.js", "route.js");

let migration;
function migrateLegacyRoute() {
  if (!migration) {
    migration = Dagami.migrate().catch(error => console.warn("Dagami proxy migration:", error.message))
      .finally(() => { migration = undefined; });
  }
}

chrome.runtime.onInstalled.addListener(migrateLegacyRoute);
chrome.runtime.onStartup.addListener(migrateLegacyRoute);
chrome.proxy.settings.onChange.addListener(migrateLegacyRoute);

// Restore the browser's prior proxy configuration if a live bridge cannot reach
// its upstream. A localhost listener alone is not a working internet connection.
let monitoring=false;
async function monitorRoute() {
 if(monitoring)return;monitoring=true;
 try {
  const before=await Dagami.read();
  if(before.levelOfControl!==Dagami.own)return;
  const port=Dagami.portFrom(before.value);
  if(await Dagami.bridgeReady(port))return;
  const current=await Dagami.read();
  if(current.levelOfControl!==Dagami.own || JSON.stringify(current.value)!==JSON.stringify(before.value))return;
  await Dagami.disable();
  await chrome.storage.local.set({offlineReason:"Соединение Dagami потеряно. Расширение отключено, возвращены обычные настройки браузера. Проверьте маршрут в приложении и включите расширение снова."});
 } finally {monitoring=false;}
}
chrome.alarms.create("dagami-route-health",{periodInMinutes:0.5});
chrome.alarms.onAlarm.addListener(alarm=>{if(alarm.name==="dagami-route-health")monitorRoute().catch(console.warn);});
chrome.runtime.onStartup.addListener(()=>monitorRoute().catch(console.warn));
