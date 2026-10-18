"""Transactional Unix DNS. No system writes occur on import or discovery.

Linux: NetworkManager; split DNS requires its existing dnsmasq plugin.
macOS: networksetup services and /private/etc/resolver domain files.
"""
from __future__ import annotations
import base64
import contextlib
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import ssl
import stat
import struct
import subprocess
import sys
import tempfile
import uuid
from .core.process_env import external_environment, independent_environment

TARGETS = ('example.com', 'www.microsoft.com', 'www.cloudflare.com')


def run(argv, timeout=25):
    env = external_environment({'LC_ALL':'C', 'LANG':'C'})
    result = subprocess.run(list(argv), capture_output=True, text=True,
                            timeout=timeout, env=env)
    if result.returncode:
        raise RuntimeError(f'{Path(argv[0]).name}: {result.stderr.strip() or result.stdout.strip()}')
    return result.stdout.strip()


def tool(name):
    # A privileged helper must not execute programs from the caller's PATH.
    for directory in ('/usr/sbin', '/usr/bin', '/sbin', '/bin'):
        path = Path(directory) / name
        if path.is_file():
            return str(path)
    raise RuntimeError(f'Не найдена системная команда {name}. Настройки не менялись.')


def domains(preset='Development'):
    data = json.loads((Path(__file__).parent / 'assets' / 'dns-domains.json').read_text(encoding='utf-8'))
    if preset not in data:
        raise ValueError('Неизвестный набор доменов.')
    selected = data[preset]
    if not selected or any(not re.fullmatch(r'[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?', domain) for domain in selected):
        raise ValueError('Некорректный набор доменов.')
    return selected


def validate_request(request):
    if not isinstance(request, dict) or request.get('action') not in ('apply', 'restore', 'reset'):
        raise ValueError('Неизвестная операция DNS.')
    if not re.fullmatch(r'[0-9a-f]{32}', str(request.get('job', ''))):
        raise ValueError('Некорректный ID операции DNS.')
    if request['action'] == 'reset' and request.get('confirmed') is not True:
        raise ValueError('Для полного сброса нужны два подтверждения.')
    if request['action'] == 'apply':
        request['server'] = str(ipaddress.ip_address(request.get('server', '')))
        if '%' in request['server']: raise ValueError('DNS должен быть IP без зоны интерфейса.')
        if request.get('mode') not in ('domains', 'adapters'):
            raise ValueError('Выберите режим DNS.')
        domains(request.get('preset', 'Development'))
        if not isinstance(request.get('services', []), list) or len(request.get('services', [])) > 128 or any(not isinstance(item, str) or len(item)>256 or any(c in item for c in '\r\n\0') for item in request.get('services', [])):
            raise ValueError('Некорректный список подключений.')
    return request


