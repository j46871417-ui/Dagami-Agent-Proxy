# Как собрать Dagami Agent Proxy на Linux и macOS

Выпуск `v0.1.0-alpha.10`: готовый файл есть для Windows. Для Linux и macOS
дан код и этот план сборки. Запуск, запрос прав, DNS и трей на этих ОС ещё
не прошли наши живые пробы. Сборка на своей машине не равна полному тесту сети.

На каждой ОС собирайте её версию. Для Intel и ARM нужны свои сборки.
Windows не создаст Linux-файл или macOS `.app` этим скриптом.

## 1. Получить код

Скачайте `Dagami-Agent-Proxy-v0.1.0-alpha.10-source.zip` из
[этого выпуска](https://github.com/j46871417-ui/Dagami-Agent-Proxy/releases/tag/v0.1.0-alpha.10).
Распакуйте в новую папку и откройте в ней Terminal. В ней должны быть
`pyproject.toml`, `build_portable.py`, `devproxy_gui.py` и папка `devproxy_pkg`.

Либо получите тот же код через Git:

```sh
git clone --branch v0.1.0-alpha.10 --depth 1 https://github.com/j46871417-ui/Dagami-Agent-Proxy.git Dagami-Agent-Proxy
cd Dagami-Agent-Proxy
```

Дальше команды выполняются из этой папки. Сборку и окно запускайте от
обычного пользователя. `sudo` нужен только для установки пакетов Linux.
Само приложение спросит права ОС при явной записи DNS.

## 2. Linux: Ubuntu 22.04 / 24.04 и Debian 12

Нужна сессия рабочего стола X11 или XWayland. Нужны Python 3.10+ и Tk.
Для иных систем установите те же средства через свой менеджер пакетов.

```sh
sudo apt update
sudo apt install python3 python3-venv python3-tk libsecret-tools pkexec
python3 -m venv .venv
. .venv/bin/activate
python -m pip install --upgrade pip
python -m pip install '.[build,desktop]'
```

Убедитесь, что Tk открывает окно. Закройте его перед следующим шагом:

```sh
python -m tkinter
python -m unittest discover -s tests -p 'test_*.py' -v
python build_portable.py
```

Если тесты упали, сначала разберите отчёт; не считайте сборку проверенной.
Скрипт сам проверит CLI и открытие GUI. В тестах DNS заменён подменами;
они не меняют DNS и не включают автозапуск.

Результат для x64:

```text
dist-portable/dagami-linux-x64/dagami-agent-proxy
dist-portable/dagami-agent-proxy-linux-x64.zip
dist-portable/SHA256-linux-x64.json
```

На ARM64 вместо `x64` будет `arm64`. Главный файл один, отдельного CLI нет.
Запуск из папки с кодом:

```sh
./dist-portable/dagami-linux-x64/dagami-agent-proxy
./dist-portable/dagami-linux-x64/dagami-agent-proxy --help
```

После распаковки готового ZIP в новую папку:

```sh
chmod +x dagami-agent-proxy
./dagami-agent-proxy
```

Для сборки на узле без дисплея нужен Xvfb. Это проверяет открытие окна,
но не работу трея и сети в сессии пользователя:

```sh
sudo apt install xvfb
xvfb-run -a python -m unittest discover -s tests -p 'test_*.py' -v
xvfb-run -a python build_portable.py
```

Трей зависит от панели. Базовая сборка может использовать XEmbed в X11.
Для AppIndicator нужны также GTK, его typelib и PyGObject в том же venv.
В GNOME может быть нужно расширение панели. При недоступном трее окно
остаётся открытым; выйти можно через «Ещё» → «Выход из Dagami Agent Proxy».

## 3. macOS: Intel и Apple Silicon

Установите Python 3.12 с [python.org](https://www.python.org/downloads/macos/).
Его установщик содержит Tcl/Tk. Не используйте старый системный Python.
После установки запустите `Install Certificates.command` из папки
`/Applications/Python 3.12`, чтобы у Python были корни TLS.

В Terminal, в папке с кодом:

```sh
python3.12 -m venv .venv
. .venv/bin/activate
python -m pip install --upgrade pip
python -m pip install '.[build,desktop]'
python -m tkinter
```

Закройте тестовое окно Tk. Затем:

```sh
python -m unittest discover -s tests -p 'test_*.py' -v
python build_portable.py
```

Если Python уже был взят из Homebrew, Tk должен быть из той же версии:
`brew install python@3.12 python-tk@3.12`, затем используйте `python3.12`.
Не смешивайте Python Intel с Tk ARM. На Apple Silicon для своей ARM-сборки
используйте нативный Terminal и Python, без Rosetta.

Результат на Apple Silicon:

```text
dist-portable/dagami-macos-arm64/Dagami Agent Proxy.app
dist-portable/dagami-agent-proxy-macos-arm64.zip
dist-portable/SHA256-macos-arm64.json
```

На Intel вместо `arm64` будет `x64`. Запуск:

```sh
open "dist-portable/dagami-macos-arm64/Dagami Agent Proxy.app"
"dist-portable/dagami-macos-arm64/Dagami Agent Proxy.app/Contents/MacOS/Dagami Agent Proxy" --help
```

Для установки перенесите **весь `.app`** в `Applications`. Библиотеки и
ресурсы внутри нужны; один внутренний файл отдельно не переносите.
Отдельный CLI-файл не нужен: команды принимает тот же главный файл.
ZIP уже хранит ссылки внутри bundle. При своей упаковке используйте
`ditto` либо `zip -y`, чтобы не потерять ссылки.

В этом выпуске нет подписи Developer ID и notarization. Для файла,
который вы собрали из проверенного кода, macOS может запросить явное
разрешение в Finder или в «Конфиденциальность и безопасность».
Не отключайте защиту ОС целиком. Для выдачи своей сборки другим людям
подпишите и заверьте её на Mac своим Developer ID.

## 4. Проверить перед записью DNS

Сначала проверьте обычный запуск, профиль прокси, запуск IDE и выход.
Затем откройте окно DNS и список подключений. Просмотр не требует root.
Изменение DNS — отдельное действие с запросом прав ОС и копией.

- Linux: общий DNS поддержан через NetworkManager. Режим доменов требует
  **уже включённый** плагин dnsmasq. Программа сама его не включает.
- macOS: общий DNS задаётся через networksetup, домены — через resolver.
- Свои службы VPN, политики и иной менеджер DNS могут быть вне этих путей.
- Полный сброс красной кнопкой охватывает и чужие DNS в этих путях.
  Он требует два ответа; на рабочей сети применяйте его осознанно.

Не меняйте общий DNS ради проверки сборки. Живую запись и откат
проверяйте отдельно там, где можно вернуть сеть. Код хранит копию,
но пока нет нашего подтверждения работы DNS на Linux/macOS.

Автозапуск включается явно в GUI; на Linux это XDG, на macOS — LaunchAgent.
Пароли хранятся в Secret Service или Keychain. Если хранилище закрыто,
разблокируйте его. Встроенный Windows-обновлятор и окно активации пока
не перенесены; новые версии Linux/macOS ставятся вручную.

## 5. Частые сбои

| Сообщение / симптом | Действие |
|---|---|
| Нет `_tkinter` | Поставьте Tk для того же Python, затем заново создайте venv |
| Нет `DISPLAY` | Запустите из рабочего стола либо через Xvfb для сборки |
| pip не даёт менять среду ОС | Создайте и включите `.venv`; не применяйте sudo pip |
| Нет tray-пакетов | В активном venv повторите `python -m pip install '.[build,desktop]'` |
| На Linux нет режима доменов | Нужен уже настроенный NM/dnsmasq; общий DNS — лишь отдельным выбором |
| Запрос прав отменён | Настройки не считайте применёнными; прочитайте отчёт окна DNS |
| macOS не находит сертификат TLS | Запустите `Install Certificates.command` того Python, которым шла сборка |
| Старый EXE открывает другое окно | Полностью закройте прежнюю копию в трее перед сменой версии |

Сведения о методах и копиях: [PORTABLE.md](PORTABLE.md).
Основа плана: [Python/Tk на Mac](https://www.python.org/download/mac/tcltk/),
[PyInstaller для разных ОС](https://pyinstaller.org/en/stable/usage.html),
[ссылки в архивах](https://pyinstaller.org/en/stable/common-issues-and-pitfalls.html),
[трей Linux](https://pystray.readthedocs.io/en/latest/faq.html).
