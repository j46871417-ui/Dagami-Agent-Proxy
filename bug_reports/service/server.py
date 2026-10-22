"""Private bug reports. Run behind the same-origin HTTPS nginx reverse proxy."""
import argparse
import base64
import hashlib
import hmac
import io
import json
import os
import re
import secrets
import sqlite3
import threading
import time
import warnings
from contextlib import contextmanager
from collections import OrderedDict
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

from PIL import Image, ImageOps

API = '/api/bug-reports/v1'
STATUSES = ('new', 'triaged', 'in_progress', 'fixed', 'closed')
MAX_IMAGE = 2 * 1024 * 1024
MAX_BODY = 3 * 1024 * 1024
Image.MAX_IMAGE_PIXELS = 12_000_000


def clean(value):
    value = str(value)
    value = re.sub(r'(?im)\b(authorization|cookie|set-cookie)["\']?\s*[:=][^\r\n]*', r'\1=[removed]', value)
    value = re.sub(r'(?i)\b(?:bearer|basic)\s+[a-z0-9+/_.=:-]+', '[authorization removed]', value)
    value = re.sub(r'(?i)(https?|socks5h?)://[^\s<>"\']+', lambda m: m[1] + '://[address removed]', value)
    value = re.sub(r'(?i)\b(access_token|refresh_token|api[_-]?key|password|passwd|secret|token|state|code|code_verifier|authorization|cookie|license(?:key)?)\b["\']?\s*[:=]\s*(?:"[^"\r\n]*"|\'[^\'\r\n]*\'|[^\s,;]+)', r'\1=[removed]', value)
    value = re.sub(r'(?i)DAGAMI-(?:[A-Z0-9]+-)*[A-Z0-9]+', '[license removed]', value)
    value = re.sub(r'(?i)[a-z]:\\Users\\[^\\\r\n]+', r'C:\\Users\\[user]', value)
    value = re.sub(r'/(?:home|Users)/[^/\s]+', '/home/[user]', value)
    value = re.sub(r'[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}', '[email removed]', value)
    return value.replace('\x00', '')


def password_hash(password, salt=None):
    salt = salt or secrets.token_hex(16)
    return salt + '$' + hashlib.pbkdf2_hmac('sha256', password.encode(), bytes.fromhex(salt), 600_000).hex()


class Failure(Exception):
    def __init__(self, status, message):
        self.status, self.message = status, message


