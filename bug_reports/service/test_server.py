import base64
import concurrent.futures
import http.cookiejar
import io
import json
import os
import secrets
import subprocess
import tempfile
import threading
import unittest
import uuid
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from PIL import Image, PngImagePlugin
import server

PASSWORD = 'test-only-not-a-real-password'
HASH = server.password_hash(PASSWORD)


class Checks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='dagami-reports-test-')
        self.app = server.Reports(Path(self.temp.name)/'reports.sqlite', HASH, 'http://localhost', False)
        self.http = ThreadingHTTPServer(('127.0.0.1', 0), server.handler(self.app))
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.thread.start()
        self.url = 'http://127.0.0.1:' + str(self.http.server_port) + server.API
        self.cookie, self.csrf = '', ''

    def tearDown(self):
        self.http.shutdown(); self.http.server_close(); self.thread.join()
        self.temp.cleanup()

    def request(self, path, body=None, headers=None, raw=None):
        headers = {'Cookie': self.cookie, **(headers or {})}
        if body is not None:
            raw = json.dumps(body).encode()
            headers.setdefault('Content-Type', 'application/json')
        req = Request(self.url+path, data=raw, headers=headers)
        try:
            response = urlopen(req, timeout=10)
        except HTTPError as error:
            response = error
        with response:
            data = response.read()
            return response.status, (json.loads(data) if response.headers.get_content_type() == 'application/json' else data), response.headers

    def payload(self, image=False):
        result = dict(id=str(uuid.uuid4()), receipt_token=secrets.token_urlsafe(32), title='Console vanished', description='Launch agy from an ordinary terminal', contact='test@example.invalid', version='alpha.10 local', platform='Windows fixture', logs='HTTP_PROXY=http://user:proxy-secret@host.invalid:99\nAuthorization: Bearer oauth-secret\napi_key="key-secret"\nC:\\Users\\PrivateName\\app\nDAGAMI-AAAA-BBBB-CCCC\n', image_base64='')
        if image:
            buffer = io.BytesIO(); info = PngImagePlugin.PngInfo(); info.add_text('secret-metadata', 'private')
            Image.new('RGB', (24, 24), '#2155ce').save(buffer, format='PNG', pnginfo=info)
            result['image_base64'] = base64.b64encode(buffer.getvalue()).decode()
        return result

    def login(self):
        status, data, headers = self.request('/admin/login', {'password': PASSWORD})
        self.assertEqual(status, 200)
        self.cookie = headers['Set-Cookie'].split(';')[0]; self.csrf = data['csrf']
        return headers

    def test_submit_private_view_image_and_status(self):
        payload = self.payload(True)
        self.assertEqual(self.request('/reports', payload)[0], 201)
        self.assertEqual(self.request('/admin/reports')[0], 401)
        headers = self.login()
        self.assertIn('HttpOnly', headers['Set-Cookie']); self.assertIn('SameSite=Strict', headers['Set-Cookie'])
        status, listing, _ = self.request('/admin/reports')
        self.assertEqual(status, 200); self.assertEqual(listing['total'], 1)
        self.assertNotIn('logs', listing['reports'][0]); self.assertNotIn('contact', listing['reports'][0])
        status, detail, headers = self.request('/admin/reports/'+payload['id'])
        self.assertEqual(status, 200); self.assertEqual(detail['contact'], payload['contact'])
        for secret in ('proxy-secret','oauth-secret','key-secret','PrivateName','AAAA-BBBB-CCCC'):
            self.assertNotIn(secret, detail['logs'])
        self.assertEqual(headers['Cache-Control'], 'no-store')
        _, image, headers = self.request('/admin/reports/'+payload['id']+'/image')
        self.assertEqual(headers.get_content_type(), 'image/png')
        with Image.open(io.BytesIO(image)) as im:
            self.assertNotIn('secret-metadata', im.info); self.assertEqual(im.size, (24,24))
        path = '/admin/reports/'+payload['id']+'/status'
        self.assertEqual(self.request(path, {'status':'fixed'})[0], 403)
        self.assertEqual(self.request(path, {'status':'fixed'}, {'X-CSRF-Token':self.csrf})[0], 200)
        self.cookie = ''
        self.assertEqual(self.request('/admin/reports/'+payload['id']+'/image')[0], 401)
        self.assertEqual(self.request('/reports/'+payload['id'])[0], 404)
        status, data, _ = self.request('/reports/'+payload['id'], headers={'Authorization':'Bearer '+payload['receipt_token']})
        self.assertEqual(status, 200); self.assertEqual(data['status'], 'fixed'); self.assertNotIn('logs', data)

    def test_idempotency_and_conflicting_content(self):
        payload=self.payload()
        self.assertEqual(self.request('/reports', payload)[0], 201)
        self.assertTrue(self.request('/reports', payload)[1]['duplicate'])
        payload['description'] += ' different'
        self.assertEqual(self.request('/reports', payload)[0], 409)
        with self.app.connect() as db:
            self.assertEqual(db.execute('SELECT count(*) FROM reports').fetchone()[0], 1)

    def test_concurrent_idempotency(self):
        payload=self.payload(True)
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            results=list(pool.map(lambda _:self.request('/reports',payload)[0],range(4)))
        self.assertTrue(all(code in (201,503) for code in results), results)
        self.assertIn(201,results)
        self.assertEqual(self.request('/reports',payload)[0],201)
        with self.app.connect() as db:
            self.assertEqual(db.execute('SELECT count(*) FROM reports').fetchone()[0],1)

    def test_invalid_input(self):
        for field, value in [('id','../invalid'),('receipt_token','short'),('title','x'),('description',[]),('logs','x'*65537),('image_base64',42)]:
            payload=self.payload(); payload[field]=value
            self.assertIn(self.request('/reports', payload)[0],(400,413))
        self.assertEqual(self.request('/reports', [], {})[0],400)
        self.assertEqual(self.request('/reports',raw=b'{}',headers={'Content-Type':'text/plain'})[0],415)
        self.assertEqual(self.request('/reports',raw=b'{',headers={'Content-Type':'application/json'})[0],400)

    def test_reject_fake_svg_truncated_and_oversized_images(self):
        for image in [b'<svg onload="alert(1)"></svg>', b'not an image', b'\x89PNG\r\n\x1a\n']:
            payload=self.payload();payload['image_base64']=base64.b64encode(image).decode()
            self.assertEqual(self.request('/reports',payload)[0],400)
        payload=self.payload();payload['image_base64']='A'*(server.MAX_IMAGE*4//3+10)
        self.assertEqual(self.request('/reports',payload)[0],413)

    def test_wrong_origin_login_logout_and_session_expiry(self):
        self.assertEqual(self.request('/reports',self.payload(),{'Origin':'https://evil.invalid'})[0],403)
        self.assertEqual(self.request('/admin/login',{'password':'wrong'})[0],401)
        self.login();self.assertEqual(self.request('/admin/session')[0],200)
        self.assertEqual(self.request('/admin/logout',{}, {'X-CSRF-Token':self.csrf})[0],200)
        self.assertEqual(self.request('/admin/session')[0],401)
        self.login();self.app.sessions.clear();self.assertEqual(self.request('/admin/session')[0],401)

    def test_login_rate_and_report_rate(self):
        for _ in range(8):self.assertEqual(self.request('/admin/login',{'password':'wrong'})[0],401)
        self.assertEqual(self.request('/admin/login',{'password':PASSWORD})[0],429)
        for _ in range(12):self.assertEqual(self.request('/reports',self.payload())[0],201)
        self.assertEqual(self.request('/reports',self.payload())[0],429)

    def test_filters_persistence_storage_limit(self):
        self.assertEqual(self.request('/reports',self.payload())[0],201)
        reopened=server.Reports(self.app.database,HASH)
        with reopened.connect() as db:self.assertEqual(db.execute('SELECT count(*) FROM reports').fetchone()[0],1)
        self.app.max_reports=1
        self.assertEqual(self.request('/reports',self.payload())[0],503)
        self.login()
        self.assertEqual(self.request('/admin/reports?status=fixed')[1]['total'],0)
        self.assertEqual(self.request('/admin/reports?status=invalid')[0],400)
        self.assertEqual(self.request('/admin/reports?offset=oops')[0],400)

    def test_json_cookie_redaction_and_daily_retention(self):
        from maintenance import maintain
        self.assertNotIn('cookie-secret',server.clean('{"Cookie":"cookie-secret"}'))
        payload=self.payload();self.assertEqual(self.request('/reports',payload)[0],201)
        with self.app.connect() as db:
            db.execute('UPDATE reports SET created=1')
        maintain(self.app.database)
        with self.app.connect() as db:
            self.assertEqual(db.execute('SELECT count(*) FROM reports').fetchone()[0],0)
        backups=list((Path(self.app.database).parent/'backups').glob('*.sqlite'))
        self.assertEqual(len(backups),1)
        import sqlite3
        from contextlib import closing
        with closing(sqlite3.connect(backups[0])) as db:
            self.assertEqual(db.execute('SELECT count(*) FROM reports').fetchone()[0],1)

    def test_status_acknowledgement_waits_for_commit(self):
        import time
        from contextlib import contextmanager
        payload=self.payload();self.assertEqual(self.request('/reports',payload)[0],201)
        self.login()
        original=self.app.connect
        @contextmanager
        def delayed_commit():
            with original() as db:
                yield db
                time.sleep(0.2)
        self.app.connect=delayed_commit
        status,_,_=self.request('/admin/reports/'+payload['id']+'/status',{'status':'fixed'},{'X-CSRF-Token':self.csrf})
        self.assertEqual(status,200)
        with original() as db:
            self.assertEqual(db.execute('SELECT status FROM reports WHERE id=?',(payload['id'],)).fetchone()[0],'fixed')

    def test_production_admin_requires_private_ssh_channel(self):
        self.app.admin_channel_required=True
        self.assertEqual(self.request('/admin/login',{'password':PASSWORD})[0],404)
        self.assertEqual(self.request('/admin/reports')[0],404)
        private={'X-Dagami-Admin-Channel':'ssh-loopback','Origin':'http://localhost:3093'}
        status,data,headers=self.request('/admin/login',{'password':PASSWORD},private)
        self.assertEqual(status,200)
        self.assertIn('HttpOnly',headers['Set-Cookie'])
        self.assertNotIn('; Secure',headers['Set-Cookie'])
        self.cookie=headers['Set-Cookie'].split(';')[0]
        self.assertEqual(self.request('/admin/reports',headers=private)[0],200)
        self.assertEqual(self.request('/admin/reports')[0],404)
        wrong={**private,'Origin':'http://evil.invalid'}
        self.assertEqual(self.request('/admin/reports',headers=wrong)[0],403)

    @unittest.skipUnless(os.environ.get('REPORTS_NATIVE_CHECKS'), 'native integration executable not supplied')
    def test_native_client_end_to_end(self):
        native_dir=str(Path(os.environ['REPORTS_NATIVE_CHECKS']).resolve().parent)
        result=subprocess.run([os.environ['REPORTS_NATIVE_CHECKS'],self.url+'/reports',self.temp.name],capture_output=True,text=True,timeout=40,env=dict(os.environ,TEMP=native_dir,TMP=native_dir))
        self.assertEqual(result.returncode,0,result.stdout+result.stderr)
        self.login()
        rows=self.request('/admin/reports')[1]['reports'];self.assertEqual(len(rows),1)
        detail=self.request('/admin/reports/'+rows[0]['id'])[1]
        self.assertTrue(detail['has_image']);self.assertNotIn('native-secret',detail['logs'])


if __name__=='__main__':
    unittest.main(verbosity=2)
