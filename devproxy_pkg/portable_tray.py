"""Tray callbacks communicate with Tk through a queue, never across threads."""
from pathlib import Path
import sys
import threading


class Tray:
    def __init__(self, events):
        self.icon = None
        self.available = False
        self.error = ''
        try:
            import pystray
            from PIL import Image
            self.icon = pystray.Icon('DagamiAgentProxy', Image.open(Path(__file__).parent/'assets'/'icon-64.png'), 'Dagami Agent Proxy',
                menu=pystray.Menu(pystray.MenuItem('Открыть Dagami Agent Proxy', lambda *_: events.put('open'), default=True),
                                  pystray.MenuItem('Выход', lambda *_: events.put('quit'))))
            def ready(icon):
                try: icon.visible = True; self.available = True
                except Exception as exc: self.error = str(exc)
            if sys.platform=='darwin':self.icon.run_detached(ready)
            else:
                def run():
                    try:self.icon.run(ready)
                    except Exception as exc:self.error=str(exc)
                    finally:self.available=False
                threading.Thread(target=run,daemon=True).start()
        except Exception as exc: self.error = str(exc)

    def stop(self):
        if self.icon:
            try: self.icon.stop()
            except Exception: pass
        self.available = False
