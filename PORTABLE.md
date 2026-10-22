# Dagami Agent Proxy — Linux и macOS

Порт в `v0.2.0-alpha` от 22.10.2026. Код, GUI и сценарии отката проверены на Windows
с подменой системных команд. Настоящие сборки и DNS на Linux/macOS ещё требуют
проверки на этих ОС. Готовых Linux/macOS-файлов в выпуске нет.
Ручная сборка: [пошаговый план](BUILD_LINUX_MACOS.md). Матрица сборок CI
остаётся ручной; успешная сборка на целевых ОС пока не заявлена.

## Файлы и запуск

Одна программа — один основной исполняемый файл:

| ОС | Формат |
|---|---|
| Windows | `DagamiAgentProxy.exe`: нативные GUI, CLI и DNS |
| Linux | `dagami-agent-proxy`: один файл PyInstaller |
| macOS | `Dagami Agent Proxy.app`: один главный файл в `Contents/MacOS` |

В выпуске готов только Windows ZIP с единственным `DagamiAgentProxy.exe`.
Локальная Windows-сборка пишет его в `dist-portable/windows`.
Linux/macOS-файлы пока не собраны.

Внутри `.app` хранятся также библиотеки и ресурсы. Второй CLI не нужен.
Без аргументов открывается GUI; с аргументами тот же файл исполняет команду.
На macOS из Terminal: `"/Applications/Dagami Agent Proxy.app/Contents/MacOS/Dagami Agent Proxy" --help`.
На Linux после распаковки: `chmod +x dagami-agent-proxy`, затем `./dagami-agent-proxy`.

GUI работает от обычного пользователя. Для записи DNS ОС отдельно запрашивает
права: polkit/pkexec на Linux, системный диалог на macOS. Запускать всё окно через
sudo не нужно. Автозапуск при входе включается галочкой; он также работает от
пользователя. Windows сохраняет свой режим с правами администратора.

Есть логотип, синие кнопки действий, окно DNS и трей. При отсутствии трея окно
остаётся доступным. На Linux нужен X11 либо XWayland для Tk; значок зависит от
панели и её поддержки AppIndicator/XEmbed. Для GNOME может быть нужно расширение.
Повторный запуск показывает прежнее окно. Выход из GUI не останавливает сессии
приложений: для них есть «Остановить сессию» и CLI `stop`.

## DNS и откат

Режим по умолчанию задаёт DNS лишь для доменов. Общий DNS — отдельный пункт
с выбором подключений. Наборы Development и Full совпадают с Windows:
66 и 76 имён; в первом нет игр и стримов.

| ОС | Домены | Общий DNS |
|---|---|---|
| Linux | NetworkManager с **уже включённым** плагином dnsmasq | Активные профили NetworkManager, через nmcli |
| macOS | Отдельные файлы `/private/etc/resolver` | Службы networksetup |

Linux не переключает DNS-плагины и не перезапускает сетевые подключения.
Если dnsmasq не включён, режим доменов завершится до записи. Общий DNS доступен
явно; systemd-networkd, отдельный systemd-resolved, resolvconf и свои службы VPN
этим портом не настраиваются. Конфликтующие чужие правила dnsmasq требуют ручного
разбора. На macOS замену чужого файла resolver можно явно разрешить с копией.

До записи сохраняются точные прежние значения и файлы. Проверяются исходные
DNS/HTTPS и новый сервер; после записи — сеть. При ошибке идёт откат и новая
проверка. Отчёт различает возврат настроек и доступность сети. Внешние изменения
сохраняются, исчезнувшие подключения пропускаются; копия остаётся в истории.
Новая запись после полного сброса не возвращает очищенные настройки сама.

Красная кнопка сброса требует два ответа на «Вы точно уверены в том что вы делаете?».
Она охватывает чужие настройки в **поддержанных механизмах**: все профили NM и
DNS-директивы его dnsmasq либо все службы macOS и файлы resolver. NRPT есть лишь
в Windows. Управляемые политики и свои службы VPN могут вновь задать DNS.
Сброс может лишить сеть рабочих имён; его отчёт не обещает доступ в интернет.

