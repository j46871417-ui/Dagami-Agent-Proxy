"""Shared session controller for the portable desktop and command line."""
from __future__ import annotations

import contextlib
import hmac
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import re
import secrets
import signal
import socket
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import subprocess
import sys
import threading
import time
from urllib.parse import urlsplit

from .adapters import get_adapter, list_adapters
from .core.launcher import Launcher
from .core.profile import ProxyProfile
from .core.recovery import get_devproxy_state_path, StateManager
from .core.secrets import SecretStore
from .core.tunnel import LocalTunnel
from .core.process_env import external_environment, independent_environment

def redact(text):
    text = re.sub(r'https://accounts\.google\.com/[^\s]+', '[OAuth URL hidden]', str(text))
    return re.sub(r'(?i)(https?|socks5h?)://[^\s/@]+@', r'\1://***@', text)

def runtime_dir():
    path = Path(get_devproxy_state_path()).parent / 'sessions'
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.is_symlink():
        raise RuntimeError('Каталог сессий не должен быть символической ссылкой.')
    if os.name != 'nt':
        if path.stat().st_uid != os.getuid():
            raise RuntimeError('Каталог сессий принадлежит другому пользователю.')
        path.chmod(0o700)
    return path

def app_adapter(app):
    adapter = get_adapter(app)
    if adapter is None or not re.fullmatch(r'[a-z0-9_-]+', app):
        raise ValueError('Неизвестное приложение. Выполните devproxy apps list.')
    return adapter

def load_profile(name):
    data = StateManager.get_profile(name)
    if not data:
        raise ValueError(f'Нет профиля «{name}». Добавьте его командой profile add.')
    password = SecretStore.get_password(name)
    if data.get('requires_password') and password is None:
        raise RuntimeError('Пароль профиля недоступен в хранилище ОС. Разблокируйте Keychain/Secret Service или сохраните профиль заново.')
    return ProxyProfile.from_dict(data, password=password)

def entry_command():
    if getattr(sys, 'frozen', False):
        return [sys.executable]
    source=Path(__file__).resolve().parent.parent / 'devproxy_gui.py'
    return [sys.executable,str(source)] if source.is_file() else [sys.executable,'-m','devproxy_pkg']

def require_stopped(exe):
    if os.name=='nt':return  # Native Windows GUI/CLI has its own process preflight.
    result=subprocess.run(['ps','-axo','pid=,comm='],capture_output=True,text=True,timeout=10,check=True,env=external_environment())
    target=os.path.realpath(exe)
    for line in result.stdout.splitlines():
        fields=line.strip().split(None,1)
        if len(fields)!=2 or not fields[0].isdigit() or int(fields[0]) in (os.getpid(),os.getppid()):continue
        command=fields[1]
        exact=os.path.realpath(command)==target
        linux_name=sys.platform.startswith('linux') and Path(command).name==Path(exe).name and Path(exe).name.lower() not in ('electron','python','python3','bash','sh')
        if exact or linux_name:raise RuntimeError('Приложение уже открыто вне этой сессии. Полностью закройте его и повторите запуск через Dagami Agent Proxy.')

def executable(adapter):
    path=StateManager.load_state().get('executables',{}).get(adapter.app_id) or adapter.detect_executable()
    if not path or not Path(path).is_file():raise RuntimeError('Приложение не найдено. Укажите путь: devproxy register APP PATH')
    if os.name!='nt' and not os.access(path,os.X_OK):raise RuntimeError('У файла приложения нет права исполнения.')
    return str(Path(path).absolute())

def register_executable(app,path):
    app_adapter(app);path=Path(path).expanduser().absolute()
    if path.suffix=='.app' and path.is_dir():
        import plistlib
        info=plistlib.loads((path/'Contents'/'Info.plist').read_bytes());name=info.get('CFBundleExecutable','')
        if not name or Path(name).name!=name:raise ValueError('Некорректный CFBundleExecutable.')
        path=path/'Contents'/'MacOS'/name
    if not path.is_file():raise ValueError('Укажите исполняемый файл или .app.')
    state=StateManager.load_state();state.setdefault('executables',{})[app]=str(path);StateManager.save_state(state)
    return str(path)

