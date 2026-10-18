# Dagami Agent Proxy: Windows CLI и переносимые GUI/CLI

Это локальная ветка разработки, версия `0.1.0 альфа` (Python-пакет `0.1.0a0`). Она не опубликована.
Windows Forms GUI остаётся основным Windows-приложением. Новый `dagami-proxy.exe`
использует его настройки и журнал обычных ярлыков. Python GUI и CLI используют общее
переносимое ядро и собственные профили `~/.devproxy/state.json`.

## Windows CLI

Поместите `dagami-proxy.exe` рядом с `DagamiAgentProxy.exe`. Python не требуется. Для смены прокси через CLI сначала завершите менеджер через «Выход» в трее: работающий GUI хранит прежнее подключение в памяти.

```powershell
.\dagami-proxy.exe apps
.\dagami-proxy.exe proxy set             # скрытый ввод HTTP/HTTPS URL
.\dagami-proxy.exe proxy status          # без пароля
.\dagami-proxy.exe patch antigravity
.\dagami-proxy.exe shortcuts antigravity
.\dagami-proxy.exe run antigravity
.\dagami-proxy.exe unpatch antigravity
.\dagami-proxy.exe dns status
.\dagami-proxy.exe dns adapters
.\dagami-proxy.exe dns apply dns-request.json  # явный UAC
.\dagami-proxy.exe dns restore                 # явный UAC
```

`unpatch` восстанавливает обычные ярлыки; подготовка бинарного клиента Antigravity
откатывается отдельно через существующий `--antigravity-restore FULL_PATH`.
CLI не публикует ничего и не применяет DNS автоматически при `patch`.
Пример запроса DNS, после подстановки GUID/индекса из `dns adapters`:

```json
{"Server":"185.149.145.6","Preset":"Development","ChangeAdapters":true,
 "UseNrpt":false,"ReplaceExisting":false,
 "Adapters":[{"Guid":"GUID_FROM_ADAPTERS","Index":1}]}
```

Операция и случайный JobId задаются CLI; запрос проверяется тем же валидатором,
что GUI. Резервная копия, защищённый помощник, UAC и откат остаются прежними.

## Linux и macOS: установка из исходников

Нужен Python 3.10+ с Tk. На Debian/Ubuntu: `sudo apt install python3-tk libsecret-tools`.
На macOS используйте Python с рабочим Tk (проверка: `python3 -m tkinter`).
Для Linux с авторизованным прокси нужна разблокированная сессия Secret Service,
например GNOME Keyring или совместимое хранилище; одного установленного secret-tool
в headless-сессии может быть недостаточно. На macOS используется Keychain.

Из распакованного исходного архива:

```sh
python3 install_portable.py
# ~/.local/bin должен входить в PATH
dagami-proxy profile add --name main   # URL запрашивается скрытым вводом
dagami-proxy apps list
dagami-proxy register antigravity /path/to/executable   # если автопоиск не нашёл
dagami-proxy run antigravity --profile main
dagami-proxy patch antigravity --profile main
dagami-proxy shortcuts antigravity
dagami-proxy sessions
dagami-proxy logs antigravity
dagami-proxy stop antigravity
dagami-proxy unpatch antigravity
dagami-proxy gui
```

macOS: `register` принимает также полный путь к `.app`. Исходная установка создаёт
`~/Applications/Dagami Agent Proxy.app`. Linux: приложение появляется в пользовательском меню.
`python3 install_portable.py --upgrade` сохраняет прежние исходники в backup-каталоге.
Альтернатива для CLI: `python3 -m pip install .`; Tk предоставляется установкой Python.

GUI: выбор профиля и приложения, добавление прокси, запуск, «Пропатчить», восстановление,
остановка, проверка, журнал, явное указание пути и ссылки проекта. Закрытие окна не
останавливает фоновые GUI-сессии. В этой переносимой версии отдельный значок трея
не реализован: открыть GUI можно снова, сессии доступны через CLI.

