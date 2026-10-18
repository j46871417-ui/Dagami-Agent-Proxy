"""Build/install plans on fixtures; no compiler, install or upload is invoked."""
import contextlib
import io
import os
from pathlib import Path
import plistlib
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import build_portable
import build_native_app
import install_portable
from devproxy_pkg.core.process_env import external_environment, independent_environment


class BuildTests(unittest.TestCase):
    def test_windows_package_has_one_executable_in_the_canonical_folder(self):
        with tempfile.TemporaryDirectory() as temp:
            output=Path(temp).resolve();calls=[]
            def command(args,**kwargs):
                calls.append(args)
                self.assertEqual('build_native_app.py',Path(args[1]).name)
                target=Path(args[args.index('--output-dir')+1]);target.mkdir(parents=True)
                (target/'DagamiAgentProxy.exe').write_bytes(b'fixture')
            with patch.object(sys,'platform','win32'),patch.object(sys,'argv',['build_portable.py','--output-dir',str(output)]),patch.object(build_portable.platform,'machine',return_value='AMD64'),patch.object(build_portable.subprocess,'run',side_effect=command),contextlib.redirect_stdout(io.StringIO()):
                build_portable.main()
            self.assertEqual(1,len(calls));self.assertEqual(output/'windows',Path(calls[0][-1]))
            self.assertEqual(['DagamiAgentProxy.exe'],[path.name for path in (output/'windows').glob('*.exe')])
            with zipfile.ZipFile(output/'dagami-agent-proxy-windows-x64.zip') as archive:
                self.assertEqual(['DagamiAgentProxy.exe'],[name for name in archive.namelist() if name.lower().endswith('.exe')])

    def test_native_default_does_not_write_into_sources_or_build_companions(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp).resolve();compiler=root/'Microsoft.NET'/'Framework64'/'v4.0.30319'/'csc.exe'
            compiler.parent.mkdir(parents=True);compiler.write_text('fixture')
            calls=[]
            def compile(args,**kwargs):
                calls.append(args)
                target=next(arg[5:] for arg in args if arg.startswith('/out:'))
                Path(target).write_bytes(b'fixture')
            with patch.object(build_native_app,'ROOT',root),patch.dict(os.environ,{'WINDIR':str(root)}),patch.object(sys,'argv',['build_native_app.py']),patch.object(build_native_app.subprocess,'run',side_effect=compile),contextlib.redirect_stdout(io.StringIO()):
                build_native_app.main()
                destination=root/'dist-portable'/'windows'
                self.assertEqual(['DagamiAgentProxy.exe'],[p.name for p in destination.glob('*.exe')])
                self.assertFalse(list(root.glob('*.exe')));self.assertEqual(1,len(calls))
                (destination/'DagamiDns.exe').write_bytes(b'old')
                with self.assertRaisesRegex(RuntimeError,'archive them first'):build_native_app.main()
                self.assertEqual(1,len(calls))

    def test_source_archive_whitelist_drops_local_files_and_rejects_secrets(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp).resolve();output=root/'out'
            files=['devproxy_pkg/entry.py','devproxy_pkg/.env','devproxy_pkg/state.json',
                   'devproxy_pkg/secrets.key','devproxy_pkg/__pycache__/entry.pyc',
                   'devproxy_pkg/assets/dns-domains.json','tests/check.py','tests/session.log']
            for name in files:
                path=root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text('fixture')
            with patch.object(build_portable,'ROOT',root),patch.object(sys,'argv',['build_portable.py','--source-only','--output-dir',str(output)]):
                build_portable.main()
                with zipfile.ZipFile(output/'dagami-agent-proxy-source.zip') as package:
                    self.assertEqual({'devproxy_pkg/entry.py','devproxy_pkg/assets/dns-domains.json','tests/check.py'},set(package.namelist()))
                original=(output/'dagami-agent-proxy-source.zip').read_bytes()
                (root/'devproxy_pkg/entry.py').write_text('ghp_'+'x'*40)
                with self.assertRaisesRegex(RuntimeError,'value redacted'):build_portable.main()
                self.assertEqual(original,(output/'dagami-agent-proxy-source.zip').read_bytes())
                self.assertFalse(list(output.glob('*.tmp')))

    def test_linux_and_mac_build_one_entry_no_companion(self):
        for platform in ('linux','darwin'):
            with self.subTest(platform=platform),tempfile.TemporaryDirectory() as temp:
                root=Path(temp);calls=[]
                def command(args,**kw):
                    calls.append(args)
                    if 'PyInstaller' in args:
                        folder=Path(args[args.index('--distpath')+1]);name=args[args.index('--name')+1]
                        if platform=='darwin':
                            main=folder/(name+'.app')/'Contents'/'MacOS'/name
                            duplicate=folder/name/name;duplicate.parent.mkdir(parents=True);duplicate.write_text('not shipped')
                        else:main=folder/name
                        main.parent.mkdir(parents=True,exist_ok=True);main.write_text('fixture')
                    return type('Result',(),{'returncode':0})()
                with patch.object(sys,'platform',platform),patch.object(sys,'argv',['build_portable.py','--output-dir',str(root)]),patch.object(build_portable.platform,'machine',return_value='x86_64'),patch.object(build_portable,'tcl_data',return_value=[]),patch.object(build_portable.importlib.util,'find_spec',return_value=object()),patch.object(build_portable.subprocess,'run',side_effect=command),contextlib.redirect_stdout(io.StringIO()):
                    build_portable.main()
                builds=[args for args in calls if 'PyInstaller' in args];self.assertEqual(1,len(builds))
                self.assertEqual('devproxy_gui.py',Path(builds[0][-1]).name)
                self.assertIn('--onefile' if platform=='linux' else '--onedir',builds[0])
                self.assertEqual(calls[1][0],calls[2][0]);self.assertEqual('--help',calls[1][1]);self.assertEqual('--smoke-gui',calls[2][1])
                archive=next(root.glob('dagami-agent-proxy-'+('linux' if platform=='linux' else 'macos')+'-*.zip'))
                with zipfile.ZipFile(archive) as package:
                    names=package.namelist()
                    self.assertNotIn('dagami-proxy',names)
                    executables=[n for n in names if n=='dagami-agent-proxy' or '/MacOS/' in n]
                    self.assertEqual(1,len(executables))
                    self.assertFalse(any('not shipped' in package.read(n).decode(errors='ignore') for n in executables))
                with zipfile.ZipFile(root/'dagami-agent-proxy-source.zip') as source:
                    names=source.namelist();self.assertIn('devproxy_pkg/assets/dns-domains.json',names)
                    self.assertFalse(any('__pycache__' in n or n.startswith(('.review/','docs/dagami-infra/','.env')) or n.endswith(('.exe','.key','.db')) for n in names))