def launch_desktop_app(app,profile):
    if not app_adapter(app).is_cli:return start_session(app,profile)
    command=entry_command()+['run',app,'--profile',profile]
    if os.name=='nt':
        subprocess.Popen(command,creationflags=subprocess.CREATE_NEW_CONSOLE,env=independent_environment());return 'Открыта отдельная консоль приложения.'
    if sys.platform=='darwin':
        import shlex
        path=runtime_dir()/(app+'-terminal.command')
        path.write_text('#!/bin/sh\nexec '+shlex.join(command)+'\n',encoding='utf-8');path.chmod(0o700)
        subprocess.run(['/usr/bin/open','-a','Terminal',str(path)],check=True,env=independent_environment());return 'Открыт Terminal.'
    import shutil
    for terminal,flag in [('x-terminal-emulator','-e'),('gnome-terminal','--'),('konsole','-e'),('xterm','-e')]:
        if shutil.which(terminal):subprocess.Popen([terminal,flag]+command,start_new_session=True,env=independent_environment());return 'Открыт терминал приложения.'
    raise RuntimeError('Терминал не найден. Выполните в своей консоли: devproxy run '+app+' --profile '+profile)

def descriptor(app):
    app_adapter(app)
    return runtime_dir() / f'{app}.json'

def request(app, command='status', **data):
    """No network discovery: only a token-protected loopback session descriptor."""
    path = descriptor(app)
    try:
        record = json.loads(path.read_text(encoding='utf-8'))
    except FileNotFoundError:
        return None
    if not isinstance(record.get('port'), int) or not 1 <= record['port'] <= 65535 or not re.fullmatch('[a-f0-9]{64}', record.get('token', '')):
        raise RuntimeError('Повреждён дескриптор сессии. Проверьте журнал.')
    body = json.dumps(dict(command=command, **data)).encode()
    if len(body) > 32768:
        raise ValueError('Запрос слишком большой.')
    import http.client
    client = http.client.HTTPConnection('127.0.0.1', record['port'], timeout=60 if command in ('open','login') else 15)
    try:
        client.request('POST', '/', body, {'Authorization': 'Bearer ' + record['token'], 'Content-Type': 'application/json'})
        reply = client.getresponse()
        result = json.loads(reply.read(32768))
        if reply.status != 200:
            raise RuntimeError(result.get('error', 'Сессия отклонила запрос.'))
        return result
    except (ConnectionRefusedError, ConnectionResetError, socket.timeout) as exc:
        if command != 'status':
            raise RuntimeError('Сессия не подтвердила команду. Проверьте её статус и журнал перед повтором.') from exc
        return None
    finally:
        client.close()

@contextlib.contextmanager
def session_lock(app):
    path = runtime_dir() / f'{app}.lock'
    with path.open('a+b') as stream:
        stream.seek(0); stream.write(b'0'); stream.flush(); stream.seek(0)
        try:
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise RuntimeError('Другая сессия уже запускается. Повторите через несколько секунд.') from exc
        try:
            yield
        finally:
            if os.name == 'nt':
                stream.seek(0); msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)

def start_session(app, profile='default', extra_args=None):
    app_adapter(app)
    data = request(app)
    if data:
        if data['profile'] != profile:
            raise RuntimeError('Приложение уже работает с другим профилем. Сначала остановите его сессию.')
        return request(app, 'open', args=extra_args or [], cwd=os.getcwd())
    load_profile(profile)  # Fail before spawning when a stored secret is unavailable.
    log = runtime_dir() / f'{app}.boot.log'
    with log.open('ab') as stream:
        if os.name != 'nt':
            log.chmod(0o600)
        helper = subprocess.Popen(entry_command() + ['session', app, '--profile', profile],
            stdin=subprocess.DEVNULL, stdout=stream, stderr=stream, close_fds=True,
            env=independent_environment(),
            start_new_session=os.name != 'nt', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0) if os.name == 'nt' else 0)
    for _ in range(100):
        data = request(app)
        if data:
            if data['profile'] != profile:
                raise RuntimeError('Одновременно запущена сессия с другим профилем.')
            return request(app, 'open', args=extra_args or [], cwd=os.getcwd())
        if helper.poll() is not None:
            # Another caller may have acquired the lock; let its descriptor appear.
            if helper.returncode and _ > 10:
                raise RuntimeError(f'Запуск не завершён. Откройте журнал: {log}')
        time.sleep(.1)
    raise RuntimeError(f'Сессия не ответила. Журнал: {log}')

def validate_login(url):
    parsed = urlsplit(url)
    if len(url) > 16384 or any(c in url for c in '\r\n\x00"') or parsed.scheme != 'https' or parsed.hostname != 'accounts.google.com' or parsed.port not in (None, 443) or parsed.username or parsed.password:
        raise ValueError('Нужна свежая HTTPS-ссылка входа accounts.google.com.')
    return url