def atomic(path, data, mode=0o600):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink():
        raise RuntimeError('Символическая ссылка вместо файла DNS; запись отменена.')
    fd, temporary = tempfile.mkstemp(prefix='.dagami-', dir=path.parent)
    try:
        if os.name != 'nt':
            os.fchmod(fd, mode)
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class Store:
    def __init__(self, root=None, platform=None, enforce_owner=True):
        self.platform = platform or sys.platform
        self.root = Path(root) if root else Path('/Library/Application Support/DagamiAgentProxy/SystemDns' if self.platform == 'darwin' else '/var/lib/dagami-agent-proxy/dns')
        self.enforce_owner = enforce_owner

    def check(self):
        if self.enforce_owner and (not hasattr(os, 'geteuid') or os.geteuid() != 0):
            raise PermissionError('Для записи DNS нужны права администратора.')
        for parent in (self.root, *self.root.parents):
            if parent.is_symlink():
                raise RuntimeError('Недопустимый путь копии DNS.')
        for parent in reversed((self.root, *self.root.parents)):
            if not parent.exists():
                parent.mkdir(mode=0o755)
                if os.name != 'nt':parent.chmod(0o755)
        if self.enforce_owner:
            if self.root.stat().st_uid != 0 or self.root.stat().st_mode & 0o022:
                raise RuntimeError('Папка копии DNS должна принадлежать root и быть защищена от записи.')

    @contextlib.contextmanager
    def locked(self):
        self.check()
        path = self.root / 'operation.lock'
        if path.is_symlink():
            raise RuntimeError('Недопустимый файл блокировки DNS.')
        with path.open('a+b') as stream:
            if os.name == 'nt':  # fixture runs on Windows; Unix production uses flock.
                import msvcrt
                stream.write(b'0'); stream.flush(); stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                try: yield
                finally:
                    stream.seek(0); msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                try: fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError: raise RuntimeError('Другая операция DNS ещё выполняется.') from None
                try: yield
                finally: fcntl.flock(stream.fileno(), fcntl.LOCK_UN)

    def load(self):
        path = self.root / 'state.json'
        if not path.exists(): return None
        if path.is_symlink() or (self.enforce_owner and (path.stat().st_uid != 0 or path.stat().st_mode & 0o022)):
            raise RuntimeError('Копия DNS не прошла проверку владельца.')
        return json.loads(path.read_text(encoding='utf-8'))

    def save(self, state):
        atomic(self.root / 'state.json', json.dumps(state, ensure_ascii=False, indent=2).encode())
        atomic(self.root / 'status.json', json.dumps({'status': state['status'], 'warnings': state.get('warnings', []), 'errors': state.get('errors', [])}, ensure_ascii=False).encode(), 0o644)

    def archive(self, state):
        if not re.fullmatch(r'[0-9a-f]{32}', state['id']):
            raise RuntimeError('Некорректная прежняя копия DNS.')
        atomic(self.root / ('history-' + state['id'] + '.json'), json.dumps(state, ensure_ascii=False).encode())


def file_snapshot(path):
    path = Path(path)
    # macOS backend uses /private/etc explicitly, avoiding the standard /etc link.
    if any(parent.is_symlink() for parent in (path, *path.parents)):
        raise RuntimeError(f'Ссылка вместо файла настроек: {path.name}')
    if not path.exists(): return {'exists': False, 'data': '', 'mode': 0o644}
    if not path.is_file() or path.stat().st_size > 65536:
        raise RuntimeError(f'Неподдерживаемый файл DNS: {path.name}')
    return {'exists': True, 'data': base64.b64encode(path.read_bytes()).decode(), 'mode': stat.S_IMODE(path.stat().st_mode)}


def file_value(text, before):
    return {'exists': True, 'data': base64.b64encode(text.encode()).decode(), 'mode': before.get('mode', 0o644)}


class Backend:
    def validate(self, request):
        """Check support and selection before touching an active older setup."""

    def missing(self, change):
        return False

    def read(self, change):
        if change['kind'] == 'file': return file_snapshot(change['id'])
        return self.read_dns(change['id'])

    def write(self, change, value):
        if change['kind'] == 'file':
            # Read again to reject a symlink introduced after the snapshot.
            file_snapshot(change['id'])
            path = Path(change['id'])
            if value['exists']: atomic(path, base64.b64decode(value['data'], validate=True), value['mode'])
            elif path.exists(): path.unlink()
        else: self.write_dns(change['id'], value)

    def file_change(self, path, text, replace=False):
        before = file_snapshot(path)
        if before['exists'] and before['data'] != base64.b64encode(text.encode()).decode() and not replace:
            raise RuntimeError(f'Есть внешнее правило DNS: {Path(path).name}. Разрешите замену с копией.')
        return {'kind': 'file', 'id': str(path), 'before': before, 'after': file_value(text, before)}