class InstallerTests(unittest.TestCase):
    def test_one_source_launcher_and_safe_upgrade_for_both_platforms(self):
        for platform in ('linux','darwin'):
            with self.subTest(platform=platform),tempfile.TemporaryDirectory() as temp:
                home=Path(temp).resolve();share=home/'share'
                with patch.object(sys,'platform',platform),patch.object(Path,'home',return_value=home),patch.dict(os.environ,{'XDG_DATA_HOME':str(share)}),patch.object(sys,'argv',['install_portable.py']),contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(0,install_portable.main())
                    launchers=list(home.rglob('dagami-agent-proxy'))
                    self.assertEqual(1,len(launchers));launcher=launchers[0]
                    self.assertIn('"$@"',launcher.read_text());original=launcher.read_bytes()
                    if platform=='darwin':
                        info=plistlib.loads((launcher.parent.parent/'Info.plist').read_bytes());self.assertEqual(launcher.name,info['CFBundleExecutable'])
                    with patch.object(sys,'argv',['install_portable.py','--upgrade']):
                        self.assertEqual(0,install_portable.main())
                        self.assertTrue(list(home.rglob('DagamiAgentProxy.backup-*')))
                        self.assertEqual(original,launcher.read_bytes())
                        launcher.write_text('foreign edit')
                        with self.assertRaisesRegex(SystemExit,'externally changed'):install_portable.main()
                        self.assertEqual('foreign edit',launcher.read_text())


class ProcessEnvironmentTests(unittest.TestCase):
    def test_frozen_linux_external_tools_do_not_load_bundled_libraries(self):
        with patch.object(sys,'platform','linux'),patch.object(sys,'frozen',True,create=True),patch.object(sys,'_MEIPASS','/tmp/dagami',create=True),patch.dict(os.environ,{'LD_LIBRARY_PATH':'/tmp/dagami:/user/lib','LD_LIBRARY_PATH_ORIG':'/user/lib','_PYI_ARCHIVE_FILE':'/app/binary','PATH':'/usr/bin'+os.pathsep+'/tmp/dagami/bin','HTTP_PROXY':'keep'},clear=True):
            env=external_environment({'LANG':'C'})
            self.assertEqual('/user/lib',env['LD_LIBRARY_PATH']);self.assertEqual('/usr/bin',env['PATH'])
            self.assertEqual('keep',env['HTTP_PROXY']);self.assertEqual('C',env['LANG'])
            self.assertNotIn('_PYI_ARCHIVE_FILE',env);self.assertNotIn('LD_LIBRARY_PATH_ORIG',env)
            self.assertNotIn('PYINSTALLER_RESET_ENVIRONMENT',env)
            self.assertEqual('1',independent_environment()['PYINSTALLER_RESET_ENVIRONMENT'])
    def test_no_original_ld_path_removes_frozen_path(self):
        with patch.object(sys,'platform','linux'),patch.object(sys,'frozen',True,create=True),patch.dict(os.environ,{'LD_LIBRARY_PATH':'bundle'},clear=True):
            self.assertNotIn('LD_LIBRARY_PATH',external_environment())
    def test_source_mode_preserves_host_environment(self):
        with patch.object(sys,'frozen',False,create=True),patch.dict(os.environ,{'LD_LIBRARY_PATH':'host'},clear=True):
            self.assertEqual('host',external_environment()['LD_LIBRARY_PATH'])


if __name__=='__main__':unittest.main()
