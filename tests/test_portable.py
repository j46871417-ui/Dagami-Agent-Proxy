import base64
import contextlib
import http.client
import json
import os
from pathlib import Path
import plistlib
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from devproxy_pkg import portable_runtime as runtime
from devproxy_pkg.portable_shortcuts import PortableShortcuts, rewrite_desktop, desktop_quote
from devproxy_pkg.core.profile import ProxyProfile
from devproxy_pkg.core.recovery import StateManager

ROOT=Path(__file__).resolve().parents[1]

class Isolated(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        self.env=patch.dict(os.environ,{'DEVPROXY_HOME':str(self.root/'state'),'XDG_DATA_HOME':str(self.root/'share'),'XDG_DATA_DIRS':str(self.root/'system')})
        self.env.start();StateManager.save_profile(ProxyProfile.parse('http://127.0.0.1:9',name='fixture-no-secret').to_dict())
    def tearDown(self):self.env.stop();self.temp.cleanup()

class ShortcutTests(Isolated):
    def make(self,platform='linux'):
        return PortableShortcuts(self.root/'journal',platform,self.root)
    def profile(self):return patch('devproxy_pkg.portable_shortcuts.load_profile',return_value=ProxyProfile.parse('http://127.0.0.1:9'))
    def test_linux_overlay_and_byte_exact_restore(self):
        source=self.root/'system'/'applications'/'code.desktop';source.parent.mkdir(parents=True)
        raw='[Desktop Entry]\nType=Application\nName=Code\nIcon=code\nExec=/opt/code --new-window %F\nDBusActivatable=true\nActions=new-window;\nPath=/tmp\n[Desktop Action new-window]\nExec=/opt/code --new-window\n'
        source.write_text(raw,encoding='utf-8');links=self.make()
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value='/opt/code'):
            self.assertTrue(links.enable('vscode',profile='fixture-no-secret')['enabled'])
            target=self.root/'share'/'applications'/'code.desktop';text=target.read_text()
            self.assertIn(' -- --new-window %F',text);self.assertIn('Icon=code',text);self.assertIn('Path=/tmp',text)
            self.assertNotIn('DBusActivatable=true',text);self.assertNotIn('\nActions=',text)
            before=target.read_bytes();links.enable('vscode',profile='fixture-no-secret');self.assertEqual(before,target.read_bytes())
            links.restore('vscode');self.assertFalse(target.exists());self.assertEqual(raw,source.read_text())
    def test_linux_existing_user_entry_restored(self):
        source=self.root/'share'/'applications'/'code.desktop';source.parent.mkdir(parents=True)
        original=b'[Desktop Entry]\nName=Custom\nExec=/opt/code %U\n';source.write_bytes(original)
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value='/opt/code'):
            links=self.make();links.enable('vscode','fixture-no-secret');links.restore('vscode');self.assertEqual(original,source.read_bytes())
    def test_foreign_edit_and_missing_file_block_rollback(self):
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value='/opt/code'):
            links=self.make();links.enable('vscode','fixture-no-secret')
            path=Path(links._load()['vscode'][0]['path']);path.write_text('foreign edit')
            self.assertFalse(links.status('vscode')['enabled'])
            with self.assertRaises(RuntimeError):links.restore('vscode')
            self.assertEqual('foreign edit',path.read_text())
    def test_macos_wrapper_does_not_touch_original_bundle(self):
        original=self.root/'Original.app';original.mkdir();(original/'signed').write_text('signed')
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value=str(original/'editor')):
            links=self.make('darwin');self.assertTrue(links.enable('vscode','fixture-no-secret')['enabled'])
            records=links._load()['vscode'];info=plistlib.loads(Path(records[0]['path']).read_bytes());self.assertEqual('launch',info['CFBundleExecutable'])
            script=Path(records[1]['path']).read_text();self.assertIn('fixture-no-secret',script);self.assertNotIn('127.0.0.1:9',script)
            links.restore('vscode');self.assertEqual('signed',(original/'signed').read_text());self.assertFalse(Path(records[0]['path']).exists())
    def test_macos_cli_wrapper_opens_terminal(self):
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value='/usr/local/bin/codex'):
            links=self.make('darwin');links.enable('codex','fixture-no-secret')
            scripts=[Path(r['path']).read_text() for r in links._load()['codex'] if not r['path'].endswith('.plist')]
            self.assertTrue(any('/usr/bin/open -a Terminal' in s for s in scripts));links.restore('codex')
    def test_windows_explicit_native_boundary(self):
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value='C:/Code.exe'):
            with self.assertRaisesRegex(RuntimeError,'нативной'):self.make('win32').enable('vscode','fixture-no-secret')
    def test_field_codes_and_injection(self):
        self.assertIn('%%',desktop_quote('/path/percent%file'))
        with self.assertRaises(ValueError):desktop_quote('a\nExec=evil')
        with self.assertRaises(ValueError):rewrite_desktop('[Desktop Entry]\nExec=code %Z\n',['devproxy'])
    def test_failure_rolls_back_created_files(self):
        from devproxy_pkg import portable_shortcuts as module
        original=module.atomic;calls=[]
        def fail_second(path,data,mode=0o600):
            if str(path).endswith('/MacOS/launch') or str(path).endswith('\\MacOS\\launch'):raise OSError('fixture denial')
            calls.append(str(path));return original(path,data,mode)
        with self.profile(),patch('devproxy_pkg.portable_shortcuts.executable',return_value='/Applications/Code.app/Contents/MacOS/Electron'),patch.object(module,'atomic',side_effect=fail_second):
            links=self.make('darwin')
            with self.assertRaises(OSError):links.enable('vscode','fixture-no-secret')
            self.assertFalse(links.status('vscode')['enabled'])

