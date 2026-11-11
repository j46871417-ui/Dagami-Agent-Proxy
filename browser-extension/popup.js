const portField = document.querySelector("#port");
const toggle = document.querySelector("#toggle");
const status = document.querySelector("#status");
let busy = false;
let revision = 0;
let active = false;

portField.value = Dagami.defaultPort;

async function refresh() {
  const requested = ++revision;
  const current = await Dagami.read();
  if (requested !== revision) return;
  const wasActive = active;
  active = current.levelOfControl === Dagami.own;
  const canControl = active || current.levelOfControl === Dagami.available;
  toggle.checked = active;
  toggle.disabled = !canControl || busy;
  portField.disabled = active || !canControl || busy;
  if (!canControl) {
    status.textContent = "Прокси контролирует политика браузера или другое расширение.";
    return;
  }
  if (!active) {
    status.textContent = "Выключено. Действуют обычные настройки браузера.";
    return;
  }
  const port = Dagami.portFrom(current.value);
  portField.value = port;
  if (!wasActive) status.textContent = "Проверяем локальный мост…";
  const ready = await Dagami.bridgeReady(port);
  if (requested !== revision) return;
  const message = ready ? "Включено. Локальный мост отвечает." :
    "Dagami недоступен. Запустите приложение и выберите подключение по ключу.";
  if (status.textContent !== message) status.textContent = message;
}

async function showStatus() {
  try { await refresh(); }
  catch {
    toggle.disabled = true;
    portField.disabled = true;
    status.textContent = "Браузер не дал прочитать настройки прокси.";
  }
}

toggle.addEventListener("change", async () => {
  const turnOn = toggle.checked;
  if (turnOn && !portField.reportValidity()) {
    toggle.checked = false;
    return;
  }
  ++revision;
  busy = toggle.disabled = portField.disabled = true;
  let error;
  try {
    if (turnOn) await Dagami.enable(Number(portField.value));
    else await Dagami.disable();
  } catch (failure) {
    error = failure.message;
  } finally {
    busy = false;
    await showStatus();
    if (error) status.textContent = error;
  }
});

Dagami.migrate().catch(() => {}).finally(showStatus);
setInterval(() => { if (active && !busy) showStatus(); }, 5000);
