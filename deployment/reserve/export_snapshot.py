#!/usr/bin/env python3
"""Forced SSH command on the issuing node: output no keys or credentials."""
import datetime
import json
from pathlib import Path
import sqlite3
import time


def export():
    config = json.loads(Path('/etc/agw-gateway/controller.json').read_text())
    overlay = json.loads(Path('/etc/agw-gateway/ide-allowlist.json').read_text())
    state = json.loads(Path('/var/lib/dagami-managed/devices.json').read_text())
    if state.get('version') != 1:
        raise ValueError('unknown registry schema')
    with sqlite3.connect('file:' + config['database'] + '?mode=ro', uri=True, timeout=5) as db:
        rows = db.execute('SELECT login,subject,scope,expires FROM accounts').fetchall()
    # A stopped exporter/bridge must not erase every previously active device.
    if not rows or max(row[3] for row in rows) <= time.time():
        raise ValueError('primary authorization snapshot is stale')
    accounts = {row[0]: {'subject': str(row[1]), 'scope': row[2]} for row in rows if row[3] > time.time()}
    devices = {key: value for key, value in state['devices'].items() if value['login'] in accounts and accounts[value['login']]['subject'] == value['subject']}
    result = {'version': 1, 'generated_at': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'devices': devices, 'accounts': accounts}
    for scope in ('allowed', 'google'):
        result[scope + '_hosts'] = sorted(set(config[scope + '_hosts']) | set(overlay['exact_hosts']))
        result[scope + '_suffixes'] = sorted(set(config[scope + '_suffixes']) | set(overlay['domain_suffixes']))
    result['google_networks'] = config['google_networks']
    return result


if __name__ == '__main__':
    try:
        print(json.dumps(export(), separators=(',', ':')))
    except Exception:
        raise SystemExit('snapshot export failed') from None
