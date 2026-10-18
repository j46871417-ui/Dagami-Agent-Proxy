"""Install sources and one user launcher. No downloads, elevation or startup."""
import argparse
import hashlib
import json
from pathlib import Path
import os
import plistlib
import shlex
import shutil
import sys
import tempfile
import uuid
from devproxy_pkg.system_dns import atomic
from devproxy_pkg.portable_shortcuts import desktop_quote


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--upgrade',action='store_true');args=parser.parse_args()
    if sys.platform not in ('darwin','linux'):raise SystemExit('Windows: use DagamiAgentProxy.exe.')
    source=Path(__file__).resolve().parent;home=Path.home()
    base=home/'Library'/'Application Support' if sys.platform=='darwin' else Path(os.environ.get('XDG_DATA_HOME',str(home/'.local'/'share')))
    target=base/'DagamiAgentProxy'
    bundle=home/'Applications'/'Dagami Agent Proxy.app'
    launcher=(bundle/'Contents'/'MacOS'/'dagami-agent-proxy') if sys.platform=='darwin' else home/'.local'/'bin'/'dagami-agent-proxy'
    script=('#!/bin/sh\n# Dagami managed source launcher v2\nexec '+shlex.join([sys.executable,str(target/'devproxy_gui.py')])+' "$@"\n').encode()
    files={launcher:script}
    if sys.platform=='darwin':
        files[bundle/'Contents'/'Info.plist']=plistlib.dumps({'CFBundleName':'Dagami Agent Proxy','CFBundleIdentifier':'ru.dagami.agentproxy','CFBundleExecutable':launcher.name,'CFBundlePackageType':'APPL','CFBundleVersion':'1','CFBundleIconFile':'dagami-agent-proxy.icns'})
        files[bundle/'Contents'/'Resources'/'dagami-agent-proxy.icns']=(source/'devproxy_pkg'/'assets'/'dagami-agent-proxy.icns').read_bytes()
    else:
        files[base/'applications'/'dagami-agent-proxy.desktop']=('[Desktop Entry]\nType=Application\nName=Dagami Agent Proxy\nExec='+desktop_quote(str(launcher))+'\nIcon='+str(target/'devproxy_pkg'/'assets'/'icon-256.png')+'\nTerminal=false\nCategories=Development;Network;\n').encode()
    for path in (target,*files):
        if any(part.is_symlink() for part in (path,*path.parents)):raise SystemExit('Symlink retained: '+str(path))
    if target.exists() and not args.upgrade:raise SystemExit('Installation exists. Use --upgrade; old sources will be kept as a backup.')
    journal=target/'source-install.json'
    if journal.is_symlink():raise SystemExit('Symlink retained: '+str(journal))
    recorded=json.loads(journal.read_text()) if journal.is_file() else {}
    if sys.platform=='darwin' and bundle.exists() and not recorded:
        raise SystemExit('Existing app retained. Choose a clean source install location or use the standalone build.')
    for path in files:
        if path.exists() and (not args.upgrade or recorded.get(str(path))!=hashlib.sha256(path.read_bytes()).hexdigest()):
            raise SystemExit('Existing or externally changed file retained: '+str(path))
    originals={path:path.read_bytes() if path.exists() else None for path in files}
    base.mkdir(parents=True,exist_ok=True);backup=None
    with tempfile.TemporaryDirectory(prefix='.dagami-install-',dir=base) as temp:
        stage=Path(temp)/'app';stage.mkdir()
        for name in ('devproxy.py','devproxy_gui.py'):shutil.copyfile(source/name,stage/name)
        shutil.copytree(source/'devproxy_pkg',stage/'devproxy_pkg',ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
        (stage/'source-install.json').write_text(json.dumps({str(path):hashlib.sha256(data).hexdigest() for path,data in files.items()},indent=2),encoding='utf-8')
        if target.exists():
            backup=base/('DagamiAgentProxy.backup-'+uuid.uuid4().hex);target.rename(backup)
        stage.rename(target)
        try:
            for path,data in files.items():atomic(path,data,0o700 if path==launcher else 0o600)
        except Exception:
            for path,data in originals.items():
                if data is None:
                    if path.exists():path.unlink()
                else:atomic(path,data,0o700 if path==launcher else 0o600)
            failed=base/('DagamiAgentProxy.failed-'+uuid.uuid4().hex);target.rename(failed)
            if backup:backup.rename(target)
            raise
    print('Installed: '+str(target));print('GUI and CLI: '+str(launcher))
    return 0


if __name__=='__main__':raise SystemExit(main())
