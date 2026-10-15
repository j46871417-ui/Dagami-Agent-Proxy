"""Reversible per-user Linux launchers and macOS proxy wrapper applications."""
from __future__ import annotations
import base64
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shlex
import shutil
import sys
import tempfile

from .portable_runtime import app_adapter, entry_command, load_profile, runtime_dir, session_lock, executable

def digest(data): return hashlib.sha256(data).hexdigest()

def atomic(path, data, mode=0o600):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    fd,tmp=tempfile.mkstemp(dir=path.parent,prefix='.devproxy-')
    try:
        with os.fdopen(fd,'wb') as stream:stream.write(data);stream.flush();os.fsync(stream.fileno())
        os.chmod(tmp,mode);os.replace(tmp,path)
    finally:
        if os.path.exists(tmp):os.unlink(tmp)

def desktop_quote(value):
    if any(c in value for c in '\r\n\x00'):
        raise ValueError('Недопустимые символы в пути запуска.')
    # Desktop string escaping precedes Exec quoting; no shell is involved.
    value=value.replace('%','%%')
    for char in ('\\','"','`','$'):value=value.replace(char,'\\'+char)
    return '"'+value.replace('\\','\\\\')+'"'

def rewrite_desktop(raw, command):
    lines=raw.splitlines(keepends=True);active=False;found=False;result=[]
    for line in lines:
        if line.startswith('['):active=line.strip()=='[Desktop Entry]'
        if active and line.startswith('Exec='):
            if found:raise ValueError('Неоднозначный Desktop Entry: несколько Exec.')
            match=re.fullmatch(r'("(?:\\.|[^"\\])*"|[^\s]+)(?:\s+(.*))?',line[5:].strip())
            if not match:raise ValueError('Не удалось разобрать Exec ярлыка.')
            tail=match.group(2) or ''
            if re.search(r'%(?![%fFuUickdDnNvm])',tail):raise ValueError('Неподдерживаемые подстановки Exec.')
            result.append('Exec='+' '.join(desktop_quote(x) for x in command)+' -- '+tail+'\n');found=True
        elif active and line.startswith(('DBusActivatable=','Actions=','TryExec=')):
            # Otherwise D-Bus or a desktop action could bypass the managed Exec.
            continue
        else:result.append(line)
    if not found:raise ValueError('В ярлыке отсутствует Exec.')
    return ''.join(result)

