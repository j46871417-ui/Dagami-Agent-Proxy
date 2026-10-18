#!/usr/bin/env python3
"""Portable GUI entry; helper invocations share the same packaged executable."""
import sys
if __name__ == '__main__':
    from devproxy_pkg.entry import main
    raise SystemExit(main())
