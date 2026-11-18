#!/usr/bin/env python3
"""Private, SSH-only provider inventory. Never expose this listener publicly."""
import argparse
from contextlib import contextmanager
import datetime as dt
import hmac
import html
import http.cookies
import ipaddress
import json
import os
from pathlib import Path
import secrets
import sqlite3
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, quote, urlsplit

UTC = dt.timezone.utc
OWNER_TZ = dt.timezone(dt.timedelta(hours=5), 'Екатеринбург')
COOKIE_NAME = 'dagami_pool_owner_v2'
SCHEMES = {'http', 'https', 'socks4', 'socks4a', 'socks5', 'socks5h', 'socks5s'}

def same_secret(left, right):
    return hmac.compare_digest(left.encode('utf-8'), right.encode('utf-8'))


def atomic(path, data, mode=0o600):
    path = Path(path)
    fd, name = tempfile.mkstemp(prefix='.pool-', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as file:
            os.fchmod(file.fileno(), mode)
            file.write(data)
            file.flush()
            os.fsync(file.fileno())
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


def proxy_url(value):
    """Do not include rejected input in exceptions: it may contain a password."""
    try:
        parsed = urlsplit(value)
        if parsed.scheme not in SCHEMES or not parsed.hostname or parsed.query or parsed.fragment or parsed.path not in ('', '/'):
            raise ValueError()
        if not parsed.port or not 1 <= parsed.port <= 65535 or len(value) > 2048 or any(ord(c) < 33 for c in value):
            raise ValueError()
        try:
            address = ipaddress.ip_address(parsed.hostname)
            if not address.is_global:
                raise ValueError()
        except ValueError:
            # DNS names are resolved by the transport; obvious local names denied.
            if ':' in parsed.hostname or parsed.hostname.replace('.', '').isdigit() or '.' not in parsed.hostname or parsed.hostname.endswith(('.local', '.localhost', '.internal')):
                raise ValueError()
        if parsed.scheme in ('socks4', 'socks4a') and parsed.password is not None:
            raise ValueError()
        return value
    except (ValueError, TypeError):
        raise ValueError('Нужен корректный URL прокси с протоколом и портом. Спецсимволы логина и пароля кодируются как %XX.') from None


def expiry(days='', until='', now=None):
    now = time.time() if now is None else now
    if days and until:
        raise ValueError('Укажите срок в днях или дату, один вариант.')
    if days:
        try:
            count = int(days)
        except ValueError:
            raise ValueError('Срок в днях должен быть целым числом.') from None
        if not 1 <= count <= 3650:
            raise ValueError('Срок: от 1 до 3650 дней.')
        return now + count * 86400
    if until:
        try:
            date = dt.datetime.fromisoformat(until)
            if date.tzinfo is None:
                date = date.replace(tzinfo=OWNER_TZ)
            stamp = date.timestamp()
        except ValueError:
            raise ValueError('Некорректная дата.') from None
        if stamp <= now:
            raise ValueError('Дата должна быть в будущем; время указано по Екатеринбургу.')
        return stamp
    return None


class Inventory:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.lock = threading.RLock()
        self.db = self.directory / 'providers.db'
        self.base = self.directory / 'base-routes.json'
        with self.connect() as db:
            db.execute('CREATE TABLE IF NOT EXISTS providers (id TEXT PRIMARY KEY, label TEXT NOT NULL, url TEXT NOT NULL, enabled INTEGER NOT NULL, expires REAL, priority INTEGER NOT NULL, created REAL NOT NULL)')
        os.chmod(self.db, 0o600)
        self.publish()

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.db, timeout=5)
        try:
            db.execute('PRAGMA secure_delete=ON')
            with db:
                yield db
        finally:
            db.close()

    def rows(self):
        with self.connect() as db:
            db.row_factory = sqlite3.Row
            return [dict(row) for row in db.execute('SELECT * FROM providers ORDER BY priority,created,id')]

    def save(self, fields):
        label = fields.get('label', '').strip()
        if not label or len(label) > 80 or any(ord(c) < 32 for c in label):
            raise ValueError('Название: от 1 до 80 символов.')
        url = fields.get('url', '').strip()
        if not url:
            scheme = fields.get('protocol', 'http')
            host = fields.get('host', '').strip()
            if ':' in host and not host.startswith('['):
                host = '[' + host + ']'
            credentials = ''
            if fields.get('username'):
                credentials = quote(fields['username'], safe='') + ':' + quote(fields.get('password', ''), safe='') + '@'
            url = scheme + '://' + credentials + host + ':' + fields.get('port', '')
        url = proxy_url(url)
        expires = expiry(fields.get('days', ''), fields.get('until', ''))
        try:
            priority = int(fields.get('priority', '100'))
            if not 1 <= priority <= 9999:
                raise ValueError()
        except ValueError:
            raise ValueError('Приоритет: от 1 до 9999; меньше — предпочтительнее.') from None
        with self.lock, self.connect() as db:
            if db.execute('SELECT COUNT(*) FROM providers').fetchone()[0] >= 24:
                raise ValueError('Сначала удалите неиспользуемый прокси: в пуле уже 24 записи.')
            identifier = 'paid_' + secrets.token_hex(8)
            db.execute('INSERT INTO providers VALUES (?,?,?,?,?,?,?)', (identifier, label, url, 1, expires, priority, time.time()))
        self.publish()
        return identifier

    def change(self, fields):
        identifier, action = fields.get('id', ''), fields.get('action', '')
        with self.lock, self.connect() as db:
            row = db.execute('SELECT id FROM providers WHERE id=?', (identifier,)).fetchone()
            if row is None:
                raise ValueError('Запись уже удалена.')
            if action == 'delete':
                db.execute('DELETE FROM providers WHERE id=?', (identifier,))
                # Secure delete prevents old plaintext credentials remaining in DB pages.
            elif action in ('enable', 'disable'):
                db.execute('UPDATE providers SET enabled=? WHERE id=?', (int(action == 'enable'), identifier))
            elif action == 'expiry':
                stamp = expiry(fields.get('days', ''), fields.get('until', ''))
                db.execute('UPDATE providers SET expires=? WHERE id=?', (stamp, identifier))
            else:
                raise ValueError('Неизвестное действие.')
        self.publish()

    def preference(self, paid_first):
        if paid_first not in ('yes', 'no'):
            raise ValueError('Некорректный режим.')
        with self.lock:
            atomic(self.directory / 'settings.json', json.dumps({'paid_first': paid_first == 'yes'}))
        self.publish()

    def paid_first(self):
        path = self.directory / 'settings.json'
        return path.exists() and json.loads(path.read_text()).get('paid_first') is True

    def publish(self, now=None):
        now = time.time() if now is None else now
        with self.lock:
            with self.connect() as db:
                db.execute('PRAGMA secure_delete=ON')
                # Expiry deletes credentials as well as removing the active route.
                db.execute('DELETE FROM providers WHERE expires IS NOT NULL AND expires<=?', (now,))
            base = json.loads(self.base.read_text())
            routes = []
            for row in self.rows():
                if not row['enabled']:
                    continue
                route = {'name': row['id'], 'proxy': row['url'], 'require_foreign': True}
                if row['expires'] is not None:
                    route['expires_at'] = dt.datetime.fromtimestamp(row['expires'], UTC).isoformat()
                routes.append(route)
            routes = routes + base['routes'] if self.paid_first() else base['routes'] + routes
            if len(routes) > 32:
                raise ValueError('Слишком много маршрутов.')
            data = json.dumps({'source_ip': base['source_ip'], 'routes': routes}, separators=(',', ':'))
            if len(data.encode()) > 65536:
                raise ValueError('Слишком большой список маршрутов.')
            destination = self.directory / 'routes.json'
            if not destination.exists() or destination.read_text() != data:
                atomic(destination, data, 0o640)