class PortableShortcuts:
    def __init__(self, root=None, platform=None, home=None):
        self.root=Path(root) if root else runtime_dir().parent/'shortcuts-portable'
        self.platform=platform or sys.platform;self.home=Path(home) if home else Path.home()
        self.journal=self.root/'journal.json'
    def _load(self):
        if not self.journal.exists():return {}
        data=json.loads(self.journal.read_text(encoding='utf-8'))
        if not isinstance(data,dict):raise ValueError('Повреждён журнал ярлыков.')
        return data
    def _save(self,data):atomic(self.journal,json.dumps(data,ensure_ascii=False,indent=2).encode())
    def status(self,app):
        app_adapter(app);records=self._load().get(app,[])
        errors=[str(r['path']) for r in records if not Path(r['path']).is_file() or digest(Path(r['path']).read_bytes())!=r['hash']]
        return {'app':app,'enabled':bool(records) and not errors,'files':len(records),'changed':errors,'profile':records[0]['profile'] if records else None}
    def enable(self,app,profile='default'):
        adapter=app_adapter(app);load_profile(profile)
        exe=executable(adapter)
        if self.platform=='win32':raise RuntimeError('Для привычных ярлыков Windows используйте dagami-proxy.exe patch APP из нативной сборки.')
        with session_lock('shortcuts'):
            data=self._load();existing=data.get(app,[])
            state=self.status(app)
            if state['changed']:raise RuntimeError('Управляемые ярлыки изменены или удалены. Автоматическая перезапись отменена.')
            if existing:
                if state['profile']!=profile:raise RuntimeError('Сначала восстановите ярлыки, чтобы сменить профиль.')
                return state
            command=entry_command()+['run',app,'--profile',profile]
            if self.platform=='darwin':
                bundle=self.home/'Applications'/'Dagami Agent Proxy'/f'{adapter.display_name} — Proxy.app'
                if bundle.exists():raise RuntimeError('Обёртка уже существует без записи в журнале; перезапись отменена.')
                info={'CFBundleName':adapter.display_name+' — Proxy','CFBundleIdentifier':'local.devproxy.launch.'+app,'CFBundleExecutable':'launch','CFBundlePackageType':'APPL','CFBundleVersion':'1','LSUIElement':True}
                script='#!/bin/sh\nexec '+shlex.join(command)+' -- "$@"\n'
                planned=[(bundle/'Contents'/'Info.plist',plistlib.dumps(info),0o644)]
                if app in ('codex','opencode'):
                    terminal=bundle/'Contents'/'Resources'/'session.command'
                    planned.append((terminal,script.encode(),0o700))
                    script='#!/bin/sh\nexec /usr/bin/open -a Terminal '+shlex.quote(str(terminal))+'\n'
                planned.append((bundle/'Contents'/'MacOS'/'launch',script.encode(),0o700))
            elif self.platform.startswith('linux'):
                user=Path(os.environ.get('XDG_DATA_HOME',str(self.home/'.local'/'share')))/'applications'
                roots=[user]+[Path(p)/'applications' for p in os.environ.get('XDG_DATA_DIRS','/usr/local/share:/usr/share').split(':') if p]
                planned=[];names=set();exe_names={Path(exe).name.casefold(),app.casefold()}
                for root in roots:
                    if not root.is_dir():continue
                    for source in sorted(root.glob('*.desktop')):
                        if source.name in names:continue
                        names.add(source.name)
                        try:raw=source.read_text(encoding='utf-8')
                        except (OSError,UnicodeError):continue
                        match=re.search(r'^Exec=(.+)$',raw,re.M)
                        if not match:continue
                        try:tokens=shlex.split(match.group(1))
                        except ValueError:continue
                        if not tokens or Path(tokens[0]).name.casefold() not in exe_names:continue
                        rewritten=rewrite_desktop(raw,command)
                        if app in ('codex','opencode'):rewritten=re.sub(r'^Terminal=.*$', 'Terminal=true',rewritten,flags=re.M) if re.search(r'^Terminal=',rewritten,re.M) else rewritten+'Terminal=true\n'
                        planned.append((user/source.name,rewritten.encode(),0o644))
                if not planned:
                    raw='[Desktop Entry]\nType=Application\nName='+adapter.display_name+'\nExec='+desktop_quote(str(exe))+' %U\nTerminal='+('true' if app in ('codex','opencode') else 'false')+'\n'
                    planned=[(user/('devproxy-'+app+'.desktop'),rewrite_desktop(raw,command).encode(),0o644)]
                    if planned[0][0].exists():raise RuntimeError('Ярлык уже существует без записи в журнале.')
            else:raise RuntimeError('Эта ОС не поддерживается.')
            records=[]
            # Persist recovery information for the entire plan before the first mutation.
            for path,payload,mode in planned:
                old=path.read_bytes() if path.exists() else None
                records.append({'path':str(path),'hash':digest(payload),'original':base64.b64encode(old).decode() if old is not None else None,'mode':path.stat().st_mode&0o777 if old is not None else mode,'profile':profile})
            data[app]=records;self._save(data)
            try:
                for (path,payload,mode),record in zip(planned,records):
                    old=base64.b64decode(record['original']) if record['original'] is not None else None
                    if (path.read_bytes() if path.exists() else None)!=old:raise RuntimeError('Ярлык изменился во время подготовки. Повторите после обновления приложения.')
                    atomic(path,payload,mode)
            except Exception:
                self._restore_locked(app,data,allow_original=True);raise
            return self.status(app)
    def _restore_locked(self,app,data,allow_original=False):
        records=data.get(app,[])
        for record in records:
            path=Path(record['path']);old=base64.b64decode(record['original']) if record['original'] is not None else None
            current=path.read_bytes() if path.exists() else None
            if current is None and old is None:continue
            if current is None or (digest(current)!=record['hash'] and not (allow_original and current==old)):
                raise RuntimeError('Файл изменён другим приложением: '+str(path)+'. Откат остановлен.')
        for record in records:
            path=Path(record['path'])
            if record['original'] is None:
                if path.exists():path.unlink()
            else:atomic(path,base64.b64decode(record['original']),record['mode'])
        data.pop(app,None);self._save(data)
        if self.platform=='darwin' and records:
            bundle=Path(records[0]['path']).parents[1]
            # Remove only empty directories created for the wrapper; never recurse.
            for path in (bundle/'Contents'/'MacOS',bundle/'Contents'/'Resources',bundle/'Contents',bundle):
                try:path.rmdir()
                except OSError:pass
        return {'app':app,'enabled':False,'restored':len(records)}
    def restore(self,app):
        app_adapter(app)
        with session_lock('shortcuts'):return self._restore_locked(app,self._load(),allow_original=True)
