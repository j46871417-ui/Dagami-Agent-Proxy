# Сервис баг-репортов

Исходники сервиса, обслуживающего форму Dagami Agent Proxy. Windows-клиент по
умолчанию отправляет на `https://dns.dagami.ru/api/bug-reports/v1/reports`.
Пользователям приложения не нужно устанавливать этот сервис.

`service/`: Python HTTP API, SQLite WAL, обработка PNG/JPEG через Pillow,
регрессионные тесты, обслуживание базы и шаблоны systemd/nginx.
`public/`: форма, закрытая панель и общие стили/тема сайта.
Данные, пароли, сертификаты и параметры доступа к серверу сюда не входят.

## Локальный стенд

Python 3.11+ и отдельный venv:

```sh
python -m pip install -r bug_reports/service/requirements.txt
python -m unittest discover -s bug_reports/service -p test_server.py -v
python bug_reports/service/dev_server.py --data /path/to/private-test-data --port 3092
```

Стенд слушает только `127.0.0.1`. Форма: `/support/agent-proxy/`, панель:
`/support/agent-proxy/admin/`. Пароль стенда: `local-preview-fixture-only`.
Этот пароль предназначен только для локального тестирования; `dev_server.py`
нельзя запускать как production-службу. Production `server.py` требует отдельный
PBKDF2-хеш через `REPORTS_ADMIN_HASH` и не имеет встроенного пароля.

Для Windows-интеграционного теста можно задать `REPORTS_NATIVE_CHECKS` — путь к
`BugReportChecks.exe` рядом с проверяемой сборкой. Тест использует искусственные
данные. Без этой переменной соответствующая проверка пропускается.

## Размещение отдельно от программы

Код `server.py` и `maintenance.py` размещается в `/opt/dagami-reports` с собственным
venv. Создайте отдельного системного пользователя `dagami-reports`. База находится
в `/var/lib/dagami-reports`; доступ только этому пользователю, каталог 0700, umask 0077.

Перед запуском задайте индивидуальный пароль, например:

```sh
umask 077
python3 bug_reports/service/hash_password.py > /path/to/private-admin.hash
```

Хеш записывается в root-only `/etc/dagami-reports.env` как
`REPORTS_ADMIN_HASH=<полученный хеш>`, mode 0600. Пароль не передаётся через argv.
Для создания хеша с Pillow установленным также доступен `server.py --hash-password`.
Шаблоны systemd используют этот env-файл; HTTP API слушает только loopback 3091.

Статику `public/support/agent-proxy` разместите в web root существующего сайта.
Общие `site-theme.js`, `dagami-site.css` и `site-preferences.css` также должны быть
доступны в корне. `service/nginx.conf` включается только в HTTPS-vhost; сначала
проверить существующие маршруты, сохранить конфиги и выполнить `nginx -t`.
Не открывайте внешний порт 3091. Для другого домена измените допустимый origin в
сервере и адрес клиента, затем повторите проверки HTTPS/cookies/CSRF.

Для реальной установки используйте отдельный production-пароль, проверьте права,
запуск службы и маршруты через HTTPS. Активация systemd/timer — отдельный шаг
администратора; эти исходники сами ничего не развёртывают. Перед обновлением
сделайте online backup SQLite и сохраните предыдущую версию runtime/статических файлов.

## Обслуживание и проверки

Daily timer делает online backup, удаляет обращения старше 90 дней и хранит снимки
за 3 дня. Периоды требуют работающего timer; удалённые записи могут оставаться
в копиях до следующей очистки. SQLite с активным WAL нельзя резервировать простым
копированием одного `.sqlite`: используйте SQLite backup API.

Обязательны проверки: без входа admin API даёт 401; отчёт с журналом/картинкой
создаётся, повтор не дублируется; вход/CSRF/статус/выход работают; секреты и метаданные
картинки удалены; прежние маршруты сайта и HTTPS сохраняются. Не публикуйте рабочую
базу, снимки или куки администратора. API не отправляет почту/Telegram-сообщения.
