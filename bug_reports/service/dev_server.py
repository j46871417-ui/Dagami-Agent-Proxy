"""Loopback-only preview. Dummy credentials and fixture reports, never deploy."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit
from server import Reports, handler, password_hash, API

parser=argparse.ArgumentParser()
parser.add_argument('--port',type=int,default=3092)
parser.add_argument('--data',required=True,type=Path)
args=parser.parse_args()
app=Reports(args.data/'preview.sqlite',password_hash('local-preview-fixture-only'),origin='http://127.0.0.1:'+str(args.port),secure=False)
ApiHandler=handler(app)

class Preview(ApiHandler,SimpleHTTPRequestHandler):
    def do_GET(self):
        if urlsplit(self.path).path.startswith(API+'/'):
            self.dispatch('GET')
        else:
            SimpleHTTPRequestHandler.do_GET(self)

root=Path(__file__).resolve().parent.parent/'public'
ThreadingHTTPServer(('127.0.0.1',args.port),partial(Preview,directory=str(root))).serve_forever()
