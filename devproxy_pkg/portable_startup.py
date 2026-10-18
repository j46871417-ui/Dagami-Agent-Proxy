"""Opt-in, per-user login startup; never runs a desktop as root."""
from __future__ import annotations
import os
from pathlib import Path
import plistlib
import hashlib
import json
import sys
from .portable_runtime import entry_command
from .portable_shortcuts import desktop_quote
from .system_dns import atomic

LABEL = 'ru.dagami.agentproxy'
MARKER = 'Dagami managed login startup v1'


class Startup:
    def __init__(self, platform=None, home=None, command=None):
        self.platform = platform or sys.platform
        self.home = Path(home) if home else Path.home()
        self.command = list(command) if command else entry_command()
        if self.platform == 'darwin':
            self.path = self.home/'Library'/'LaunchAgents'/(LABEL+'.plist')
        elif self.platform.startswith('linux'):
            config = Path(os.environ.get('XDG_CONFIG_HOME', str(self.home/'.config'))) if home is None else self.home/'.config'
            self.path = config/'autostart'/(LABEL+'.desktop')
        else: raise RuntimeError('Используйте нативный автозапуск Windows.')
        if any(any(c in part for c in '\r\n\0') for part in self.command): raise ValueError('Недопустимый путь запуска.')
        self.journal = self.path.with_suffix('.dagami.json')

    def content(self):
        command = self.command+['--autostart']
        if self.platform == 'darwin':
            return plistlib.dumps({'Label': LABEL, 'ProgramArguments': command, 'RunAtLoad': True,
                                  'KeepAlive': False, 'LimitLoadToSessionType': 'Aqua',
                                  'ProcessType': 'Interactive', 'DagamiManaged': MARKER})
        return ('[Desktop Entry]\nType=Application\nName=Dagami Agent Proxy\nComment='+MARKER+
                '\nExec='+' '.join(desktop_quote(part) for part in command)+
                '\nTerminal=false\nX-GNOME-Autostart-enabled=true\n').encode()

    def existing(self):
        if self.path.is_symlink() or any(parent.is_symlink() for parent in self.path.parents):
            raise RuntimeError('Ссылка вместо файла автозапуска; файл сохранён без изменений.')
        if not self.path.exists(): return None
        data = self.path.read_bytes()
        if self.journal.is_symlink() or not self.journal.is_file():
            raise RuntimeError('Файл автозапуска создан вне приложения. Он сохранён без изменений.')
        recorded = json.loads(self.journal.read_text(encoding='utf-8'))
        if recorded.get('sha256') != hashlib.sha256(data).hexdigest():
            raise RuntimeError('Файл автозапуска изменён вне приложения. Он сохранён без изменений.')
        if self.platform == 'darwin':
            item = plistlib.loads(data)
            managed = item.get('Label') == LABEL and item.get('DagamiManaged') == MARKER
        else: managed = ('Comment='+MARKER+'\n').encode() in data
        if not managed: raise RuntimeError('Есть чужой файл автозапуска. Он не перезаписан и не удалён.')
        return data

    def enabled(self):
        return self.existing() == self.content()

    def set_enabled(self, enabled):
        old = self.existing()
        if self.journal.is_symlink(): raise RuntimeError('Ссылка вместо журнала автозапуска.')
        previous = self.journal.read_bytes() if self.journal.is_file() else None
        try:
            if enabled:
                data = self.content()
                atomic(self.path, data)
                atomic(self.journal, json.dumps({'sha256': hashlib.sha256(data).hexdigest()}).encode())
            elif old is not None:
                self.path.unlink()
                self.journal.unlink()
        except Exception:
            if old is None:
                if self.path.exists(): self.path.unlink()
            else: atomic(self.path, old)
            if previous is None:
                if self.journal.exists(): self.journal.unlink()
            else: atomic(self.journal, previous)
            raise
        return self.enabled()