Для Codex CLI и OpenCode `dagami-proxy run` работает в текущем терминале с обычным вводом;
из GUI открывается отдельный терминал. Для GUI-приложений поднимается отдельный
пользовательский helper и loopback HTTP-мост. Helper сохраняет мост для дочерних
процессов после выхода CLI-обёртки приложения. Linux/macOS: сессия завершается после
выхода принадлежащей ей группы процессов; Windows Python-helper до явного `stop`.
Повторный запрос использует существующий helper с тем же профилем. Смена профиля
требует остановки сессии. Перед первым запуском полностью закройте приложение,
ранее запущенное напрямую: чужой процесс не получает новое окружение задним числом.

## Ярлыки

Linux: пользовательские `.desktop` в `$XDG_DATA_HOME/applications`. Системный файл
не меняется; создаётся пользовательское переопределение с тем же именем. Сохраняются
Name/Icon/Path и аргументы/подстановки Exec. D-Bus activation и дополнительные Actions
отключаются в управляемой записи, чтобы они не обходили основной Exec. При отсутствии
подходящего ярлыка создаётся отдельный Dagami launcher. Закрепления, использующие
тот же desktop ID, подхватывают его после обновления меню оболочки; конкретная оболочка
может потребовать перепривязки значка. Shell-команды `env ...` в Exec автоматически
не переписываются. Snap/Flatpak-сандбоксы требуют отдельной проверки.

macOS: `~/Applications/Dagami Agent Proxy/APP — Proxy.app`, отдельная локальная обёртка. Её нужно
открывать вместо исходной и при желании закрепить в Dock. Исходный подписанный `.app`
и его обновления не изменяются; существующие значки исходного приложения не перехватываются.
CLI-обёртки открывают Terminal. Права аккаунта, регион и доступность моделей эта подготовка
не меняет. Windows protobuf-патч нельзя автоматически переносить на Mach-O/ELF.

Журнал ярлыков: `~/.devproxy/shortcuts-portable/journal.json`. В нём резервные байты,
права и SHA-256 изменённых файлов; паролей нет. Повторное применение идемпотентно,
чужие правки останавливают откат. Для смены профиля сначала выполните `unpatch`.

## Авторизация и журнал

`dagami-proxy login antigravity --url 'FRESH_GOOGLE_URL'` открывает отдельный Chromium-профиль
через тот же локальный прокси. В GUI свежая ссылка вводится в отдельном диалоге. Полную
ссылку нельзя публиковать; CLI-аргумент может попасть в историю оболочки, поэтому GUI
удобнее для её ввода. Эта версия не извлекает OAuth-ссылку из сторонних логов автоматически.
Loopback-адреса исключены из прокси. Antigravity использует обычный профиль; UI и backend
получают HTTP_PROXY/HTTPS_PROXY/ALL_PROXY и AG_LS_PROXY/AGY_PROXY_URL.

Пароли сохраняются в системном хранилище, не в JSON и не в ярлыках. При недоступности
хранилища сохранение авторизованного постоянного профиля отклоняется. Для headless Linux
нужно отдельно предоставить рабочее защищённое хранилище; plaintext fallback не используется.

`~/.devproxy/sessions/APP.log`: ограниченные по размеру журналы жизненного цикла и ошибок.
Три ротации по 512 КиБ; отдельный `.boot.log` для ошибок старта. Данные stdout/stderr
GUI-приложений не копируются в эти файлы, чтобы не собирать токены и содержимое запросов.
IPC слушает только 127.0.0.1; токен находится в пользовательском файле с режимом 0600
в каталоге 0700 на POSIX. Статус транспорта не доказывает вход или ответ модели.

## Сборка

```sh
python3 -m pip install 'pyinstaller>=6.16,<7'
python3 build_portable.py --source-only
python3 build_portable.py
python3 -m unittest discover -s tests -p 'test_*.py' -v
```

