#!/usr/bin/env python3
"""Pull the central private provider inventory; retain cached valid routes offline."""
import json
from pathlib import Path
import subprocess
import time

from pool_admin import atomic


def sync():
    process = subprocess.run(['/usr/bin/ssh', '-F', '/etc/dagami-reserve/pool-ssh.conf', 'pool-source'], check=True, timeout=7, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    if len(process.stdout) > 65536:
        raise ValueError('pool too large')
    value = json.loads(process.stdout)
    if value.get('version') != 1 or not isinstance(value.get('routes'), list) or len(value['routes']) > 24:
        raise ValueError('invalid pool')
    for route in value['routes']:
        if not route.get('name', '').startswith('paid_') or route.get('require_foreign') is not True:
            raise ValueError('invalid provider route')
    base = json.loads(Path('/etc/dagami-reserve/base-routes.json').read_text())
    base['routes'] = value['routes'] + base['routes'] if value.get('paid_first') is True else base['routes'] + value['routes']
    destination = Path('/var/lib/dagami-pool/routes.json')
    data = json.dumps(base, separators=(',', ':'))
    if not destination.exists() or destination.read_text() != data:
        atomic(destination, data, 0o640)


if __name__ == '__main__':
    failed = False
    while True:
        try:
            sync()
            if failed:
                print('Provider inventory synchronization recovered', flush=True)
            failed = False
        except Exception:
            if not failed:
                print('Provider inventory unavailable; retaining cached routes and their expiry dates', flush=True)
            failed = True
        time.sleep(10)