class Handler(BaseHTTPRequestHandler):
    server_version = 'Dagami-Pool'

    def log_message(self, *_):
        pass  # Never log request bodies, provider URLs, login tokens or cookies.

    def reply(self, status, body, cookie=None):
        content = body.encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(content)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Referrer-Policy', 'no-referrer')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Content-Security-Policy', "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'")
        if cookie:
            self.send_header('Set-Cookie', cookie)
        self.end_headers()
        self.wfile.write(content)

    def allowed_host(self):
        return self.headers.get('Host') in (f'127.0.0.1:{self.server.server_port}', f'localhost:{self.server.server_port}')

    def authenticated(self):
        cookie = http.cookies.SimpleCookie()
        try:
            cookie.load(self.headers.get('Cookie', ''))
            return same_secret(cookie[COOKIE_NAME].value, self.server.session)
        except (KeyError, http.cookies.CookieError):
            return False

    def do_GET(self):
        if not self.allowed_host() or self.path != '/':
            self.reply(404, 'Не найдено')
            return
        if not self.authenticated():
            self.reply(200, page('<h1>Управление прокси Dagami</h1><form method="post" action="/login"><input name="csrf" type="hidden" value="' + self.server.csrf + '"><label>Пароль панели <input name="password" type="password" required autocomplete="current-password"></label><button>Войти</button></form><p>Доступ только через SSH-туннель владельца.</p>'))
            return
        self.reply(200, self.dashboard())

    def do_POST(self):
        if not self.allowed_host():
            self.reply(403, 'Запрещено'); return
        origin = self.headers.get('Origin')
        if (origin and origin != 'null' and origin != 'http://' + self.headers['Host']):
            self.reply(403, page('<p>Запрос пришёл с другого адреса. <a href="/">Откройте панель заново</a>.</p>')); return
        try:
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= 8192 or self.headers.get_content_type() != 'application/x-www-form-urlencoded':
                self.reply(413, 'Слишком большой запрос'); return
            self.connection.settimeout(5)
            values = parse_qs(self.rfile.read(length).decode('utf-8'), max_num_fields=20)
            fields = {key: value[0] for key, value in values.items() if len(value) == 1}
        except (ValueError, UnicodeError, TimeoutError):
            self.reply(400, 'Некорректный запрос'); return
        if self.path == '/login':
            if not same_secret(fields.get('csrf', ''), self.server.csrf):
                self.reply(409, page('<p>Страница входа устарела после обновления панели. <a href="/">Открыть свежую страницу входа</a>.</p>')); return
            if not same_secret(fields.get('password', ''), self.server.password):
                time.sleep(1)
                self.reply(403, page('<p>Неверный пароль. <a href="/">Повторить вход</a></p>')); return
            self.reply(200, self.dashboard('Вход выполнен.'), COOKIE_NAME + '=' + self.server.session + '; HttpOnly; SameSite=Strict; Path=/')
            return
        if not self.authenticated():
            self.reply(401, page('<p>Сеанс панели завершился. <a href="/">Войдите заново</a>. Изменения не применялись.</p>')); return
        if not same_secret(fields.get('csrf', ''), self.server.csrf):
            self.reply(409, page('<p>Форма устарела. <a href="/">Обновите панель</a> и повторите действие. Изменения не применялись.</p>')); return
        try:
            if self.path == '/add':
                self.server.inventory.save(fields)
            elif self.path == '/change':
                self.server.inventory.change(fields)
            elif self.path == '/preference':
                self.server.inventory.preference(fields.get('paid_first'))
            else:
                self.reply(404, 'Не найдено'); return
            self.reply(200, self.dashboard('Изменения сохранены. Новый выход включится после двух успешных проверок.'))
        except ValueError as error:
            self.reply(400, self.dashboard(str(error)))
        except Exception:
            self.reply(503, page('<p>Не удалось сохранить изменения. Реквизиты не выводятся в журнал.</p>'))

    def dashboard(self, message=''):
        inventory = self.server.inventory
        inventory.publish()
        csrf = '<input type="hidden" name="csrf" value="' + self.server.csrf + '">'
        try:
            health = json.loads(self.server.health_file.read_text())
        except (OSError, ValueError):
            health = {}
        cards = []
        for row in inventory.rows():
            label = html.escape(row['label'])
            status = 'Включён' if row['enabled'] else 'Выключен'
            until = dt.datetime.fromtimestamp(row['expires'], OWNER_TZ).strftime('%d.%m.%Y %H:%M (Екатеринбург)') if row['expires'] else 'Без срока'
            checked = []
            for service in ('google', 'openai', 'general'):
                state = health.get(row['id'], {}).get(service, {})
                fresh = False
                try:
                    fresh = time.time() - dt.datetime.fromisoformat(state.get('checked', '').replace('Z', '+00:00')).timestamp() < 180
                except ValueError:
                    pass
                checked.append(service + ': ' + ('работает' if state.get('up') and fresh else 'не проверен / недоступен'))
            form = '<form method="post" action="/change">' + csrf + '<input type="hidden" name="id" value="' + row['id'] + '">'
            cards.append('<section><h2>' + label + '</h2><p>' + status + ' · до ' + until + ' · приоритет ' + str(row['priority']) + '</p><p>' + ' · '.join(checked) + '</p>' + form + '<button name="action" value="' + ('disable' if row['enabled'] else 'enable') + '">' + ('Выключить' if row['enabled'] else 'Включить') + '</button><button name="action" value="delete">Удалить</button></form>' + '<details><summary>Изменить срок</summary>' + form + '<label>Новый срок, дней <input name="days" type="number" min="1" max="3650"></label><label>или до (Екатеринбург) <input name="until" type="datetime-local"></label><button name="action" value="expiry">Сохранить срок</button></form></details></section>')
        preference = '<section><h2>Приватный пул · только по ключу</h2><p>Эти прокси доступны в приложении через отдельную кнопку «Приватные прокси». Публичные прокси работают отдельно и не требуют ключа Dagami.</p></section>'
        add = '<h2>Добавить приватный прокси</h2><form method="post" action="/add">' + csrf + '<label>Название <input name="label" required maxlength="80" placeholder="Например, запасной №1"></label><p>Вставьте готовый URL или заполните поля ниже.</p><label>Готовый URL <input name="url" type="password" autocomplete="new-password" placeholder="https://login:password@host:port"></label><br><label>Протокол <select name="protocol">' + ''.join('<option>' + value + '</option>' for value in ('http', 'https', 'socks5', 'socks5h', 'socks4', 'socks4a', 'socks5s')) + '</select></label><label>Сервер <input name="host" autocomplete="off"></label><label>Порт <input name="port" type="number" min="1" max="65535"></label><label>Логин <input name="username" autocomplete="off"></label><label>Пароль <input name="password" type="password" autocomplete="new-password"></label><br><label>Срок, дней <input name="days" type="number" min="1" max="3650"></label><label>или до (Екатеринбург) <input name="until" type="datetime-local"></label><label>Приоритет среди платных прокси <input name="priority" type="number" value="100" min="1" max="9999"></label><button>Добавить и проверить</button></form>'
        return page('<h1>Мои приватные прокси</h1><p>Реквизиты остаются на сервере и не передаются приложению. Просроченные записи удаляются автоматически. Время — Екатеринбург. Статусы ниже — проверки резервного зарубежного узла; основной проверяет свои маршруты независимо.</p><p role="status">' + html.escape(message) + '</p>' + preference + ''.join(cards) + add + '<p>HTTP CONNECT, HTTPS, SOCKS4/4a, SOCKS5/5h и SOCKS5 over TLS. Для Google/OpenAI сохраняется сквозной TLS. Для включения требуется проверенный зарубежный выход.</p>')