class Reports:
    def __init__(self, database, admin_hash, origin='https://dns.dagami.ru', secure=True, max_reports=5000):
        self.database, self.admin_hash, self.origin, self.secure = str(database), admin_hash, origin, secure
        self.max_reports = max_reports
        self.sessions, self.rates = {}, OrderedDict()
        self.lock = threading.Lock()
        self.slots = threading.BoundedSemaphore(2)
        Path(database).parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as db:
            db.executescript('''PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS reports(
              id TEXT PRIMARY KEY, receipt_hash TEXT NOT NULL, payload_hash TEXT NOT NULL,
              created INTEGER NOT NULL, updated INTEGER NOT NULL, status TEXT NOT NULL,
              title TEXT NOT NULL, description TEXT NOT NULL, contact TEXT NOT NULL,
              version TEXT NOT NULL, platform TEXT NOT NULL, logs TEXT NOT NULL, image BLOB);
            CREATE INDEX IF NOT EXISTS reports_created ON reports(created);
            ''')

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.database, timeout=10)
        db.row_factory = sqlite3.Row
        try:
            with db:
                yield db
        finally:
            db.close()

    def rate(self, key, limit, period):
        now = time.monotonic()
        with self.lock:
            while self.rates and next(iter(self.rates.values()))[1] < now:
                self.rates.popitem(last=False)
            count, expiry = self.rates.pop(key, (0, now + period))
            if expiry < now:
                count, expiry = 0, now + period
            self.rates[key] = (count + 1, expiry)
            if len(self.rates) > 10000:
                self.rates.popitem(last=False)
            if count >= limit:
                raise Failure(429, 'Слишком много запросов. Повторите позже.')

    def submit(self, payload):
        def field(name, maximum, minimum=0):
            value = payload.get(name, '')
            if not isinstance(value, str) or not minimum <= len(value.strip()) <= maximum:
                raise Failure(400, 'Проверьте поле: ' + name)
            return value.strip()
        ident = field('id', 36, 36)
        if not re.fullmatch(r'[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}', ident):
            raise Failure(400, 'Некорректный номер отчёта.')
        token = field('receipt_token', 128, 40)
        if not re.fullmatch(r'[A-Za-z0-9_-]{40,128}', token):
            raise Failure(400, 'Некорректный ключ отчёта.')
        values = [clean(field('title', 160, 3)), clean(field('description', 12000, 10)),
                  field('contact', 200), clean(field('version', 100)), clean(field('platform', 200)),
                  clean(field('logs', 65536))]
        image = payload.get('image_base64', '')
        if not isinstance(image, str) or len(image) > (MAX_IMAGE * 4 // 3 + 4):
            raise Failure(413, 'Картинка должна быть не больше 2 МБ.')
        # Hash the submitted content, not a decoder-dependent image encoding.
        digest = hashlib.sha256(json.dumps([values, image], ensure_ascii=False).encode()).hexdigest()
        receipt = hashlib.sha256(token.encode()).hexdigest()
        with self.connect() as db:
            old = db.execute('SELECT receipt_hash,payload_hash FROM reports WHERE id=?', (ident,)).fetchone()
            if old:
                if not hmac.compare_digest(receipt, old[0]) or not hmac.compare_digest(digest, old[1]):
                    raise Failure(409, 'Этот номер уже использован другим отчётом.')
                return {'id': ident, 'duplicate': True}
        picture = None
        if image:
            try:
                raw = base64.b64decode(image, validate=True)
                if len(raw) > MAX_IMAGE:
                    raise ValueError()
                with warnings.catch_warnings():
                    warnings.simplefilter('error', Image.DecompressionBombWarning)
                    with Image.open(io.BytesIO(raw)) as im:
                        if im.format not in ('PNG', 'JPEG') or im.width * im.height > Image.MAX_IMAGE_PIXELS:
                            raise ValueError()
                        im.load()
                        # Rebuild pixels: EXIF/XMP/ICC and untrusted trailing bytes never survive.
                        im = ImageOps.exif_transpose(im).convert('RGB')
                        pixels = Image.new('RGB', im.size)
                        pixels.paste(im)
                        out = io.BytesIO()
                        pixels.save(out, format='PNG')
                        picture = out.getvalue()
                        if len(picture) > 6 * 1024 * 1024:
                            raise ValueError()
            except (ValueError, OSError, SyntaxError, Image.DecompressionBombWarning, Image.DecompressionBombError):
                raise Failure(400, 'Нужна корректная PNG/JPEG до 2 МБ и 12 мегапикселей.')
        now = int(time.time())
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            if db.execute('SELECT count(*) FROM reports').fetchone()[0] >= self.max_reports:
                raise Failure(503, 'Хранилище отчётов заполнено. Попробуйте позже.')
            if db.execute('SELECT coalesce(sum(length(cast(logs AS blob))+length(cast(description AS blob))+coalesce(length(image),0)),0) FROM reports').fetchone()[0] + len(picture or b'') + len(values[-1].encode()) + len(values[1].encode()) > 512 * 1024 * 1024:
                raise Failure(503, 'Хранилище отчётов заполнено. Попробуйте позже.')
            try:
                db.execute('INSERT INTO reports VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)',
                           (ident, receipt, digest, now, now, 'new', *values, picture))
            except sqlite3.IntegrityError:
                # A concurrent identical retry must behave like a serial retry.
                old = db.execute('SELECT receipt_hash,payload_hash FROM reports WHERE id=?', (ident,)).fetchone()
                if not old or not hmac.compare_digest(receipt, old[0]) or digest != old[1]:
                    raise Failure(409, 'Этот номер уже использован другим отчётом.')
        return {'id': ident, 'duplicate': False}


def handler(app):
    class Handler(BaseHTTPRequestHandler):
        server_version = 'DagamiReports'
        def log_message(self, *args):
            pass  # Never write report bodies, credentials or contact details to access logs.

        def reply(self, status, value, content_type='application/json; charset=utf-8', cookie=None):
            data = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(data)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'")
            if cookie:
                self.send_header('Set-Cookie', cookie)
            self.end_headers()
            self.wfile.write(data)

        def body(self):
            if self.headers.get_content_type() != 'application/json' or self.headers.get('Transfer-Encoding'):
                raise Failure(415, 'Ожидается JSON.')
            try:
                length = int(self.headers.get('Content-Length', '-1'))
            except ValueError:
                raise Failure(400, 'Некорректный размер запроса.')
            if not 0 <= length <= MAX_BODY:
                raise Failure(413, 'Слишком большой отчёт.')
            self.connection.settimeout(15)
            try:
                payload = json.loads(self.rfile.read(length))
            except (ValueError, UnicodeError):
                raise Failure(400, 'Некорректный JSON.')
            if not isinstance(payload, dict):
                raise Failure(400, 'Ожидается объект.')
            return payload

        def session(self, csrf=False):
            cookies = SimpleCookie()
            try:
                cookies.load(self.headers.get('Cookie', ''))
                token = cookies['dagami_reports'].value
            except (KeyError, ValueError):
                raise Failure(401, 'Войдите в панель отчётов.')
            with app.lock:
                item = app.sessions.get(token)
            if not item or item[0] < time.monotonic():
                raise Failure(401, 'Сессия истекла. Войдите снова.')
            if csrf and not hmac.compare_digest(self.headers.get('X-CSRF-Token', ''), item[1]):
                raise Failure(403, 'Обновите страницу и повторите действие.')
            return token, item[1]

        def cookie(self, token, age=28800):
            return 'dagami_reports=' + token + '; Path=' + API + '; HttpOnly; SameSite=Strict; Max-Age=' + str(age) + ('; Secure' if app.secure else '')

        def do_GET(self):
            self.dispatch('GET')

        def do_POST(self):
            self.dispatch('POST')

        def dispatch(self, method):
            if not app.slots.acquire(blocking=False):
                self.reply(503, {'error': 'Сервис занят. Повторите отправку позже.'})
                return
            try:
                self.route(method)
            except Failure as ex:
                self.reply(ex.status, {'error': ex.message})
            except (BrokenPipeError, ConnectionResetError, TimeoutError):
                pass
            except Exception:
                self.reply(500, {'error': 'Сервис временно недоступен. Повторите позже.'})
            finally:
                app.slots.release()

        def route(self, method):
            split = urlsplit(self.path)
            path = split.path
            if not path.startswith(API + '/'):
                raise Failure(404, 'Не найдено.')
            origin = self.headers.get('Origin')
            if origin and origin != app.origin:
                raise Failure(403, 'Недопустимый источник запроса.')
            if method == 'GET' and path == API + '/health':
                return self.reply(200, {'ok': True})
            # Trust only the nginx-supplied header; the service binds loopback.
            ip = self.headers.get('X-Real-IP', self.client_address[0])
            if method == 'POST' and path == API + '/reports':
                app.rate(('report', ip), 12, 3600)
                return self.reply(201, app.submit(self.body()))
            if method == 'POST' and path == API + '/admin/login':
                app.rate(('login', ip), 8, 900)
                app.rate(('login-global', ''), 80, 900)
                body = self.body()
                password = body.get('password', '')
                if not isinstance(password, str) or len(password) > 256:
                    raise Failure(401, 'Неверный пароль.')
                salt = app.admin_hash.split('$')[0]
                if not hmac.compare_digest(password_hash(password, salt), app.admin_hash):
                    raise Failure(401, 'Неверный пароль.')
                token, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
                with app.lock:
                    now = time.monotonic()
                    app.sessions = {k: v for k, v in app.sessions.items() if v[0] > now}
                    if len(app.sessions) >= 100:
                        raise Failure(503, 'Слишком много сессий.')
                    app.sessions[token] = (now + 28800, csrf)
                return self.reply(200, {'csrf': csrf}, cookie=self.cookie(token))
            if path.startswith(API + '/admin/'):
                token, csrf = self.session(method == 'POST')
                if method == 'GET' and path == API + '/admin/session':
                    return self.reply(200, {'csrf': csrf})
                if method == 'POST' and path == API + '/admin/logout':
                    with app.lock:
                        app.sessions.pop(token, None)
                    return self.reply(200, {'ok': True}, cookie=self.cookie('', 0))
                if method == 'GET' and path == API + '/admin/reports':
                    query = parse_qs(split.query)
                    status = query.get('status', [''])[0]
                    try:
                        offset = max(0, min(5000, int(query.get('offset', ['0'])[0])))
                    except ValueError:
                        raise Failure(400, 'Некорректная страница.')
                    if status and status not in STATUSES:
                        raise Failure(400, 'Некорректный статус.')
                    where, args = ('WHERE status=?', [status]) if status else ('', [])
                    with app.connect() as db:
                        total = db.execute('SELECT count(*) FROM reports ' + where, args).fetchone()[0]
                        rows = db.execute('SELECT id,created,updated,status,title,version,(image IS NOT NULL) AS has_image FROM reports ' + where + ' ORDER BY created DESC,id LIMIT 50 OFFSET ?', [*args, offset]).fetchall()
                    return self.reply(200, {'reports': [dict(row) for row in rows], 'total': total})
                match = re.fullmatch(re.escape(API) + r'/admin/reports/([0-9a-f-]{36})(/image|/status)?', path)
                if match:
                    ident, action = match.groups()
                    saved = False
                    with app.connect() as db:
                        row = db.execute('SELECT * FROM reports WHERE id=?', (ident,)).fetchone()
                        if not row:
                            raise Failure(404, 'Отчёт не найден.')
                        if method == 'GET' and action == '/image':
                            if row['image'] is None:
                                raise Failure(404, 'Картинка не приложена.')
                            return self.reply(200, row['image'], 'image/png')
                        if method == 'GET' and action is None:
                            return self.reply(200, {k: row[k] for k in ('id', 'created', 'updated', 'status', 'title', 'description', 'contact', 'version', 'platform', 'logs')} | {'has_image': row['image'] is not None})
                        if method == 'POST' and action == '/status':
                            status = self.body().get('status')
                            if status not in STATUSES:
                                raise Failure(400, 'Некорректный статус.')
                            db.execute('UPDATE reports SET status=?,updated=? WHERE id=?', (status, int(time.time()), ident))
                            saved = True
                    # Do not acknowledge a mutation until the transaction commits.
                    # A following GET can otherwise race the previous handler.
                    if saved:
                        return self.reply(200, {'ok': True})
                raise Failure(404, 'Не найдено.')
            match = re.fullmatch(re.escape(API) + r'/reports/([0-9a-f-]{36})', path)
            if method == 'GET' and match:
                app.rate(('status', ip), 120, 900)
                bearer = self.headers.get('Authorization', '')
                if not bearer.startswith('Bearer '):
                    raise Failure(404, 'Отчёт не найден.')
                with app.connect() as db:
                    row = db.execute('SELECT receipt_hash,status,updated FROM reports WHERE id=?', (match[1],)).fetchone()
                if not row or not hmac.compare_digest(hashlib.sha256(bearer[7:].encode()).hexdigest(), row[0]):
                    raise Failure(404, 'Отчёт не найден.')
                return self.reply(200, {'id': match[1], 'status': row[1], 'updated': row[2]})
            raise Failure(404, 'Не найдено.')
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--hash-password', action='store_true')
    parser.add_argument('--port', type=int, default=3091)
    args = parser.parse_args()
    if args.hash_password:
        import getpass
        password = getpass.getpass('Пароль администратора (не менее 16 символов): ')
        if len(password) < 16:
            parser.error('Нужно не менее 16 символов.')
        print(password_hash(password))
        return
    credential = os.environ.get('REPORTS_ADMIN_HASH', '')
    if not re.fullmatch(r'[0-9a-f]{32}\$[0-9a-f]{64}', credential):
        parser.error('Set REPORTS_ADMIN_HASH using --hash-password; never use a default password.')
    app = Reports(os.environ.get('REPORTS_DB', '/var/lib/dagami-reports/reports.sqlite'), credential)
    ThreadingHTTPServer(('127.0.0.1', args.port), handler(app)).serve_forever()


if __name__ == '__main__':
    main()
