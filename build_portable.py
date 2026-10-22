"""Build Dagami packages for the host OS. No publishing or remote operations."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import uuid
import zipfile
from build_native_app import SOURCES as NATIVE_SOURCES

ROOT=Path(__file__).resolve().parent
ASSETS=ROOT/'devproxy_pkg'/'assets'
GUI='Dagami Agent Proxy'
SOURCE_ASSETS = {
    'dagami-agent-proxy.ico', 'dagami-agent-proxy.icns', 'dagami-agent-proxy.svg',
    'dagami-logo.jpg', 'icon-64.png', 'icon-256.png', 'dns-domains.json',
}
BUG_REPORTS_FILES = {
    'README.md',
    'service/server.py', 'service/maintenance.py', 'service/test_server.py',
    'service/dev_server.py', 'service/hash_password.py', 'service/requirements.txt',
    'service/nginx.conf', 'service/dagami-reports.service',
    'service/dagami-reports-maintenance.service', 'service/dagami-reports-maintenance.timer',
    'public/site-theme.js', 'public/dagami-site.css', 'public/site-preferences.css',
    'public/support/agent-proxy/index.html', 'public/support/agent-proxy/admin/index.html',
    'public/support/agent-proxy/reports.css', 'public/support/agent-proxy/reports.js',
    'public/support/agent-proxy/admin.js',
}

def source_member(path):
    relative=path.relative_to(ROOT)
    if path.is_symlink() or not path.resolve().is_relative_to(ROOT.resolve()):
        raise RuntimeError('Source archive refuses links: '+relative.as_posix())
    if '__pycache__' in relative.parts or any(part.startswith('.') for part in relative.parts if part!='.github'):return False
    if relative.parts[0]=='devproxy_pkg':
        return path.suffix=='.py' or (relative.parent.as_posix()=='devproxy_pkg/assets' and
            path.name in SOURCE_ASSETS)
    if relative.parts[0]=='tests':return path.suffix in ('.py','.cs','.ps1')
    if relative.parts[0]=='tools':return path.name in ('check_source_hygiene.py','source-policy.json','test_source_hygiene.py')
    if relative.parts[0]=='bug_reports':return Path(*relative.parts[1:]).as_posix() in BUG_REPORTS_FILES
    return relative.parts[:2]==('.github','workflows') and path.suffix in ('.yml','.yaml')

def add_source(archive,path):
    from tools.check_source_hygiene import PATTERNS
    data=path.read_bytes()
    for label,pattern in PATTERNS.items():
        if re.search(pattern,data):raise RuntimeError(f'Source archive: possible {label} in {path.relative_to(ROOT)}; value redacted')
    info=zipfile.ZipInfo.from_file(path,path.relative_to(ROOT))
    archive.writestr(info,data,compress_type=zipfile.ZIP_DEFLATED)

def tcl_data(output):
    """Tk 9 embeds scripts in zipfs; materialize them for PyInstaller's hook."""
    import tkinter
    interp=tkinter.Tcl()
    if not str(interp.call('info','library')).startswith('//zipfs:'):return []
    root=tkinter.Tk();root.withdraw()
    try:
        files=[]
        for source,name in ((root.tk.call('info','library'),'_tcl_data'),(root.tk.eval('set tk_library'),'_tk_data')):
            dest=output/'tk9'/name
            if not dest.exists():
                dest.parent.mkdir(parents=True,exist_ok=True)
                root.tk.call('file','copy',source,str(dest))
            files.extend(['--add-data',str(dest)+':'+name])
        return files
    finally:root.destroy()

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--source-only',action='store_true');parser.add_argument('--output-dir',type=Path,default=ROOT/'dist-portable');args=parser.parse_args()
    output=args.output_dir.resolve();output.mkdir(parents=True,exist_ok=True)
    source=output/'dagami-agent-proxy-source.zip'
    names=['.gitignore','.gitattributes','.gitleaks.toml','.githooks/pre-commit',
           'devproxy_gui.py','pyproject.toml','PORTABLE.md','BUILD_LINUX_MACOS.md',
           'README.md','MATRIX.md','APPS_RESEARCH.md','BUG_REPORTS.md','RELEASE_NOTES.md',
           'RECHECK_20261022.md','SYSTEM_DNS.md','SOURCE_POLICY.md','STARTUP.md','LICENSE',
           'build_portable.py','build_native_app.py','install_portable.py','install.sh',
           'SystemDns.ps1','app.manifest',*NATIVE_SOURCES]
    staged=output/('source-'+uuid.uuid4().hex+'.tmp')
    try:
        with zipfile.ZipFile(staged,'w',zipfile.ZIP_DEFLATED) as archive:
            for name in names:
                if (ROOT/name).is_symlink():raise RuntimeError('Source archive refuses links: '+name)
                if (ROOT/name).exists():add_source(archive,ROOT/name)
            for folder in ('devproxy_pkg','tests','tools','.github/workflows','bug_reports'):
                for path in sorted((ROOT/folder).rglob('*')):
                    if path.is_file() and source_member(path):add_source(archive,path)
        staged.replace(source)
    finally:
        if staged.exists():staged.unlink()
    if args.source_only:
        (output/'SHA256-source.json').write_text(json.dumps({source.name:hashlib.sha256(source.read_bytes()).hexdigest()},indent=2)+'\n',encoding='utf-8')
        print(source);return
    host={'win32':'windows','darwin':'macos'}.get(sys.platform,'linux')
    arch=platform.machine().lower();arch={'amd64':'x64','x86_64':'x64','aarch64':'arm64'}.get(arch,arch)
    if host=='windows':
        # Windows has one native executable for GUI, CLI and DNS maintenance.
        native=output/'windows'
        subprocess.run([sys.executable,str(ROOT/'build_native_app.py'),'--output-dir',str(native)],check=True)
        exe=native/'DagamiAgentProxy.exe'
        hashes={exe.name:hashlib.sha256(exe.read_bytes()).hexdigest()}
        (native/'SHA256-native.json').write_text(json.dumps(hashes,indent=2)+'\n',encoding='utf-8')
        archive=output/f'dagami-agent-proxy-{host}-{arch}.zip'
        with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as package:
            for path in (exe,native/'SHA256-native.json',ROOT/'README.md',ROOT/'RELEASE_NOTES.md',ROOT/'BUG_REPORTS.md',ROOT/'MATRIX.md',ROOT/'SYSTEM_DNS.md',ROOT/'SOURCE_POLICY.md',ROOT/'STARTUP.md',ROOT/'PORTABLE.md',ROOT/'LICENSE'):
                package.write(path,path.name)
        manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (source,archive)}
        (output/f'SHA256-{host}-{arch}.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
        print(archive)
        return
    if any(importlib.util.find_spec(module) is None for module in ('pystray','PIL')):
        raise SystemExit('Install desktop build dependencies: python3 -m pip install ".[build,desktop]"')
    build=output/('dagami-'+host+'-'+arch);build.mkdir(exist_ok=True)
    # GUI, CLI, session and elevation enter the same binary. A macOS .app
    # keeps runtime libraries inside the bundle; no second CLI is shipped.
    name=GUI if host=='macos' else 'dagami-agent-proxy'
    command=[sys.executable,'-m','PyInstaller','--noconfirm','--clean',
             '--onedir' if host=='macos' else '--onefile','--name',name,
             '--distpath',str(build),'--workpath',str(output/'work'/name),
             '--specpath',str(output/'specs'),'--add-data',str(ASSETS)+':devproxy_pkg/assets',
             *tcl_data(output)]
    if host=='macos':
        command+=['--windowed','--osx-bundle-identifier','ru.dagami.agentproxy',
                  '--icon',str(ASSETS/'dagami-agent-proxy.icns')]
    command.append(str(ROOT/'devproxy_gui.py'));subprocess.run(command,cwd=ROOT,check=True)
    gui=build/name if host=='linux' else build/(GUI+'.app')/'Contents'/'MacOS'/GUI
    subprocess.run([str(gui),'--help'],check=True,timeout=30)
    subprocess.run([str(gui),'--smoke-gui'],check=True,timeout=30)
    archive=output/f'dagami-agent-proxy-{host}-{arch}.zip'
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as package:
        artifact=build/name if host=='linux' else build/(GUI+'.app')
        paths=[artifact] if artifact.is_file() else sorted(p for p in artifact.rglob('*') if p.is_file() or p.is_symlink())
        for path in paths:
            relative=path.relative_to(build).as_posix()
            if path.is_symlink():
                import os
                info=zipfile.ZipInfo(relative);info.create_system=3
                info.external_attr=(0o120777<<16)
                package.writestr(info,os.readlink(path))
            else:package.write(path,relative)
        for name in ('PORTABLE.md','LICENSE'):package.write(ROOT/name,name)
    manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (source,archive)}
    (output/f'SHA256-{host}-{arch}.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(archive)

if __name__=='__main__':main()