def _browser():
    import shutil
    candidates = ['/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge']
    if sys.platform == 'win32':
        for base in ('LOCALAPPDATA', 'ProgramFiles', 'ProgramFiles(x86)'):
            candidates += [str(Path(os.environ.get(base, '')) / p) for p in ('Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe')]
    for path in candidates:
        if Path(path).is_file():
            return path
    for command in ('google-chrome', 'chromium', 'chromium-browser', 'microsoft-edge'):
        if shutil.which(command):
            return shutil.which(command)
    raise RuntimeError('Установите Chromium, Google Chrome или Microsoft Edge для входа через прокси.')

def serve_session(app, profile_name):
    adapter = app_adapter(app)
    exe = executable(adapter)
    profile = load_profile(profile_name)
    stop = threading.Event(); guard = threading.RLock(); processes = []; retired=set()
    token = secrets.token_hex(32)
    path = descriptor(app)
    with session_lock(app):
        log=logging.getLogger('devproxy.'+app);log.setLevel(logging.INFO)
        handler=RotatingFileHandler(runtime_dir()/(app+'.log'),maxBytes=524288,backupCount=3,encoding='utf-8')
        handler.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(message)s'));log.addHandler(handler)
        if os.name!='nt':(runtime_dir()/(app+'.log')).chmod(0o600)
        tunnel = LocalTunnel(profile)
        local = f'http://127.0.0.1:{tunnel.start()}'
        env = {key: local for key in ('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','all_proxy','AG_LS_PROXY','AGY_PROXY_URL','agy_proxy_url')}
        env.update(NO_PROXY='localhost,127.0.0.1,::1', no_proxy='localhost,127.0.0.1,::1')
        try:
            env.update(adapter.get_launch_environment(local,exe))
        except BaseException:
            tunnel.stop()
            raise
        flags = [f.replace('{PROXY_URL}', local) for f in adapter.launch_flags(profile,exe)]
        job=None
        if os.name=='nt':
            from .core.windows_job import WindowsJobManager
            job=WindowsJobManager()
        def alive(proc):
            if proc.pid in retired:return False
            if proc.poll() is None:return True
            if os.name!='nt':
                try:os.killpg(proc.pid,0);return True
                except ProcessLookupError:pass
            retired.add(proc.pid);return False
        def launch(args,cwd=None):
            if not isinstance(args, list) or len(args) > 256 or not all(isinstance(a,str) and '\x00' not in a for a in args):
                raise ValueError('Некорректные аргументы приложения.')
            if cwd is not None and (not isinstance(cwd,str) or not Path(cwd).is_absolute() or not Path(cwd).is_dir()):raise ValueError('Некорректный рабочий каталог.')
            if not any(alive(p) for p in processes):require_stopped(exe)
            processes.append(Launcher.launch(exe, flags + args, env, cwd=cwd or str(Path(exe).parent),job_manager=job,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL))
            log.info('Application launched app=%s pid=%s',app,processes[-1].pid)
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def do_POST(self):
                self.connection.settimeout(10)
                if not hmac.compare_digest(self.headers.get('Authorization', ''), 'Bearer ' + token):
                    self.send_error(403); return
                try:
                    size = int(self.headers.get('Content-Length', '0'))
                    if not 0 < size <= 32768:
                        raise ValueError('Некорректная длина запроса.')
                    data = json.loads(self.rfile.read(size))
                    command = data.get('command')
                    with guard:
                        if command == 'stop': stop.set()
                        elif command == 'open':
                            if not any(alive(p) for p in processes) or data.get('args'):
                                launch(data.get('args', []),data.get('cwd'))
                        elif command == 'login':
                            url = validate_login(data['url'])
                            browser_profile = runtime_dir() / (app + '-browser')
                            browser_profile.mkdir(mode=0o700, exist_ok=True)
                            processes.append(Launcher.launch(_browser(), ['--proxy-server='+local, '--proxy-bypass-list=localhost;127.0.0.1;[::1]', '--disable-quic', '--user-data-dir='+str(browser_profile), url], env,job_manager=job,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL))
                            log.info('Login browser launched app=%s',app)
                        elif command != 'status': raise ValueError('Неизвестная команда.')
                    result = {'app': app, 'profile': profile_name, 'pid': os.getpid(), 'bridge': local, 'running': any(alive(p) for p in processes), 'account_verified': False}
                    body = json.dumps(result).encode(); status = 200
                except Exception as exc:
                    log.warning('Request failed: %s',redact(exc))
                    body = json.dumps({'error': redact(exc)}).encode(); status = 400
                self.send_response(status); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body)
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        server.daemon_threads = True
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        tmp = path.with_suffix('.tmp')
        tmp.write_text(json.dumps({'port':server.server_port, 'token':token}), encoding='utf-8'); tmp.chmod(0o600); os.replace(tmp, path)
        log.info('Session ready app=%s profile=%s bridge=%s',app,profile_name,local)
        try:
            idle=0
            while not stop.wait(.5):
                with guard:
                    live=any(alive(p) for p in processes)
                    idle=0 if live else idle+1
                    if processes and idle>=10 and os.name!='nt':break
        finally:
            server.shutdown(); server.server_close()
            for proc in processes:
                if os.name != 'nt' and proc.pid not in retired:
                    try: os.killpg(proc.pid, signal.SIGTERM)
                    except ProcessLookupError: pass
                elif proc.poll() is None:
                    proc.terminate()
                try: proc.wait(timeout=5)
                except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=5)
            if job:job.close()
            tunnel.stop()
            log.info('Session stopped app=%s',app);log.removeHandler(handler);handler.close()
            if path.exists() and json.loads(path.read_text(encoding='utf-8')).get('token') == token:
                path.unlink()