Результаты: `dist-portable`, отдельные CLI и GUI, архив и SHA-256. Сборка всегда для
текущей ОС/архитектуры. Нельзя получить настоящий macOS/Linux-бинарник запуском PyInstaller
на Windows. `.github/workflows/portable.yml` — ручная матрица Windows, Linux x64/arm64,
macOS Intel/Apple Silicon; она загружает artifacts и не создаёт GitHub Release.
Для публичной macOS-дистрибуции остаётся подпись Developer ID и notarization на Mac.
Ключи подписи в репозиторий не добавляются.

Системный DNS через NRPT реализован только в Windows. Linux systemd-resolved,
NetworkManager и macOS networksetup в этой версии не изменяются. Пользовательский
туннель работает независимо от глобального DNS, но доступ к конкретным сервисам
и аккаунтам нужно проверять на целевой машине и используемом прокси.

## Имя, иконка и совместимость

Продукт называется **Dagami Agent Proxy**. Основной Windows EXE — `DagamiAgentProxy.exe`,
его консольный спутник — `dagami-proxy.exe`. Python-пакет — `dagami-agent-proxy`;
команды `dagami-proxy` и `dagami-agent-proxy` открывают CLI и GUI. Старые команды
`devproxy`/`devproxy-gui` сохранены как pip-алиасы. Имена модулей, каталог `~/.devproxy`,
Windows-хранилище, DPAPI entropy и IPC-идентификаторы оставлены прежними, чтобы
переименование не сбрасывало профили и журнал существующих ярлыков.

Знак — буква A как сетевой маршрут: светлая линия, бирюзовые узлы, тёмный фон.
`devproxy_pkg/assets` содержит SVG, PNG 16–1024, многоразмерный ICO и ICNS.
`python make_brand_assets.py` воспроизводит их без шрифтов, внешних пакетов и загрузок.
Иконка встроена в Windows EXE, окно и трей; переносимый Tk GUI использует PNG,
Linux-меню — PNG, macOS bundle — ICNS.

На Windows рекомендуются нативные GUI/CLI: они используют уже настроенное подключение.
Архив `dagami-agent-proxy-windows-x64.zip` содержит **переносимый Python preview**,
его CLI называется `dagami-portable-cli.exe`, GUI находится в каталоге `Dagami Agent Proxy`.
Это отдельное переносимое ядро с собственными профилями, а не замена текущего Windows GUI.
Архив необходимо распаковать целиком: EXE нужны соседние файлы `_internal`.

Проверено локально на Windows: 40 тестов, встроенная иконка и название в нативной сборке,
рендер интерфейса, запуск упакованного Tk GUI, CLI help, реальный локальный CONNECT
и тестовый процесс из обоих готовых переносимых EXE. Проверялись повторное использование
сессии, защита IPC, остановка и отсутствие stdout-секретов в журнале. Linux/macOS
проверены только файловыми тестами форматов и логики на Windows; нативные сборки,
desktop-интеграция и вход/модели на этих ОС ещё требуют проверки на целевых машинах.
Описанные проверки переносимого Python preview выполнены ранее; текущий нативный Windows-релиз — v0.1.0-alpha.5.

## Windows CLI: обычная команда (v0.1.0-alpha.5)

`dagami-proxy.exe patch APP_ID` и GUI «Пропатчить» для консольных приложений
настраивают команду через управляемый каталог пользовательского PATH; ярлыки не нужны.
После изменения полностью перезапустите терминал. GUI и CLI берите из одного архива.
`dagami-proxy.exe run APP_ID -- ARGS` запускает CLI в текущем терминале без записи PATH;
`unpatch APP_ID` возвращает прямой запуск. Подробности.

## Обновления нативной Windows-сборки (v0.1.0-alpha.5)

В GUI — «Обновления» внизу боковой панели. Проверка автоматическая (6 часов),
скачивание/установка — по кнопке. `dagami-proxy.exe update check` и `update download`
работают из терминала. Первый переход на up5 вручную заменяет оба EXE одним архивом.
Есть SHA-256, проверка пары, резервные копии, откат и блокировка активных сессий.
Формат GitHub-релиза и подробности.
Python/Linux/macOS этот механизм не устанавливает.
