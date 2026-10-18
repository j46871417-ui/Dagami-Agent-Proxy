"""Single executable dispatch: desktop, CLI, session and privileged DNS helper."""
import sys
import os


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if args[:1] == ['--system-dns-helper']:
        if len(args) != 2: return 2
        from .system_dns import helper
        return helper(args[1])
    if args == ['--smoke-gui']:
        from .desktop import Desktop
        root = Desktop(tray=False)
        root.withdraw(); root.update(); root.destroy()
        return 0
    if not args or args in (['--autostart'], ['gui']):
        if sys.platform != 'win32' and os.geteuid() == 0:
            raise RuntimeError('Откройте приложение от обычного пользователя. Права для DNS оно запросит отдельно.')
        from .desktop import main as desktop
        return desktop(autostart=args == ['--autostart'])
    # A macOS windowed bundle has no Python standard streams; CLI mode may
    # still be invoked from Terminal using the very same executable.
    for name in ('stdout', 'stderr'):
        if getattr(sys, name) is None:
            try:setattr(sys, name, open('/dev/'+name, 'w', encoding='utf-8'))
            except OSError:setattr(sys, name, open(os.devnull, 'w', encoding='utf-8'))
    if args[:1] in (['dns'], ['autostart']):
        import argparse
        import json
        parser = argparse.ArgumentParser()
        sub = parser.add_subparsers(dest='command', required=True)
        dns = sub.add_parser('dns')
        dns.add_argument('action', choices=['status', 'services', 'apply', 'restore', 'reset'])
        dns.add_argument('--server', default='185.149.145.6')
        dns.add_argument('--mode', choices=['domains', 'adapters'], default='domains')
        dns.add_argument('--service', action='append', default=[])
        dns.add_argument('--preset', choices=['Development', 'Full'], default='Development')
        dns.add_argument('--replace', action='store_true')
        login = sub.add_parser('autostart'); login.add_argument('action', choices=['status', 'enable', 'disable'])
        options = parser.parse_args(args)
        try:
            if options.command == 'autostart':
                from .portable_startup import Startup
                startup = Startup()
                if options.action != 'status': startup.set_enabled(options.action == 'enable')
                print('Включён' if startup.enabled() else 'Выключен')
            else:
                from .system_dns import backend, Store, execute
                if options.action == 'services': print(json.dumps(backend().services(), ensure_ascii=False, indent=2))
                elif options.action == 'status':
                    path = Store().root/'status.json'
                    print(path.read_text(encoding='utf-8') if path.is_file() else 'DNS ещё не менялся приложением.')
                else:
                    confirmed = False
                    if options.action == 'reset':
                        for step in (1, 2):
                            if input(f'Вы точно уверены в том что вы делаете? ({step}/2) Введите ДА: ') != 'ДА': return 1
                        confirmed = True
                    print(execute(options.action, server=options.server, mode=options.mode, services=options.service,
                                  preset=options.preset, replace=options.replace, confirmed=confirmed))
            return 0
        except Exception as exc:
            print('Ошибка: '+str(exc), file=sys.stderr); return 1
    from .cli import main as cli
    old = sys.argv
    try:
        sys.argv = [sys.argv[0], *args]
        return cli()
    finally: sys.argv = old
