"""Build Dagami packages for the host OS. No publishing or remote operations."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import zipfile

ROOT=Path(__file__).resolve().parent
ASSETS=ROOT/'devproxy_pkg'/'assets'
GUI='Dagami Agent Proxy'
CLI='dagami-portable-cli' if sys.platform=='win32' else 'dagami-proxy'

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
    parser=argparse.ArgumentParser();parser.add_argument('--source-only',action='store_true');args=parser.parse_args()
    output=ROOT/'dist-portable';output.mkdir(exist_ok=True)
    source=output/'dagami-agent-proxy-source.zip'
    names=['devproxy.py','devproxy_gui.py','pyproject.toml','PORTABLE.md','README.md','MATRIX.md','WINDOWS_SHORTCUTS_AND_DNS.md','ANTIGRAVITY_INTEGRATION.md','LICENSE','build_portable.py','build_native_app.py','install_portable.py','make_brand_assets.py','Brand.cs','SystemDns.ps1']
    names += [p.name for p in ROOT.glob('*.cs') if p.name not in names]
    with zipfile.ZipFile(source,'w',zipfile.ZIP_DEFLATED) as archive:
        for name in names:
            if (ROOT/name).exists():archive.write(ROOT/name,name)
        for folder in ('devproxy_pkg','tests','.github/workflows'):
            for path in sorted((ROOT/folder).rglob('*')):
                if path.is_file() and '__pycache__' not in path.parts and path.suffix!='.pyc':archive.write(path,path.relative_to(ROOT))
    if args.source_only:return
    host={'win32':'windows','darwin':'macos'}.get(sys.platform,'linux')
    arch=platform.machine().lower();arch={'amd64':'x64','x86_64':'x64','aarch64':'arm64'}.get(arch,arch)
    build=output/('dagami-'+host+'-'+arch);build.mkdir(exist_ok=True)
    tk_args=tcl_data(output)
    for name,script in [(CLI,'devproxy.py'),(GUI,'devproxy_gui.py')]:
        command=[sys.executable,'-m','PyInstaller','--noconfirm','--clean','--onedir','--name',name,'--distpath',str(build),'--workpath',str(output/'work'/name),'--specpath',str(output/'specs'),'--add-data',str(ASSETS)+':devproxy_pkg/assets']
        if name==CLI:command+=['--exclude-module','tkinter']
        else:
            command+=tk_args
            if sys.platform in ('win32','darwin'):command.append('--windowed')
            if sys.platform=='darwin':command+=['--osx-bundle-identifier','ru.dagami.agentproxy','--icon',str(ASSETS/'dagami-agent-proxy.icns')]
        if sys.platform=='win32':command+=['--icon',str(ASSETS/'dagami-agent-proxy.ico')]
        command.append(str(ROOT/script));subprocess.run(command,cwd=ROOT,check=True)
    suffix='.exe' if host=='windows' else ''
    cli=build/CLI/(CLI+suffix)
    gui=build/GUI/(GUI+suffix) if host!='macos' else build/(GUI+'.app')/'Contents'/'MacOS'/GUI
    subprocess.run([str(cli),'--help'],check=True,timeout=30)
    subprocess.run([str(gui),'--smoke-gui'],check=True,timeout=30)
    shutil.copyfile(ROOT/'PORTABLE.md',build/'PORTABLE.md')
    shutil.copyfile(ROOT/'LICENSE',build/'LICENSE')
    archive=Path(shutil.make_archive(str(output/f'dagami-agent-proxy-{host}-{arch}'),'zip',build))
    manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (source,archive)}
    (output/f'SHA256-{host}-{arch}.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(archive)

if __name__=='__main__':main()