class RuntimeTests(Isolated):
    def test_google_url_and_redaction(self):
        url='https://accounts.google.com/o/oauth2/auth?state=PRIVATE'
        self.assertEqual(url,runtime.validate_login(url));self.assertNotIn('PRIVATE',runtime.redact(url))
        self.assertNotIn('password',runtime.redact('https://alice:password@proxy.test:443'))
        for bad in ('http://accounts.google.com/','https://accounts.google.com.evil/','https://user@accounts.google.com/','https://accounts.google.com:444/'):
            with self.assertRaises(ValueError):runtime.validate_login(bad)
    def test_unknown_app_and_missing_profile_fail_before_spawn(self):
        with self.assertRaises(ValueError):runtime.start_session('../escape')
        with self.assertRaises(ValueError):runtime.start_session('vscode','missing')
    def test_missing_stored_password_fails_closed(self):
        state=ProxyProfile.parse('http://u:secret@127.0.0.1:9',name='fixture-password').to_dict();state['requires_password']=True;StateManager.save_profile(state)
        with patch.object(runtime.SecretStore,'get_password',return_value=None):
            with self.assertRaises(RuntimeError):runtime.load_profile('fixture-password')
    def test_register_mac_bundle_and_custom_binary(self):
        bundle=self.root/'My Editor.app';(bundle/'Contents'/'MacOS').mkdir(parents=True)
        (bundle/'Contents'/'Info.plist').write_bytes(plistlib.dumps({'CFBundleExecutable':'editor'}));(bundle/'Contents'/'MacOS'/'editor').write_text('fixture')
        self.assertTrue(runtime.register_executable('vscode',bundle).endswith('editor'))
    def test_cli_help_and_unknown_target_exit_codes(self):
        for args,code in [(['run','--help'],0),(['run','unknown'],1),(['patch','--help'],0)]:
            result=subprocess.run([sys.executable,str(ROOT/'devproxy_gui.py')]+args,capture_output=True,text=True,encoding='utf-8')
            self.assertEqual(code,result.returncode,result.stdout+result.stderr)
    def test_live_worker_bridge_reuse_auth_stop_and_secret_free_logs(self):
        output=self.root/'child.json'
        if os.name=='nt':
            compiler=Path(os.environ.get('WINDIR','C:/Windows'))/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
            if not compiler.exists():self.skipTest('No C# fixture compiler')
            fixture=self.root/'fixture.exe';source=self.root/'fixture.cs'
            source.write_text('using System; using System.IO; using System.Threading; using System.Web.Script.Serialization; class Fixture { static void Main(){ var data=new System.Collections.Generic.Dictionary<string,string>(); foreach(string k in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","AG_LS_PROXY","AGY_PROXY_URL","agy_proxy_url"})data[k]=Environment.GetEnvironmentVariable(k); data["pid"]=System.Diagnostics.Process.GetCurrentProcess().Id.ToString(); File.WriteAllText(Environment.GetEnvironmentVariable("DEVPROXY_FIXTURE_OUTPUT"),new JavaScriptSerializer().Serialize(data)); Console.WriteLine("FAKE_SECRET_MUST_NOT_BE_LOGGED");Thread.Sleep(60000); }}')
            subprocess.run([str(compiler),'/nologo','/target:exe','/out:'+str(fixture),'/r:System.Web.Extensions.dll',str(source)],check=True,capture_output=True)
        else:
            fixture=self.root/'fixture-editor';fixture.write_text('#!/usr/bin/env python3\nimport os,json,time\nfrom pathlib import Path\ndata={k:os.environ.get(k) for k in ("HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","AG_LS_PROXY","AGY_PROXY_URL","agy_proxy_url")}\ndata["pid"]=str(os.getpid())\nPath(os.environ["DEVPROXY_FIXTURE_OUTPUT"]).write_text(json.dumps(data))\nprint("FAKE_SECRET_MUST_NOT_BE_LOGGED",flush=True)\ntime.sleep(60)\n');fixture.chmod(0o700)
        listener=socket.socket();listener.bind(('127.0.0.1',0));listener.listen();listener.settimeout(.2);done=threading.Event()
        def upstream():
            while not done.is_set():
                try:peer,_=listener.accept()
                except socket.timeout:continue
                except OSError:return
                with peer:
                    peer.settimeout(5);data=b''
                    while b'\r\n\r\n' not in data:data+=peer.recv(4096)
                    peer.sendall(b'HTTP/1.1 200 OK\r\n\r\n')
                    while True:
                        data=peer.recv(4096)
                        if not data:break
                        peer.sendall(data)
        thread=threading.Thread(target=upstream,daemon=True);thread.start()
        StateManager.save_profile(ProxyProfile.parse('http://127.0.0.1:'+str(listener.getsockname()[1]),name='fixture-no-secret').to_dict());runtime.register_executable('vscode',fixture)
        spawned=[];original=subprocess.Popen
        def spawn(*a,**k):proc=original(*a,**k);spawned.append(proc);return proc
        try:
            with patch.dict(os.environ,{'DEVPROXY_FIXTURE_OUTPUT':str(output)}),patch.object(runtime.subprocess,'Popen',side_effect=spawn):data=runtime.start_session('vscode','fixture-no-secret')
            for _ in range(100):
                if output.exists():break
                time.sleep(.1)
            child=json.loads(output.read_text());self.assertTrue(data['running']);self.assertFalse(data['account_verified'])
            for key in ('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','AG_LS_PROXY','AGY_PROXY_URL','agy_proxy_url'):self.assertEqual(data['bridge'],child[key])
            self.assertEqual(data['pid'],runtime.start_session('vscode','fixture-no-secret')['pid']);self.assertEqual(child,json.loads(output.read_text()))
            port=int(data['bridge'].rsplit(':',1)[1])
            with socket.create_connection(('127.0.0.1',port),timeout=5) as client:
                client.sendall(b'CONNECT echo.invalid:443 HTTP/1.1\r\nHost: echo.invalid:443\r\n\r\n');headers=b''
                while b'\r\n\r\n' not in headers:headers+=client.recv(1024)
                self.assertIn(b' 200 ',headers);client.sendall(b'fixture-payload');self.assertEqual(b'fixture-payload',client.recv(100))
            with self.assertRaises(RuntimeError):runtime.start_session('vscode','another-profile')
            record=json.loads(runtime.descriptor('vscode').read_text());connection=http.client.HTTPConnection('127.0.0.1',record['port'],timeout=3)
            connection.request('POST','/',b'{}',{'Authorization':'Bearer wrong'});self.assertEqual(403,connection.getresponse().status);connection.close()
            with self.assertRaises(RuntimeError):runtime.request('vscode','login',url='http://accounts.google.com/?state=PRIVATE')
            log=(runtime.runtime_dir()/'vscode.log').read_text();self.assertNotIn('FAKE_SECRET',log);self.assertNotIn('PRIVATE',log)
        finally:
            runtime.request('vscode','stop')
            for process in spawned:
                try:process.wait(timeout=12)
                except subprocess.TimeoutExpired:process.kill();process.wait()
            done.set();listener.close();thread.join(timeout=2)
        self.assertIsNone(runtime.request('vscode'))

if __name__=='__main__':unittest.main()
