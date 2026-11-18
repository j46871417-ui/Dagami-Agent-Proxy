#!/usr/bin/env python3
"""Forced SSH command: paid provider credentials travel between servers only."""
import json
from pathlib import Path

if __name__ == '__main__':
    raw = Path('/var/lib/dagami-pool/routes.json').read_bytes()
    if len(raw) > 65536:
        raise SystemExit('pool too large')
    value = json.loads(raw)
    routes = [route for route in value['routes'] if route['name'].startswith('paid_')]
    settings = Path('/var/lib/dagami-pool/settings.json')
    paid_first = settings.exists() and json.loads(settings.read_text()).get('paid_first') is True
    print(json.dumps({'version': 1, 'routes': routes, 'paid_first': paid_first}, separators=(',', ':')))