def main(argv=None):
    import argparse
    parser = argparse.ArgumentParser(description='Dagami Agent Proxy: запуск, постоянные ярлыки и фоновые сессии')
    sub = parser.add_subparsers(dest='command', required=True)
    for name in ('run','session','patch'):
        p=sub.add_parser(name); p.add_argument('app'); p.add_argument('--profile', default='default')
        if name=='run': p.add_argument('--wait', action='store_true')
    for name in ('stop','unpatch','shortcuts','login'):
        p=sub.add_parser(name);p.add_argument('app')
        if name=='login':p.add_argument('--url',required=True)
    register=sub.add_parser('register');register.add_argument('app');register.add_argument('path')
    sub.add_parser('sessions');sub.add_parser('gui');sub.add_parser('logs').add_argument('app')
    raw=list(sys.argv[1:] if argv is None else argv);extra=[]
    if '--' in raw:
        index=raw.index('--');extra=raw[index+1:];raw=raw[:index]
    args=parser.parse_args(raw)
    try:
        if args.command=='gui':
            from .desktop import main as desktop_main
            return desktop_main()
        if args.command=='sessions':
            for adapter in list_adapters():
                data=request(adapter.app_id)
                if data: print(json.dumps(data,ensure_ascii=False))
        elif args.command=='session':serve_session(args.app,args.profile)
        elif args.command=='run':
            if app_adapter(args.app).is_cli:
                adapter=app_adapter(args.app);exe=executable(adapter)
                process,_=Launcher.launch_with_tunnel(exe,load_profile(args.profile),adapter.launch_flags(load_profile(args.profile),exe),extra,wait=True,environment_factory=adapter.get_launch_environment)
                return process.returncode
            data=start_session(args.app,args.profile,extra); print(json.dumps(data,ensure_ascii=False))
            if args.wait:
                try:
                    while request(args.app):time.sleep(1)
                except KeyboardInterrupt:request(args.app,'stop')
        elif args.command=='stop':
            print('Остановка сессии отправлена.' if request(args.app,'stop') else 'Активной сессии нет.')
        elif args.command=='login':
            validate_login(args.url)
            if not request(args.app,'login',url=args.url):raise RuntimeError('Сначала запустите приложение через Dagami Agent Proxy.')
            print('Открыто окно браузера через тот же локальный прокси.')
        elif args.command in ('patch','unpatch','shortcuts'):
            from .portable_shortcuts import PortableShortcuts
            links=PortableShortcuts()
            result=getattr(links, {'patch':'enable','unpatch':'restore','shortcuts':'status'}[args.command])(args.app, **({'profile':args.profile} if args.command=='patch' else {}))
            print(json.dumps(result,ensure_ascii=False))
        elif args.command=='logs':
            app_adapter(args.app)
            for suffix in ('.boot.log','.log'):
                path=runtime_dir()/(args.app+suffix)
                if path.exists():print(redact(path.read_text(encoding='utf-8',errors='replace')[-32768:]))
        elif args.command=='register':print(register_executable(args.app,args.path))
        return 0
    except Exception as exc:
        print(redact(exc),file=sys.stderr);return 1
