const settings = chrome.proxy.settings;
const portField = document.querySelector("#port");
const enable = document.querySelector("#enable");
const disable = document.querySelector("#disable");
const status = document.querySelector("#status");

async function refresh() {
  const current = await settings.get({incognito: false});
  const active = current.levelOfControl === "controlled_by_this_extension";
  const canEnable = active || current.levelOfControl === "controllable_by_this_extension";
  enable.disabled = !canEnable;
  disable.disabled = !active;
  if (active) {
    const match = /^http:\/\/127\.0\.0\.1:(\d+)\/dagami-selective\.pac$/.exec(current.value.pacScript?.url ?? "");
    if (match) portField.value = match[1];
  }
  status.textContent = active ? "Выборочный прокси включён." :
    canEnable ? "Выборочный прокси выключен." : "Прокси контролирует политика браузера или другое расширение.";
}

async function run(operation) {
  enable.disabled = disable.disabled = true;
  try {
    await operation();
    await refresh();
  } catch (error) {
    try { await refresh(); } catch { /* Keep controls disabled when the API is unavailable. */ }
    status.textContent = error.message;
  }
}

enable.addEventListener("click", () => run(async () => {
  if (!portField.reportValidity()) return;
  const url = "http://127.0.0.1:" + Number(portField.value) + "/dagami-selective.pac";
  let response;
  try {
    response = await fetch(url, {cache: "no-store", redirect: "error", signal: AbortSignal.timeout(3000)});
  } catch {
    throw new Error("Мост Dagami недоступен. Проверьте порт и подключение по ключу.");
  }
  if (!response.ok || !response.headers.get("content-type")?.startsWith("application/x-ns-proxy-autoconfig")) {
    throw new Error("Dagami не выдал сценарий PAC. Проверьте подключение по ключу.");
  }
  await response.body?.cancel();
  const current = await settings.get({incognito: false});
  if (!["controllable_by_this_extension", "controlled_by_this_extension"].includes(current.levelOfControl)) {
    throw new Error("Прокси контролирует политика браузера или другое расширение.");
  }
  await settings.set({scope: "regular_only", value: {mode: "pac_script", pacScript: {url, mandatory: true}}});
}));

disable.addEventListener("click", () => run(() => settings.clear({scope: "regular_only"})));
run(refresh);
