# Структуры приложений и реализованная подготовка

Исследование от 22.10.2026, использованное в выпуске 0.2.0 alpha. Доработки для IDE 2.6
от 28.10.2026 описаны в [RECHECK_20261028.md](RECHECK_20261028.md).
[Матрица поддержки](MATRIX.md) отделяет механизм настройки от
проверки входа, провайдера и модели.

## VS Code, Cursor, Windsurf, VSCodium

Семейство редакторов с Electron/Chromium, Node extension host и дочерними
инструментами. `%APPDATA%/<продукт>/User/settings.json`: Code, Cursor, Windsurf,
VSCodium, Antigravity; для Insiders — Code - Insiders. Portable-установка:
`<каталог EXE>/data/user-data/User/settings.json`.

Реализованы выбор конфига, `http.proxy`, `http.proxySupport=override`,
`http.proxyStrictSSL=true`, правильный массив `http.noProxy`; обычный профиль,
Chromium-флаги и env дочерним процессам. При управляемом старте порт обновляется
в ранее пропатченном конфиге. Старый экземпляр нужно закрыть: Electron может
передать ему запрос без нового окружения. Прежде отдельный пустой user-data-dir
отрывал запущенную IDE от её аккаунта и уже записанного конфига — это исправлено.

Расширения могут использовать собственные HTTP-клиенты; remote host, WSL и
контейнер не равны локальному процессу. Cursor использует потоковые соединения;
у Windsurf есть отдельный language server. Для следующего этапа нужны диагностика
HTTP/2/SSE и проверка backend конкретной версии. У Cursor можно проверить штатный
режим HTTP/1.1 в настройках. Изменение app.asar само по себе не подтверждает
исправление и потребует сопровождения каждой версии.

