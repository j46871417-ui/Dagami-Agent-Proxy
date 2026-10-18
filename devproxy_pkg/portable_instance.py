"""One desktop per user; a private file signals an existing window to show."""
import os
from .portable_runtime import runtime_dir
from .system_dns import atomic


class Instance:
    def __init__(self, root=None):
        self.root = root if root is not None else runtime_dir()
        self.stream = None
        self.primary = False

    def __enter__(self):
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.signal = self.root/'desktop.show'
        path = self.root/'desktop.lock'
        if path.is_symlink() or self.signal.is_symlink():
            raise RuntimeError('Недопустимая ссылка в файлах окна.')
        self.stream = path.open('a+b')
        try:
            if os.name == 'nt':
                import msvcrt
                self.stream.seek(0)
                if not self.stream.read(1):self.stream.write(b'0');self.stream.flush()
                self.stream.seek(0);msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except (BlockingIOError, PermissionError, OSError) as exc:
            if getattr(exc, 'errno', None) not in (11, 13, 35):
                self.stream.close();raise
            self.stream.close();self.stream=None
            atomic(self.signal, b'show')
            return self
        self.primary = True
        if self.signal.exists():self.signal.unlink()
        return self

    def consume_show(self):
        if self.primary and self.signal.is_file() and not self.signal.is_symlink():
            self.signal.unlink();return True
        return False

    def __exit__(self, *args):
        if self.stream:
            if os.name == 'nt':
                import msvcrt
                self.stream.seek(0);msvcrt.locking(self.stream.fileno(),msvcrt.LK_UNLCK,1)
            self.stream.close()
