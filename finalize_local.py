from pathlib import Path
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
root=Path(__file__).resolve().parent
p=root/'README.md';s=p.read_text(encoding='utf-8');s=s.replace('`devproxy.exe`','`DagamiAgentProxy.exe`').replace('\ndevproxy.exe\n','\nDagamiAgentProxy.exe\n')
s=re.sub(r'- Контрольная сумма `DagamiAgentProxy.exe`:\n  - \*\*SHA-256\*\*: `[^`]+`','- Контрольные суммы новых Windows GUI/CLI: `SHA256-native.json`; переносимых архивов: `dist-portable/SHA256-ОС-АРХИТЕКТУРА.json`.',s)
s=s.replace('Сборка `DagamiAgentProxy.exe`','Основная Windows-сборка `DagamiAgentProxy.exe`',1)
p.write_text(s,encoding='utf-8')
manifest={name:hashlib.sha256((root/name).read_bytes()).hexdigest() for name in ('DagamiAgentProxy.exe','dagami-proxy.exe')}
(root/'SHA256-native.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
repo = Path(r"C:\Users\gabov\OneDrive\Документы\deeptest\devproxy")
if repo != root:
    for name in ('MATRIX.md','WINDOWS_SHORTCUTS_AND_DNS.md','ANTIGRAVITY_INTEGRATION.md'):
        if (repo/name).is_file():shutil.copyfile(repo/name,root/name)
# Reuse the existing window-layout regression checks with a bridge-free form.
with tempfile.TemporaryDirectory(prefix='.layout-check-') as work:
    work=Path(work);source=work/'LayoutChecks.cs'
    text=(root/'tests/native/CompactChecks.cs').read_text(encoding='utf-8-sig').replace('new MainForm()','new MainForm(null,false,true)')
    source.write_text(text,encoding='utf-8')
    shutil.copyfile(root/'DagamiAgentProxy.exe',work/'DagamiAgentProxy.exe')
    compiler=Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
    subprocess.run([str(compiler),'/nologo','/target:exe','/out:'+str(work/'LayoutChecks.exe'),'/r:'+str(work/'DagamiAgentProxy.exe'),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',str(source)],check=True)
    subprocess.run([str(work/'LayoutChecks.exe')],cwd=work,check=True,timeout=45)
    shutil.copyfile(work/'compact-default.png',root/'branding-compact-verified.png')
print('Native manifest and layout verification complete')