Источники: [VS Code network](https://code.visualstudio.com/docs/setup/network),
[схема proxy-настроек VS Code](https://github.com/microsoft/vscode/blob/main/src/vs/platform/request/common/request.ts),
[portable mode](https://code.visualstudio.com/docs/editor/portable),
[Cursor network](https://prod.cursor.com/help/troubleshooting/network),
[Cursor enterprise](https://prod.cursor.com/docs/enterprise/network-configuration),
[Windsurf troubleshooting](https://docs.windsurf.com/troubleshooting/windsurf-common-issues),
[VSCodium source/build](https://github.com/VSCodium/vscodium).

## Codex CLI и Desktop

CLI — отдельный backend; Desktop имеет собственное окно и дочерние процессы.
Одинаковое имя Codex.exe не означает одинаковый тип: Windows PE subsystem
различает console/GUI. Добавлены отдельная строка Desktop, поиск стандартных
каталогов, App Paths и доступного EXE работающего пакета. PATH приоритетен;
запасной выбор CLI из каталога версий сортируется по времени файла.

CLI получает управляемую команду и env; Desktop — ярлыки, Chromium и env с обычным
профилем. config.toml не получает вымышленный общий ключ proxy. Обработчик
восстановления старых Python-записей сохранён. Проверка запущенного процесса
различает полные пути CLI и Desktop с одинаковым именем.

В исходниках Codex выбор исходящего proxy учитывает HTTP_PROXY, HTTPS_PROXY,
ALL_PROXY и исключения. Это не обещает доступ любого sandbox к Windows loopback:
WSL, контейнеры, remote executor и политики сети нужно проверять отдельно.
Их разрешения и sandbox_mode не менялись. Проприетарные файлы Desktop для
изменения ответов сервиса не модифицировались.

Источники: [Codex outbound proxy](https://github.com/openai/codex/blob/main/codex-rs/http-client/src/outbound_proxy.rs),
[официальный справочник конфигурации](https://learn.chatgpt.com/docs/config-file/config-reference).

## OpenCode CLI и Desktop

CLI и локальный сервер должны получать proxy env с исключением localhost.
Текущий Desktop в ветке dev использует Electron и отдельно упакованный CLI
вне app.asar; старые Windows-версии использовали Tauri/WebView2. Фоновый сервер
может пережить окно и сохранить прежнее окружение.

Реализованы раздельное обнаружение CLI/Desktop, env, распознавание Electron по
ресурсам и Chromium-флаги для него, WebView2 env для прежних Windows-сборок.
Перед стартом проверяются старые процессы Desktop/известных серверов. Не
добавляется неподтверждённый JSON-ключ proxy; пути Python исправлены на XDG
`opencode/opencode.json/jsonc`. Старые записи изменения config можно восстановить.
Внешний уже работающий сервер нужно перезапустить отдельно с нужным окружением.

Источники: [настройки сети](https://opencode.ai/docs/network/),
[текущий Desktop README](https://github.com/anomalyco/opencode/blob/dev/packages/desktop/README.md),
[официальные troubleshooting](https://opencode.ai/docs/troubleshooting/).

## Gemini CLI, Claude Code, agy

Сетевые запросы выполняет нативный EXE либо Node entrypoint стандартной npm
CMD/PS1-обёртки. Дополнительные CLI ищутся также вне PATH в типовых каталогах.
В C# стандартная npm-обёртка разбирается для запуска Node напрямую с сохранением
аргументов; произвольный CMD/BAT за npm не принимается. NODE_USE_ENV_PROXY=1
помогает подходящим версиям Node; поддержка proxy в самом CLI всё равно обязательна.

Реализованы env в обоих регистрах, loopback bypass, управляемые команды,
сохранение stdin/stdout/stderr, кода выхода, аргументов и рабочего каталога.
Gemini CLI исключён из Desktop-эвристики Gemini.exe. У agy дополнительно остаётся
версия-зависимая бинарная подготовка. Для Claude не меняются managed settings,
ключи и аккаунт. Эти меры не объявляют отдельный Claude Desktop проверенным.

Источники: [Gemini CLI — выбор proxy из env](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/config/config.ts),
[Gemini configuration](https://github.com/google-gemini/gemini-cli/blob/main/docs/reference/configuration.md),
[Claude Code network](https://code.claude.com/docs/en/network-config),
[предыдущее исследование agy](LOCAL_CHANGES.md).

## Git и терминалы

Git использует свою HTTP-конфигурацию; SSH — другой транспорт. Реализована
команда dagami-git, которая добавляет http.proxy и http.sslVerify=true через
GIT_CONFIG_COUNT/KEY_n/VALUE_n только дочернему процессу, сохраняя существующие
записи. Обычный git и глобальный .gitconfig не меняются. Отмена удаляет свой
запускатель и собственное вхождение PATH, сохраняя чужие изменения.

Терминал, добавленный вручную, получает ярлык с env дочерним командам.
Windows PowerShell, PowerShell 7 и cmd исключены из автоматического обнаружения.
Системные команды в PATH не заменяются. Не всякий инструмент уважает env;
старый терминал и существующий daemon не перенастраиваются. Windows Terminal
с повторно используемым сервером требует отдельной реализации.

Источник: [Git config: environment и http.proxy](https://git-scm.com/docs/git-config).

## JetBrains на платформе IntelliJ

Установки имеют product-info.json и launcher в bin. Прежний поиск первого
попавшегося *64.exe мог выбирать служебный файл. Теперь ищется product-info в
Program Files, пользовательских Programs и Toolbox с ограниченной глубиной;
выбираются известные IDE launcher и отдельные идентификаторы установок.

Из dataDirectoryName определяется `%APPDATA%/JetBrains/<версия>/options/proxy.settings.xml`.
В официальном исходнике это компонент HttpConfigurable; современный адаптер
сохраняет совместимость с ним. Записываются HTTP, localhost и порт, отключаются
PAC и proxy-auth для локального моста. Данные паролей не копируются. XML читается
без DTD/внешних сущностей и заменяется атомарно. Процесс получает JVM proxy
properties и env.

Откат заменяет ручные изменения управляемых proxy-полей исходными значениями,
сохраняя остальные XML-компоненты. При нестандартном idea.config.path или
IDEA_PROPERTIES автоматический каталог может не совпасть: требуется отдельная
проверка. Gradle/Maven/SSH, уже работающие daemon и Fleet не объявлены покрытыми.

Источники: [JetBrains HTTP proxy](https://www.jetbrains.com/help/idea/settings-http-proxy.html),
[JVM/custom configuration](https://www.jetbrains.com/help/idea/tuning-the-ide.html),
[HttpConfigurable.java](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-api/src/com/intellij/util/net/HttpConfigurable.java),
[современный адаптер](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/src/com/intellij/util/net/OverrideCapableProxySettings.kt).

## Проверки и дальнейшая проверка на приложениях

Проверены синтетические структуры установок, настоящие Windows lnk, JSON/XML
и откат, выбор EXE, конфликт PATH, Git alias, передача аргументов/env реальному
дочернему процессу, локальный CONNECT-трафик и время жизни сессии. Проверки
не требуют учётных записей и не подтверждают ответ модели.

Следующий этап на конкретных версиях: запуск, вход, запрос модели/расширения,
стриминг, холодный/тёплый backend и откат; для JetBrains отдельно build daemon,
для Codex — выбранный sandbox, для OpenCode — повторно используемый сервер.
Реальные установленные приложения, ярлыки пользователя и сайт не изменялись;
недостающие программы не устанавливались.

Бинарные патчи Cursor, Windsurf, Codex и Claude требуют воспроизводимой проблемы
и совместимых сигнатур конкретной версии. Доказанного общего бинарного патча для
них в исследованных источниках нет. В этой сборке для них используются штатные
механизмы конфигурации и запуска; отдельное бинарное преобразование остаётся
для совместимых версий Antigravity/agy.
