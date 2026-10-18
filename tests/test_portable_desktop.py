import contextlib
import io
import json
import os
from pathlib import Path
import plistlib
import sys
import tempfile
import unittest
from unittest.mock import patch
from devproxy_pkg.portable_startup import Startup, LABEL
from devproxy_pkg.portable_instance import Instance
from devproxy_pkg import entry, system_dns
from devproxy_pkg.dns_desktop import confirm_reset


class StartupTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name).resolve()
    def make(self, platform='linux',command=None):
        return Startup(platform,self.root,command or ['/opt/Dagami Proxy/bin'])
    def test_linux_enable_disable_quotes_and_no_terminal(self):
        item=self.make();self.assertFalse(item.enabled());self.assertTrue(item.set_enabled(True))
        text=item.path.read_text();self.assertIn('Exec="/opt/Dagami Proxy/bin" "--autostart"',text)
        self.assertIn('Terminal=false',text);self.assertIn('Type=Application',text)
        self.assertFalse(item.set_enabled(False));self.assertFalse(item.path.exists())
    def test_mac_launchagent_uses_user_executable_no_sudo(self):
        item=self.make('darwin');item.set_enabled(True);value=plistlib.loads(item.path.read_bytes())
        self.assertEqual(['/opt/Dagami Proxy/bin','--autostart'],value['ProgramArguments'])
        self.assertTrue(value['RunAtLoad']);self.assertFalse(value['KeepAlive'])
        self.assertEqual(LABEL,value['Label']);self.assertNotIn('UserName',value)
        self.assertNotIn('sudo',item.path.read_text())
    def test_external_file_and_external_edit_protected(self):
        item=self.make();item.path.parent.mkdir(parents=True);item.path.write_text('external')
        for enabled in (True,False):
            with self.assertRaisesRegex(RuntimeError,'вне приложения'):item.set_enabled(enabled)
        self.assertEqual('external',item.path.read_text())
        item.path.unlink();item.set_enabled(True);item.path.write_text('edited')
        with self.assertRaisesRegex(RuntimeError,'изменён'):item.set_enabled(False)
        self.assertEqual('edited',item.path.read_text())
    def test_relocation_rewrites_only_managed_entry(self):
        item=self.make();item.set_enabled(True);moved=self.make(command=['/new/location'])
        self.assertFalse(moved.enabled());moved.set_enabled(True)
        self.assertIn('/new/location',moved.path.read_text())
        self.assertTrue(moved.enabled())
    def test_failed_journal_write_removes_new_launcher(self):
        item=self.make();original=system_dns.atomic
        def fail(path,data,*args):
            if path==item.journal:raise OSError('disk full')
            return original(path,data,*args)
        with patch('devproxy_pkg.portable_startup.atomic',side_effect=fail):
            with self.assertRaises(OSError):item.set_enabled(True)
        self.assertFalse(item.path.exists());self.assertFalse(item.journal.exists())
    def test_no_command_injection_in_startup(self):
        for command in (['/app\nExec=bad'],['/app\x00']):
            with self.assertRaises(ValueError):self.make(command=command)
    def test_symlink_protected(self):
        item=self.make();item.path.parent.mkdir(parents=True);target=self.root/'target';target.write_text('foreign')
        try:item.path.symlink_to(target)
        except OSError:self.skipTest('symlink privilege unavailable')
        with self.assertRaises(RuntimeError):item.set_enabled(True)
        self.assertEqual('foreign',target.read_text())