class MacBackend(Backend):
    def __init__(self, execute=run, resolver='/private/etc/resolver'):
        self.execute, self.resolver = execute, Path(resolver)
        self.command = '/usr/sbin/networksetup'

    def services(self, all_profiles=False):
        lines = self.execute([self.command, '-listallnetworkservices']).splitlines()[1:]
        return [{'id': name.lstrip('*'), 'name': name.lstrip('*')} for name in lines if name.strip()]

    def read_dns(self, service):
        text = self.execute([self.command, '-getdnsservers', service])
        if text.startswith("There aren't any DNS Servers set"): return []
        values = [line.strip() for line in text.splitlines() if line.strip()]
        for value in values: ipaddress.ip_address(value)
        return values

    def write_dns(self, service, servers):
        self.execute([self.command, '-setdnsservers', service, *(servers or ['Empty'])])

    def validate(self, request):
        if request['mode'] == 'adapters':
            chosen=set(request.get('services', []))
            if not chosen or not chosen <= {item['id'] for item in self.services()}:
                raise ValueError('Выберите существующие сетевые службы.')

    def missing(self, change):
        return change['kind']=='dns' and change['id'] not in {item['id'] for item in self.services(True)}

    def plan(self, request):
        if request['mode'] == 'domains':
            return [self.file_change(self.resolver / domain, f'# Dagami Agent Proxy\nnameserver {request["server"]}\n', request.get('replace', False)) for domain in domains(request.get('preset', 'Development'))]
        chosen = set(request.get('services', []))
        available = {item['id'] for item in self.services()}
        if not chosen or not chosen <= available: raise ValueError('Выберите существующие сетевые службы.')
        result = []
        family = ipaddress.ip_address(request['server']).version
        for service in sorted(chosen):
            before = self.read_dns(service)
            after = [value for value in before if ipaddress.ip_address(value).version != family] + [request['server']]
            result.append({'kind': 'dns', 'id': service, 'before': before, 'after': after})
        return result

    def reset_plan(self):
        changes = [{'kind': 'dns', 'id': item['id'], 'before': self.read_dns(item['id']), 'after': []} for item in self.services(True)]
        if self.resolver.is_dir():
            for path in sorted(self.resolver.iterdir()):
                before = file_snapshot(path)
                changes.append({'kind': 'file', 'id': str(path), 'before': before, 'after': {'exists': False, 'data': '', 'mode': before['mode']}})
        return changes

    def flush(self):
        self.execute(['/usr/bin/dscacheutil', '-flushcache'])
        self.execute(['/usr/bin/killall', '-HUP', 'mDNSResponder'])


