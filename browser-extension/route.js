"use strict";

globalThis.Dagami = (() => {
  const settings = chrome.proxy.settings;
  const scope = "regular_only";
  const config = globalThis.dagamiConfig;
  const own = "controlled_by_this_extension";
  const available = "controllable_by_this_extension";
  const legacy = /^http:\/\/127\.0\.0\.1:(\d+)\/dagami-selective\.pac$/;
  const read = () => settings.get({incognito: false});
  const validPort = port => Number.isInteger(port) && port >= 1 && port <= 65535;

  function portFrom(value) {
    const pac = value?.pacScript;
    const match = legacy.exec(pac?.url ?? "") ||
      /^\/\/ Dagami local port: (\d+)\r?\n/.exec(pac?.data ?? "");
    const port = Number(match?.[1]);
    return validPort(port) ? port : config.port;
  }

  async function enable(port) {
    if (!validPort(port)) throw new Error("Укажите порт от 1 до 65535.");
    const current = await read();
    if (![own, available].includes(current.levelOfControl)) {
      throw new Error("Прокси контролирует политика браузера или другое расширение.");
    }
    if(!await bridgeReady(port)) throw new Error("Маршрут не отвечает. Проверьте подключение в Dagami; настройки браузера не менялись.");
    const latest=await read();
    if(![own,available].includes(latest.levelOfControl) || JSON.stringify(latest.value)!==JSON.stringify(current.value)) {
      throw new Error("Настройки прокси изменились во время проверки. Повторите включение.");
    }
    await chrome.storage.local.remove("offlineReason");
    await apply(port);
  }

  function apply(port) {
    const pac = config.pac.split(`PROXY 127.0.0.1:${config.port};`).join(`PROXY 127.0.0.1:${port};`);
    return settings.set({
      scope,
      value: {mode: "pac_script", pacScript: {data: `// Dagami local port: ${port}\n${pac}`, mandatory: false}}
    });
  }

  async function migrate() {
    const current = await read();
    if (current.levelOfControl === own && legacy.test(current.value?.pacScript?.url ?? "")) {
      await apply(portFrom(current.value));
    }
  }

  async function bridgeReady(port) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/dagami-health`, {
        cache: "no-store", redirect: "error", credentials: "omit", signal: AbortSignal.timeout(14000)
      });
      if(!response.ok) {await response.body?.cancel();return false;}
      const result=await response.json();
      return result.dagami===true && result.transportReachable===true;

    } catch {
      return false;
    }
  }

  return {own, available, read, enable, migrate, bridgeReady, portFrom, defaultPort: config.port,
    disable: () => settings.clear({scope})};
})();
