#!/usr/bin/env python3
"""Root-owned snapshot transport; its SSH key permits the export command only."""
import argparse
import grp
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
from pathlib import Path
import socketserver
import subprocess
import sys
import threading
import time

from pool_admin import atomic


class Sync:
    def __init__(self, config):
        self.config = config
        self.lock = threading.Lock()
        self.last = 0
        self.failed = False

    def refresh(self):
        with self.lock:
            process = subprocess.run(['/usr/bin/ssh', '-F', self.config, 'snapshot-source'], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=7, check=True)
            if len(process.stdout) > 4 * 1024 * 1024:
                raise ValueError('snapshot too large')
            value = json.loads(process.stdout)
            if value.get('version') != 1 or not isinstance(value.get('devices'), dict) or not isinstance(value.get('accounts'), dict):
                raise ValueError('invalid snapshot')
            destination = Path('/var/lib/dagami-reserve/snapshot.json')
            atomic(destination, process.stdout.decode(), 0o640)
            os.chown(destination, 0, grp.getgrnam('dagami-reserve').gr_gid)
            self.last = time.time()

    def refresh_loop(self):
        while True:
            try:
                self.refresh()
                if self.failed:
                    print('Snapshot synchronization recovered', flush=True)
                self.failed = False
            except Exception:
                if not self.failed:
                    print('Snapshot synchronization failed; retaining last valid bindings', flush=True)
                self.failed = True
            time.sleep(5)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        code = 404
        if self.path == '/refresh':
            try:
                self.server.sync.refresh()
                code = 204
            except Exception:
                code = 503
        self.send_response(code)
        self.send_header('Content-Length', '0')
        self.end_headers()


class UnixHTTP(socketserver.UnixStreamServer, HTTPServer):
    pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--ssh-config', default='/etc/dagami-reserve/snapshot-ssh.conf')
    parser.add_argument('--once', action='store_true')
    args = parser.parse_args()
    sync = Sync(args.ssh_config)
    if args.once:
        sync.refresh()
        return
    path = '/run/dagami-reserve-sync/refresh.sock'
    Path(path).unlink(missing_ok=True)
    server = UnixHTTP(path, Handler)
    server.sync = sync
    os.chown(path, 0, grp.getgrnam('dagami-reserve').gr_gid)
    os.chmod(path, 0o660)
    threading.Thread(target=sync.refresh_loop, daemon=True).start()
    server.serve_forever()


if __name__ == '__main__':
    main()