class LinuxBackend(Backend):
    properties = ('dns', 'ignore-auto-dns', 'dns-search')
    def __init__(self, execute=run, config='/etc/NetworkManager/dnsmasq.d', nmcli=None, manager=None):
        self.execute, self.config = execute, Path(config)
        self.nmcli = nmcli or tool('nmcli')
        self.manager = manager

    def services(self, all_profiles=False):
        args = [self.nmcli, '-t', '--escape', 'no', '-f', 'UUID,TYPE,DEVICE', 'connection', 'show']
        if not all_profiles: args += ['--active']
        result = []
        for line in self.execute(args).splitlines():
            parts = line.split(':', 2)
            if len(parts) != 3 or not re.fullmatch(r'[0-9a-fA-F-]{36}', parts[0]): continue
            if parts[1] == 'loopback': continue
            result.append({'id': parts[0], 'name': parts[2] if parts[2] not in ('', '--') else parts[0]})
        return result

    def read_dns(self, identity):
        connection, family = identity.split('/')
        if family not in ('4', '6') or not re.fullmatch(r'[0-9a-fA-F-]{36}', connection): raise ValueError('Некорректный ID подключения.')
        # Separate property queries preserve empty values without positional ambiguity.
        return {key: self.execute([self.nmcli, '--escape', 'no', '-g', 'ipv'+family+'.'+key, 'connection', 'show', 'uuid', connection]) for key in self.properties}

    def write_dns(self, identity, value):
        connection, family = identity.split('/')
        command = [self.nmcli, 'connection', 'modify', 'uuid', connection]
        for key in self.properties: command += ['ipv'+family+'.'+key, value[key]]
        self.execute(command)
        current = next((item for item in self.services() if item['id'] == connection), None)
        if current: self.execute([self.nmcli, 'device', 'reapply', current['name']])

    def split_supported(self):
        text = self.execute([self.manager or tool('NetworkManager'), '--print-config'])
        import configparser
        config = configparser.ConfigParser(interpolation=None, strict=False)
        config.read_string(text)
        if config.get('main', 'dns', fallback='') != 'dnsmasq':
            raise RuntimeError('Режим доменов Linux требует NetworkManager с уже включённым dnsmasq. Общий DNS доступен отдельным режимом. Настройки не менялись.')

    def validate(self, request):
        if request['mode']=='domains':self.split_supported()
        else:
            chosen=set(request.get('services', []))
            if not chosen or not chosen <= {item['id'] for item in self.services()}:
                raise ValueError('Выберите активные подключения NetworkManager.')

    def missing(self, change):
        return change['kind']=='dns' and change['id'].split('/')[0] not in {item['id'] for item in self.services(True)}

    def plan(self, request):
        if request['mode'] == 'domains':
            self.split_supported()
            selected = domains(request.get('preset', 'Development'))
            # Refuse overlapping rules instead of relying on dnsmasq file ordering.
            for path in sorted(self.config.glob('*')):
                if path.name == '90-dagami-agent-proxy.conf': continue
                data = file_snapshot(path)
                text = base64.b64decode(data['data']).decode('utf-8', errors='replace')
                selectors=[]
                for directive in re.finditer(r'^\s*(?:server|address|local)\s*=\s*/([^\n#]*)',text,re.M):
                    selectors.extend(part.lstrip('*.') for part in directive.group(1).split('/')[:-1])
                if set(selectors).intersection(selected):
                    raise RuntimeError(f'Есть внешнее правило dnsmasq в {path.name}. Оно не перезаписано; верните прежнее правило перед записью.')
            text = '# Dagami Agent Proxy\n' + ''.join(f'server=/{domain}/{request["server"]}\n' for domain in selected)
            return [self.file_change(self.config / '90-dagami-agent-proxy.conf', text, request.get('replace', False))]
        chosen = set(request.get('services', []))
        available = {item['id'] for item in self.services()}
        if not chosen or not chosen <= available: raise ValueError('Выберите активные подключения NetworkManager.')
        family = str(ipaddress.ip_address(request['server']).version)
        result = []
        for connection in sorted(chosen):
            identity = connection+'/'+family
            before = self.read_dns(identity)
            after = dict(before, dns=request['server'], **{'ignore-auto-dns': 'yes'})
            result.append({'kind': 'dns', 'id': identity, 'before': before, 'after': after})
        return result

    def reset_plan(self):
        result = []
        for item in self.services(True):
            for family in ('4', '6'):
                identity = item['id']+'/'+family
                result.append({'kind': 'dns', 'id': identity, 'before': self.read_dns(identity), 'after': {'dns': '', 'ignore-auto-dns': 'no', 'dns-search': ''}})
        # Reset DNS routing directives, retaining unrelated DHCP/plugin settings.
        for path in sorted(self.config.glob('*')):
            before = file_snapshot(path)
            text = base64.b64decode(before['data']).decode('utf-8')
            after = ''.join(line for line in text.splitlines(keepends=True) if not re.match(r'^\s*(server|address|local|rev-server)\s*=', line))
            if after != text: result.append({'kind': 'file', 'id': str(path), 'before': before, 'after': file_value(after, before)})
        return result

    def flush(self):
        self.execute([self.nmcli, 'general', 'reload', 'dns-full'])


def backend(platform=None):
    platform = platform or sys.platform
    if platform == 'darwin': return MacBackend()
    if platform.startswith('linux'): return LinuxBackend()
    raise RuntimeError('Этот модуль DNS предназначен для Linux и macOS. В Windows используйте нативный EXE.')