Копии root, режим 0600: Linux `/var/lib/dagami-agent-proxy/dns`, macOS
`/Library/Application Support/DagamiAgentProxy/SystemDns`. Краткий статус и
отчёты операций доступны GUI для чтения. Каталог защищён от записи пользователя.

## Профили, сессии и ярлыки

```sh
./dagami-agent-proxy profile add --name default
./dagami-agent-proxy apps list
./dagami-agent-proxy register vscode /path/to/editor
./dagami-agent-proxy run vscode --profile default
./dagami-agent-proxy patch vscode --profile default
./dagami-agent-proxy unpatch vscode
./dagami-agent-proxy sessions
./dagami-agent-proxy stop vscode
./dagami-agent-proxy dns services
./dagami-agent-proxy dns apply --server 185.149.145.6
./dagami-agent-proxy dns restore
./dagami-agent-proxy autostart status
```

Пароль вводится без URL в истории оболочки. Linux хранит его через Secret Service
(`secret-tool`, пакет libsecret-tools); macOS — через Keychain. При недоступном
хранилище пароль не сохраняется в открытом виде. Настройки и сессии — `~/.devproxy`
либо явный DEVPROXY_HOME. Приложения получают свой loopback-прокси и окружение;
глобальный прокси системы не меняется. Доступ аккаунта и моделей проверяется
в самом приложении. Codex CLI/OpenCode работают в терминале с обычным вводом.

Linux создаёт управляемые `.desktop` в пользовательском меню. macOS создаёт
обёртки `.app` в `~/Applications/Dagami Agent Proxy`: их можно закрепить в Dock.
Подписанный исходный `.app` не меняется. Бинарный патч Windows не переносится
на ELF/Mach-O. Snap/Flatpak и отдельные sandbox-режимы требуют своих проверок.
Встроенный Windows-обновлятор и окно активации пока не перенесены; новые сборки
Linux/macOS заменяются вручную после остановки сессий.

Журнал ярлыков содержит копии и хеши; чужие правки не затираются.
Фоновые сессии используют отдельный запуск того же файла и свой каталог runtime,
чтобы очистка временных файлов GUI не сломала сессию. IPC — лишь 127.0.0.1 с
токеном в файле 0600. Журналы ограничены по размеру; stdout приложений не пишется.

## Сборка и проверка

Собирать нужно **на целевой ОС и архитектуре**. Windows не создаёт ELF или Mach-O.
Нужны Python 3.10+, Tk и зависимости:

```sh
python3 -m pip install '.[build,desktop]'
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/build_portable.py
```

Для Linux нужен пакет Tk; без дисплея используйте `xvfb-run -a` для тестов/сборки.
Pystray использует доступный backend; для AppIndicator нужен также PyGObject
и системные GTK/introspection-пакеты. Без него возможен XEmbed в X11.
Результат — ZIP и SHA-256 в `dist-portable`; на macOS архив хранит ссылки bundle.
Нативный Windows-скрипт `python tools/build_native_app.py` по умолчанию пишет
в `dist-portable/windows`, а не в корень исходников. Если в целевой папке
есть другие EXE, сборка остановится до записи, чтобы не выдавать набор дублей.
Для публичной macOS-сборки нужны Developer ID и notarization на Mac.

`python3 tools/build_portable.py --source-only` создаёт лишь архив исходников.
Ручная матрица CI описана в `.github/workflows/portable.yml`. Её запуск загрузит
артефакты на GitHub, поэтому для него нужен явный запрос владельца. Скрипт локальной
сборки сам ничего не публикует и не загружает.

Установка исходников: `python3 tools/install_portable.py`, обновление — с `--upgrade`.
На Linux создаётся один launcher `~/.local/bin/dagami-agent-proxy`; на macOS — один
главный launcher в `~/Applications/Dagami Agent Proxy.app/Contents/MacOS`.
Tk и tray-пакеты нужны в выбранном Python. Старые исходники сохраняются в backup;
чужие файлы не перезаписываются. Автозапуск и DNS установщик не включает.
Pip также даёт одну команду `dagami-agent-proxy`; прежние алиасы не создаются.

До первого готового выпуска остаются пробы на Linux/macOS: реальный DNS и откат,
polkit/системный запрос прав, логон, трей, запуск IDE, Keychain/Secret Service и
работа собранного файла после выхода GUI. Файловые тесты на Windows их не заменяют.
