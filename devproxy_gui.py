#!/usr/bin/env python3
"""Portable GUI entry; helper invocations share the same packaged executable."""
import sys
if __name__ == '__main__':
    if sys.argv[1:] == ['--smoke-gui']:
        from devproxy_pkg.desktop import Desktop
        root=Desktop();root.withdraw();root.update();root.destroy()
        raise SystemExit(0)
    if len(sys.argv) > 1:
        from devproxy_pkg.cli import main
    else:
        from devproxy_pkg.desktop import main
    raise SystemExit(main())