def dns_addresses(host, server):
    """Minimal bounded DNS A/AAAA query, with transaction and response checks."""
    result = []
    for qtype in (1, 28):
        token = os.urandom(2)
        question = b''.join(bytes([len(label)])+label.encode('ascii') for label in host.split('.'))+b'\0'+struct.pack('!HH', qtype, 1)
        packet = token+b'\x01\x00\x00\x01'+b'\0'*6+question
        try:
            with socket.socket(socket.AF_INET6 if ':' in server else socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.settimeout(2); udp.connect((server, 53)); udp.send(packet); data = udp.recv(4096)
            if len(data) < 12 or data[:2] != token or data[2] & 0x02 or data[3] & 0x0f: continue
            qd, answers = struct.unpack('!HH', data[4:8]); offset = 12
            def skip_name(start):
                for _ in range(128):
                    if start >= len(data): raise ValueError('Truncated DNS name')
                    length = data[start]
                    if length & 0xc0 == 0xc0: return start+2
                    start += 1
                    if length == 0: return start
                    if length > 63: raise ValueError('Invalid DNS name')
                    start += length
                raise ValueError('Invalid DNS name')
            for _ in range(qd): offset = skip_name(offset)+4
            for _ in range(min(answers, 128)):
                offset = skip_name(offset)
                kind, cls, ttl, size = struct.unpack('!HHIH', data[offset:offset+10]); offset += 10
                value = data[offset:offset+size]; offset += size
                if cls == 1 and (kind, size) in ((1, 4), (28, 16)): result.append(str(ipaddress.ip_address(value)))
        except (OSError, ValueError, struct.error): continue
    return list(dict.fromkeys(result))


class Probes:
    def https(self, host, addresses=None):
        try:
            addresses = addresses if addresses is not None else list(dict.fromkeys(item[4][0] for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)))
        except OSError: return False
        for address in addresses[:4]:
            try:
                with socket.create_connection((address, 443), timeout=4) as tcp:
                    with ssl.create_default_context().wrap_socket(tcp, server_hostname=host) as tls:
                        tls.sendall(f'HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n'.encode('ascii'))
                        header=b''
                        while b'\n' not in header and len(header)<128:
                            chunk=tls.recv(128-len(header))
                            if not chunk:break
                            header+=chunk
                        status = header.split(b' ', 2)
                        if len(status) >= 2 and 200 <= int(status[1]) < 500: return True
            except (OSError, ValueError): continue
        return False

    def baseline(self):
        targets = [host for host in TARGETS if self.https(host)]
        if len(targets) < 2: raise RuntimeError('Не удалось проверить исходные DNS и HTTPS. Настройки не менялись.')
        return targets

    def target(self, server, targets):
        if not all(self.https(host, dns_addresses(host, server)) for host in targets):
            raise RuntimeError('Новый DNS не прошёл проверку DNS и HTTPS. Настройки не менялись.')

    def verify(self, targets):
        if not all(self.https(host) for host in targets): raise RuntimeError('После записи не прошла проверка DNS и HTTPS.')


class Engine:
    def __init__(self, system, store, probes=None):
        self.system, self.store, self.probes = system, store, probes or Probes()

    def undo(self, state):
        errors, warnings = [], []
        for change in reversed(state['changes'][:state['pending']+1]):
            try:
                if self.system.missing(change):
                    warnings.append('Нет подключения, пропущено: '+change['id']);continue
                current = self.system.read(change)
                if current == change['before']: continue
                if current != change['after']:
                    warnings.append('Внешние настройки сохранены: '+change['id']); continue
                self.system.write(change, change['before'])
                if self.system.read(change) != change['before']: raise RuntimeError('Проверка отката не прошла: '+change['id'])
            except Exception as exc: errors.append(str(exc))
        try: self.system.flush()
        except Exception as exc: errors.append(str(exc))
        state.update(status='RollbackFailed' if errors else 'Released' if warnings else 'Restored', errors=errors, warnings=warnings)
        self.store.save(state)
        if errors: raise RuntimeError('; '.join(errors))
        return 'Прежние DNS возвращены.'+ (' Пропуски: '+'; '.join(warnings) if warnings else '')

    def perform(self, request):
        validate_request(request)
        with self.store.locked():
            existing = self.store.load()
            if request['action'] == 'restore':
                if not existing: raise RuntimeError('Копии DNS ещё нет.')
                if existing['status'] in ('Restored', 'Released'): return 'Откат уже завершён; исходная копия сохранена.'
                return self.undo(existing)
            targets = []
            if request['action'] == 'apply':
                self.system.validate(request)
                targets = self.probes.baseline(); self.probes.target(request['server'], targets)
                if existing and existing['action'] != 'reset' and existing['status'] not in ('Restored', 'Released'):
                    self.undo(existing)
                changes = self.system.plan(request)
            else: changes = self.system.reset_plan()
            if existing: self.store.archive(existing)
            state = {'id': request['job'], 'action': request['action'], 'status': 'Prepared', 'changes': changes, 'pending': -1, 'warnings': [], 'errors': [], 'targets': targets}
            self.store.save(state)  # complete original backup before the first write
            try:
                state['status'] = 'Applying'
                for index, change in enumerate(changes):
                    try:
                        if self.system.read(change) != change['before']: raise RuntimeError('Настройки изменились после копии: '+change['id'])
                        state['pending'] = index; self.store.save(state)
                        self.system.write(change, change['after'])
                        if self.system.read(change) != change['after']: raise RuntimeError('Проверка записи не прошла: '+change['id'])
                    except Exception as change_error:
                        if request['action'] != 'reset': raise
                        state['errors'].append(str(change_error)); self.store.save(state)
                self.system.flush()
                if state['errors']: raise RuntimeError('; '.join(state['errors']))
                if targets: self.probes.verify(targets)
                state['status'] = 'Applied' if targets else 'Cleared'; self.store.save(state)
                return ('DNS применён, DNS и HTTPS проверены.' if targets else 'Сброс DNS выполнен. Доступ к интернету после сброса не проверялся.')+' Копия: '+str(self.store.root)
            except Exception as exc:
                if request['action'] == 'reset':
                    state.update(status='ClearFailed', errors=[str(exc)]); self.store.save(state)
                    raise RuntimeError('Сброс выполнен частично. Копия сохранена. '+str(exc)) from exc
                try: recovery = self.undo(state)
                except Exception as rollback: raise RuntimeError(f'Запись не прошла: {exc}. Откат не завершён: {rollback}. Копия сохранена.') from exc
                try: self.probes.verify(targets)
                except Exception as network: raise RuntimeError(f'Запись отменена: {exc}. {recovery} Доступ к сети после отката не подтверждён: {network}') from exc
                raise RuntimeError(f'Запись отменена: {exc}. {recovery} DNS и HTTPS после отката проверены.') from exc


