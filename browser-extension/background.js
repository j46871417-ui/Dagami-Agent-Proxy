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