def page(content):
    return '<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Прокси Dagami</title><style>body{font:16px system-ui;max-width:1000px;margin:32px auto;padding:0 20px;color:#ececf4;background:#11121b}section{border:1px solid #45465c;border-radius:12px;padding:16px;margin:16px 0}label{display:inline-flex;flex-direction:column;gap:6px;margin:8px;max-width:100%}input,select,button{box-sizing:border-box;max-width:100%;font:inherit;padding:10px;border-radius:6px;border:1px solid #73758a}button{cursor:pointer;margin:8px;background:#b9a8ff;color:#181025}a{color:#c7baff}p{line-height:1.6}input[type=password]{width:290px}</style><main>' + content + '</main></html>'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--directory', default='/var/lib/dagami-pool')
    parser.add_argument('--port', type=int, default=18995)
    parser.add_argument('--health-file', default='/var/lib/dagami-reserve/health.json')
    args = parser.parse_args()
    os.umask(0o077)
    inventory = Inventory(args.directory)
    password_file = inventory.directory / 'admin-password'
    if not password_file.exists():
        atomic(password_file, secrets.token_urlsafe(32))
    server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
    server.password = password_file.read_text().strip()
    server.session, server.csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    server.inventory = inventory
    server.health_file = Path(args.health_file)
    def expire():
        while True:
            time.sleep(1)
            try:
                inventory.publish()
            except Exception:
                print('Pool publication failed; retaining previous configuration', flush=True)
    threading.Thread(target=expire, daemon=True).start()
    server.serve_forever()


if __name__ == '__main__':
    main()