def helper(payload):
    request = None
    store = Store()
    try:
        if len(payload) > 22000: raise ValueError('Слишком большой запрос DNS.')
        request = validate_request(json.loads(base64.b64decode(payload, validate=True)))
        store.check()
        message = Engine(backend(), store).perform(request)
        report = {'success': True, 'message': message}
    except Exception as exc: report = {'success': False, 'message': str(exc)}
    if request and hasattr(os, 'geteuid') and os.geteuid() == 0:
        store.check()
        jobs=store.root/'jobs'
        if jobs.is_symlink():raise RuntimeError('Недопустимый путь отчётов DNS.')
        jobs.mkdir(exist_ok=True, mode=0o755);jobs.chmod(0o755)
        atomic(store.root / 'jobs' / (request['job']+'.json'), json.dumps(report, ensure_ascii=False).encode(), 0o644)
    return 0 if report['success'] else 1


def execute(action, **values):
    """User-facing elevation bridge; request values never become shell syntax."""
    from .portable_runtime import entry_command
    request = validate_request(dict(values, action=action, job=uuid.uuid4().hex))
    payload = base64.b64encode(json.dumps(request).encode()).decode()
    command = entry_command()+['--system-dns-helper', payload]
    if sys.platform.startswith('linux'):
        command = [tool('pkexec'), tool('env'), 'PYINSTALLER_RESET_ENVIRONMENT=1', *command]
    elif sys.platform == 'darwin':
        import shlex
        script = 'do shell script '+json.dumps(shlex.join([tool('env'), 'PYINSTALLER_RESET_ENVIRONMENT=1', *command]), ensure_ascii=False)+' with administrator privileges'
        command = ['/usr/bin/osascript', '-e', script]
    else: raise RuntimeError('В Windows используйте нативное окно DNS.')
    completed = subprocess.run(command, capture_output=True, text=True, timeout=600, env=independent_environment())
    report = Store().root / 'jobs' / (request['job']+'.json')
    if not report.is_file():
        raise RuntimeError('Операция не подтверждена: запрос прав отменён или помощник не создал отчёт. '+completed.stderr.strip())
    if report.is_symlink() or report.stat().st_uid != 0: raise RuntimeError('Недопустимый отчёт DNS.')
    result = json.loads(report.read_text(encoding='utf-8'))
    if not result['success']: raise RuntimeError(result['message'])
    return result['message']
