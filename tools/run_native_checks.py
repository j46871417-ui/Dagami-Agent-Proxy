"""Selected Windows fixture checks; never runs activation or live DNS mutation tests."""
import argparse
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]
SUITES=['BugReportChecks','ConsoleLaunchChecks','SessionStopChecks','HardReviewChecks','CliChecks','ApplicationSupportChecks',
        'ReviewRegressionChecks','ConnectionRecoveryChecks','CodexDiscoveryChecks','PatchChecks','ShortcutChecks','ShortcutRecoveryChecks',
        'NativeLoginChecks','GoogleGeminiChecks','AntigravityPatchChecks','Ide26Checks','ProxyFailureChecks','DnsTransportChecks','DnsProviderChecks','DnsAgentChecks','TlsDiagnosticsChecks','SystemDnsUiChecks','UserInfoChecks','LocalUiChecks','StartupChecks']

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--assembly',type=Path,required=True)
    parser.add_argument('--output-dir',type=Path,required=True)
    parser.add_argument('--suite',nargs='+',choices=SUITES)
    args=parser.parse_args()
    assembly=args.assembly.resolve();output=args.output_dir.resolve()
    if not assembly.is_file():parser.error('Build the application first.')
    if output in (ROOT,ROOT/'src',ROOT/'tools',ROOT/'tests',Path.home().resolve()):parser.error('Choose a dedicated output directory.')
    output.mkdir(parents=True,exist_ok=True)
    allowed={'DagamiAgentProxy.exe','ConsoleFixture.exe',*[name+'.exe' for name in SUITES]}
    if any(path.name not in allowed for path in output.glob('*.exe')):parser.error('Output contains unrelated executables.')
    target=output/'DagamiAgentProxy.exe'
    if target!=assembly:shutil.copyfile(assembly,target)
    compiler=Path(os.environ.get('WINDIR',r'C:\Windows'))/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if not compiler.is_file():parser.error('Windows .NET Framework compiler not found.')
    env=dict(os.environ,TEMP=str(output),TMP=str(output),DAGAMI_TEST_PYTHON=sys.executable)
    subprocess.run([str(compiler),'/nologo','/target:winexe','/out:'+str(output/'ConsoleFixture.exe'),str(ROOT/'tests/native/ConsoleFixture.cs')],check=True)
    for name in args.suite or SUITES:
        executable=output/(name+'.exe')
        command=[str(compiler),'/nologo','/target:exe','/out:'+str(executable),'/r:'+str(target)]
        command+=['/r:'+reference for reference in ('System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Security.dll','System.Xml.dll','System.ServiceProcess.dll')]
        subprocess.run([*command,str(ROOT/'tests/native'/(name+'.cs'))],check=True)
        try:
            result=subprocess.run([str(executable)],cwd=output,env=env,capture_output=True,text=True,encoding="utf-8",errors="replace",timeout=90)
        except subprocess.TimeoutExpired as error:
            captured=(error.stdout or b'')+(error.stderr or b'')
            if isinstance(captured,bytes):captured=captured.decode('utf-8',errors='replace')
            (output/(name+'.log')).write_text(captured+'\nFixture timed out after 90 seconds.\n',encoding='utf-8')
            print(name+': '+captured,flush=True)
            raise SystemExit(name+' timed out; captured output saved.') from None
        (output/(name+'.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
        print(name+': '+result.stdout.strip(),flush=True)
        if result.returncode:
            print(result.stderr,flush=True)
            raise SystemExit(result.returncode)
        if not re.search(r'(?m)^PASS(?:[:\s]|$)',result.stdout):
            raise SystemExit(name+' returned no PASS report; execution was not verified.')

if __name__=='__main__':main()