class InstanceTests(unittest.TestCase):
    def test_second_instance_shows_primary_and_crash_lock_releases(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            with Instance(root) as first:
                self.assertTrue(first.primary);self.assertFalse(first.consume_show())
                with Instance(root) as second:self.assertFalse(second.primary)
                self.assertTrue(first.consume_show());self.assertFalse(first.consume_show())
            with Instance(root) as third:self.assertTrue(third.primary)


class EntryTests(unittest.TestCase):
    def test_slow_worker_command_is_not_reported_as_successful_null(self):
        import socket
        import http.client
        from devproxy_pkg import portable_runtime as runtime
        with tempfile.TemporaryDirectory() as temp,patch.dict(os.environ,{'DEVPROXY_HOME':temp}):
            runtime.descriptor('vscode').write_text(json.dumps({'port':12345,'token':'a'*64}))
            with patch.object(http.client,'HTTPConnection') as connection:
                connection.return_value.request.side_effect=socket.timeout('fixture')
                self.assertIsNone(runtime.request('vscode'))
                self.assertEqual(15,connection.call_args.kwargs['timeout'])
                with self.assertRaisesRegex(RuntimeError,'не подтвердила'):runtime.request('vscode','open')
                self.assertEqual(60,connection.call_args.kwargs['timeout'])

    def test_gui_and_startup_share_entry(self):
        with patch('devproxy_pkg.desktop.main',return_value=0) as gui,patch.object(os,'geteuid',return_value=1000,create=True):
            self.assertEqual(0,entry.main([]));gui.assert_called_with(autostart=False)
            self.assertEqual(0,entry.main(['--autostart']));gui.assert_called_with(autostart=True)
    def test_privileged_helper_does_not_open_gui(self):
        with patch('devproxy_pkg.system_dns.helper',return_value=7) as helper,patch('devproxy_pkg.desktop.main') as gui:
            self.assertEqual(7,entry.main(['--system-dns-helper','payload']))
            helper.assert_called_once_with('payload');gui.assert_not_called()
    def test_frozen_helper_reuses_one_binary(self):
        from devproxy_pkg.portable_runtime import entry_command
        with patch.object(sys,'frozen',True,create=True),patch.object(sys,'executable','/app/Dagami'):
            self.assertEqual(['/app/Dagami'],entry_command())
    def test_dns_reset_no_second_confirmation_no_elevation(self):
        for answers in (['нет'],['ДА','нет']):
            with patch('builtins.input',side_effect=answers),patch('devproxy_pkg.system_dns.execute') as elevated:
                self.assertEqual(1,entry.main(['dns','reset']));elevated.assert_not_called()
    def test_dns_reset_two_yes_then_one_elevation(self):
        with patch('builtins.input',side_effect=['ДА','ДА']),patch('devproxy_pkg.system_dns.execute',return_value='cleared') as elevated,contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0,entry.main(['dns','reset']))
            self.assertTrue(elevated.call_args.kwargs['confirmed']);self.assertEqual(1,elevated.call_count)
    def test_dialog_exact_question_twice_and_cancel(self):
        calls=[]
        def yes(title,text):calls.append(text);return True
        self.assertTrue(confirm_reset(yes));self.assertEqual(2,len(calls))
        self.assertTrue(all('Вы точно уверены в том что вы делаете?' in text for text in calls))
        self.assertFalse(confirm_reset(lambda *_:False))
    def test_elevation_payload_not_shell_interpolated_and_reset_environment(self):
        import base64
        result=type('Result',(),{'stderr':'denied'})()
        with patch.object(sys,'platform','linux'),patch('devproxy_pkg.system_dns.tool',side_effect=lambda n:'/usr/bin/'+n),patch('devproxy_pkg.portable_runtime.entry_command',return_value=['/app/with spaces']),patch('devproxy_pkg.system_dns.subprocess.run',return_value=result) as run,patch.dict(os.environ,{'_PYI_APPLICATION_HOME_DIR':'user-extract'}):
            with self.assertRaisesRegex(RuntimeError,'не подтверждена'):system_dns.execute('apply',server='1.1.1.1',mode='domains')
            args=run.call_args.args[0]
            self.assertEqual(['/usr/bin/pkexec','/usr/bin/env','PYINSTALLER_RESET_ENVIRONMENT=1','/app/with spaces','--system-dns-helper'],args[:-1])
            self.assertEqual('1.1.1.1',json.loads(base64.b64decode(args[-1]))['server'])
            self.assertNotIn('_PYI_APPLICATION_HOME_DIR',run.call_args.kwargs['env'])
    def test_legacy_cli_commands_still_dispatch(self):
        with patch('devproxy_pkg.cli.main',return_value=5) as cli:
            self.assertEqual(5,entry.main(['apps','list']));cli.assert_called_once()


class DesktopTests(unittest.TestCase):
    def test_gui_logo_styles_dns_busy_guard_and_no_tray_fallback(self):
        import tkinter as tk
        from tkinter import ttk
        from devproxy_pkg.desktop import Desktop
        from devproxy_pkg.dns_desktop import DnsDialog
        import time
        class System:
            def services(self):return [{'id':'wifi','name':'Wi-Fi'}]
        with tempfile.TemporaryDirectory() as temp,patch.dict(os.environ,{'DEVPROXY_HOME':str(Path(temp)/'state'),'XDG_DATA_HOME':str(Path(temp)/'share')}):
            try:app=Desktop(tray=False)
            except tk.TclError as exc:self.skipTest('No GUI display: '+str(exc))
            try:
                app.withdraw();app.update()
                self.assertEqual(64,app.app_icon.width())
                style=ttk.Style(app);self.assertEqual('#087fc1',style.lookup('Accent.TButton','background'))
                self.assertEqual('#c62626',style.lookup('Danger.TButton','background'))
                app.hide_after_login();self.assertIn('оставлено открытым',app.notice.get())
                dialog=DnsDialog(app,system=System(),execute=lambda *_args,**_kw:'fixture')
                app.dns_dialog=dialog
                deadline=time.monotonic()+3
                while dialog.busy and time.monotonic()<deadline:app.update();time.sleep(.02)
                self.assertEqual(('wifi',),dialog.services.get_children())
                self.assertTrue(all(button.winfo_reqwidth()>20 for button in dialog.buttons))
                dialog.busy=True
                with patch('devproxy_pkg.desktop.messagebox.showinfo'):
                    self.assertFalse(app.exit_app());self.assertTrue(app.winfo_exists())
                dialog.busy=False;dialog.destroy()
            finally:app.destroy()

    def test_linux_controls_fit_minimum_window(self):
        import tkinter as tk
        from tkinter import ttk
        from devproxy_pkg.desktop import Desktop
        from devproxy_pkg.dns_desktop import DnsDialog
        class System:
            def services(self):return []
        with tempfile.TemporaryDirectory() as temp,patch.dict(os.environ,{'DEVPROXY_HOME':str(Path(temp)/'state')}),patch.object(sys,'platform','linux'),patch('devproxy_pkg.desktop.list_adapters',return_value=[]),patch('devproxy_pkg.portable_startup.Startup') as startup:
            startup.return_value.enabled.return_value=False
            try:app=Desktop(tray=False)
            except tk.TclError as exc:self.skipTest('No GUI display: '+str(exc))
            try:
                app.geometry('900x600');app.update()
                def check(window):
                    def walk(widget):
                        for child in widget.winfo_children():
                            if isinstance(child,(ttk.Button,ttk.Checkbutton,ttk.Combobox)) and child.winfo_viewable():
                                left=child.winfo_rootx()-window.winfo_rootx()
                                self.assertGreaterEqual(left,0)
                                self.assertLessEqual(left+child.winfo_width(),window.winfo_width())
                            walk(child)
                    walk(window)
                check(app)
                dialog=DnsDialog(app,system=System());dialog.geometry('780x610');app.update();check(dialog)
            finally:app.destroy()


if __name__=='__main__':unittest.main()
