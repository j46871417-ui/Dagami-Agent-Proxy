"""Install this source tree for the current user, without downloads or sudo."""
import argparse
from pathlib import Path
import os
import plistlib
import shlex
import shutil
import sys
import tempfile
import uuid

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--upgrade',action='store_true');args=parser.parse_args()
    if sys.platform not in ('darwin','linux'):raise SystemExit('Windows: use DagamiAgentProxy.exe and dagami-proxy.exe; portable Python: pip install .')
    source=Path(__file__).resolve().parent;home=Path.home()
    base=home/'Library'/'Application Support' if sys.platform=='darwin' else Path(os.environ.get('XDG_DATA_HOME',str(home/'.local'/'share')))
    target=base/'DagamiAgentProxy';base.mkdir(parents=True,exist_ok=True)
    bin_dir=home/'.local'/'bin'
    launchers=[('dagami-proxy','devproxy.py'),('dagami-agent-proxy','devproxy_gui.py')]
    for name,script in launchers:
        launcher=bin_dir/name
        if launcher.is_symlink():raise SystemExit('Launcher is a symlink; refusing replacement: '+str(launcher))
        if launcher.exists() and (not args.upgrade or str(target/script) not in launcher.read_text(encoding='utf-8')):
            raise SystemExit('Existing launcher retained: '+str(launcher))
    if target.is_symlink():raise SystemExit('Installation target is a symlink; refusing replacement.')
    if target.exists() and not args.upgrade:raise SystemExit('Installation exists. Use --upgrade; old files will be retained as a backup.')
    with tempfile.TemporaryDirectory(prefix='.devproxy-install-',dir=base) as temp:
        stage=Path(temp)/'app';stage.mkdir()
        for name in ('devproxy.py','devproxy_gui.py'):shutil.copyfile(source/name,stage/name)
        shutil.copytree(source/'devproxy_pkg',stage/'devproxy_pkg',ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
        if target.exists():target.rename(base/('DagamiAgentProxy.backup-'+uuid.uuid4().hex))
        stage.rename(target)
    bin_dir.mkdir(parents=True,exist_ok=True)
    for name,script in launchers:
        path=bin_dir/name
        if path.exists() and not args.upgrade:raise SystemExit('Launcher already exists: '+str(path)+'; use --upgrade after checking it.')
        if path.is_symlink():raise SystemExit('Launcher is a symlink; refusing replacement: '+str(path))
        path.write_text('#!/bin/sh\nexec '+shlex.join([sys.executable,str(target/script)])+' "$@"\n',encoding='utf-8');path.chmod(0o700)
    if sys.platform=='darwin':
        bundle=home/'Applications'/'Dagami Agent Proxy.app'
        if bundle.exists():print('Existing app retained; run '+str(bin_dir/'dagami-agent-proxy'))
        else:
            (bundle/'Contents'/'MacOS').mkdir(parents=True)
            (bundle/'Contents'/'Resources').mkdir()
            shutil.copyfile(target/'devproxy_pkg'/'assets'/'dagami-agent-proxy.icns',bundle/'Contents'/'Resources'/'dagami-agent-proxy.icns')
            (bundle/'Contents'/'Info.plist').write_bytes(plistlib.dumps({'CFBundleName':'Dagami Agent Proxy','CFBundleIdentifier':'ru.dagami.agentproxy','CFBundleExecutable':'devproxy','CFBundlePackageType':'APPL','CFBundleVersion':'1','CFBundleIconFile':'dagami-agent-proxy.icns'}))
            script=bundle/'Contents'/'MacOS'/'devproxy';script.write_text('#!/bin/sh\nexec '+shlex.quote(str(bin_dir/'dagami-agent-proxy'))+'\n');script.chmod(0o700)
    else:
        from devproxy_pkg.portable_shortcuts import desktop_quote
        applications=base/'applications';applications.mkdir(parents=True,exist_ok=True)
        desktop=applications/'dagami-agent-proxy.desktop'
        if not desktop.exists() or args.upgrade:desktop.write_text('[Desktop Entry]\nType=Application\nName=Dagami Agent Proxy\nExec='+desktop_quote(str(bin_dir/'dagami-agent-proxy'))+'\nIcon='+str(target/'devproxy_pkg'/'assets'/'icon-256.png')+'\nTerminal=false\nCategories=Development;Network;\n',encoding='utf-8')
    print('Installed: '+str(target));print('CLI: '+str(bin_dir/'dagami-proxy'));print('GUI: '+str(bin_dir/'dagami-agent-proxy'))

if __name__=='__main__':main()
