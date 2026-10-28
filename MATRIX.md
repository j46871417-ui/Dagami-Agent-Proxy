# Поддержка приложений: 0.2.0 альфа

Исследование от 22.10.2026 для `v0.2.0-alpha`, база `v0.1.0-alpha.10`. Основная реализация новых
механизмов — нативный Windows-клиент. Эта таблица описывает реализованную настройку,
а не подтверждённый доступ к аккаунтам. Структуры и источники: [APPS_RESEARCH.md](APPS_RESEARCH.md).

Дополнения в текущих исходниках от 28.10.2026: отдельная Antigravity IDE 2.6,
каталог расширения `agy`, общий внешний `agy.exe` и локальные ConnectRPC-запросы.
Проверки и границы: [RECHECK_20261028.md](RECHECK_20261028.md). Архивы релиза
0.2.0-alpha от 22.10.2026 этих дополнений пока не содержат.

| Приложение | Что делает «Пропатчить» в Windows | Ограничения |
|---|---|---|
| Antigravity IDE / Manager | Настройки IDE, ярлыки, Chromium/env; проверяемая подготовка совместимого backend | Версия бинарника, вход и модель проверяются отдельно |
| agy CLI | Команда, env; совместимый бинарный патч | Неподдержанные сигнатуры не изменяются |
| VS Code / Insiders | settings.json, ярлыки, Chromium/env, обычный профиль, portable data | Remote extension host и отдельные сетевые библиотеки требуют проверки |
| Cursor | Настройки семейства VS Code, ярлыки, Chromium/env | AI backend и HTTP/2/стриминг зависят также от upstream |
| Windsurf | Настройки семейства VS Code, ярлыки, Chromium/env | Отдельный language server и вход требуют проверки |
| VSCodium | Настройки семейства VS Code, ярлыки, Chromium/env | Marketplace и расширения имеют свои особенности |
| Codex CLI | Команда CLI, env локального моста | config.toml не меняется; sandbox/WSL/remote — отдельный сетевой контекст |
| Codex Desktop | Ярлыки, Chromium/env; обычный профиль; MSIX-пакет OpenAI.Codex и основной EXE из манифеста | Backend, sandbox и живая сессия проверяются отдельно; WindowsApps ACL не меняются |
| OpenCode CLI | Команда, env, исключение loopback | JSON-ключ proxy не добавляется; проверить провайдера |
| OpenCode Desktop | Ярлыки, env; Electron определяется по ресурсам, для прежних Windows-версий WebView2 | Старый сервер нужно закрыть: он не получает новое окружение |
| Gemini CLI | Команда native/npm, env | Google auth; настройки sandbox и проекта |
| Claude Code | Команда native/npm, env | Managed settings; отдельный Claude Desktop не объявлен проверенным |
| Git | Отдельная команда dagami-git; сессионный http.proxy и строгий TLS | Только HTTP(S); обычный git и глобальный .gitconfig сохраняются, SSH отдельно |
| JetBrains на IntelliJ | product-info.json, известные IDE launcher, options/proxy.settings.xml, JVM/env, ярлыки | Нестандартный idea.config.path и отдельные Gradle/Maven/daemon/SSH |
| Терминал, добавленный вручную | Ярлык с env для дочерних команд | PowerShell/pwsh/cmd автоматически не добавляются; системные команды в PATH не заменяются |

JetBrains: IntelliJ IDEA, PyCharm, WebStorm, PhpStorm, CLion, GoLand, Rider,
DataGrip, RubyMine, RustRover, DataSpell. Нестандартную установку IDE или Insiders
можно добавить выбором исходного EXE.

CLI ищутся в PATH, пользовательских `.local/bin`, `.имя/bin`, LocalAppData и npm.
При конфликте с системным PATH обычную CLI-команду нельзя надёжно заменить только
пользовательским PATH: Dagami сообщает об этом; доступен запуск из Dagami или
`run APP_ID -- ARGS`. Для Git сразу используется отдельное имя `dagami-git`.

Нативный upstream — HTTP/HTTPS. Приложения получают локальный HTTP-мост без пароля;
пароль upstream остаётся в Dagami. Проверка сертификатов не отключается. CONNECT
сохраняет байтовый поток, но HTTP/2/стриминг зависит также от upstream и клиента.
Прежние необоснованные утверждения «проверено для всех» и конфиги proxy TOML/JSON удалены.

Python-часть содержит адаптеры IDE, Codex CLI/Desktop, OpenCode CLI/Desktop,
Gemini CLI, Claude Code, agy и Git. Новые XML-настройки JetBrains и ярлыки терминалов
относятся к Windows C#-клиенту. macOS/Linux на реальных приложениях здесь не проверялись.

Произвольный Gemini.exe/GeminiAppLauncher.exe сохраняет прежнюю эвристику
Chromium/WebView2/Qt. Это не подтверждение конкретного официального Desktop-клиента
Google. Gemini CLI определяется отдельно. Windows Terminal и Fleet не приравниваются
автоматически к обычному терминалу/IntelliJ IDE.

Восстановление заменяет ручные изменения управляемых proxy-полей исходными
значениями. Остальные настройки сохраняются. Отмена режима ярлыков / unpatch
восстанавливает настройки IDE при наличии резервной копии.

Повторная проверка и оставшиеся ограничения: [RECHECK_20261022.md](RECHECK_20261022.md).
При смене каталога настроек после обновления откат старых профилей не автоматизирован.
Недоступные исходные ярлыки остаются прямыми: используйте созданную пользовательскую копию.
