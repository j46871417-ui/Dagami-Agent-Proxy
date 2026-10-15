"""Build the native Windows Forms application with the installed .NET compiler."""

import os
import subprocess
import tempfile
import shutil
import uuid
import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', action='store_true', help='Build the native console companion')
    args = parser.parse_args()
    executable_name = 'dagami-proxy.exe' if args.cli else 'DagamiAgentProxy.exe'
    windows = Path(os.environ.get("WINDIR", r"C:\Windows"))
    candidates = [
        windows / "Microsoft.NET/Framework64/v4.0.30319/csc.exe",
        windows / "Microsoft.NET/Framework/v4.0.30319/csc.exe",
    ]
    compiler = next((path for path in candidates if path.is_file()), None)
    if compiler is None:
        raise RuntimeError("The .NET Framework C# compiler was not found.")
    references = [
        "System.dll", "System.Core.dll", "System.Drawing.dll",
        "System.Windows.Forms.dll", "System.Security.dll", "System.Web.Extensions.dll",
    ]
    command = [str(compiler), "/nologo", '/target:exe' if args.cli else '/target:winexe', "/platform:anycpu", "/optimize+"]
    command.extend(f"/reference:{name}" for name in references)
    sources = [
        "DevProxyApp.cs", "NativeUi.cs", "FluentUi.cs", "AntigravityIntegration.cs",
        "AntigravityUi.cs", "AntigravityLogin.cs", "NativeShortcuts.cs",
        "NativeLaunchBroker.cs", "NativeSessions.cs", "SystemDnsManager.cs", "NativeCli.cs", "Brand.cs",
        "LicenseManager.cs"
    ]
    command.extend(sources)
    command.append(r"/win32icon:devproxy_pkg\assets\dagami-agent-proxy.ico")
    command.append(r"/resource:devproxy_pkg\assets\dagami-agent-proxy.ico,Dagami.AgentProxy.ico")
    command.append(r"/resource:devproxy_pkg\assets\icon-64.png,Dagami.AgentProxy.png")
    command.append(r"/resource:SystemDns.ps1,DevProxy.SystemDns.ps1")
    # Keep the assembly name stable while replacing the previous executable
    # only after compilation succeeds.
    with tempfile.TemporaryDirectory(prefix=".devproxy-build-") as build:
        output = Path(build) / executable_name
        subprocess.run([command[0], f"/out:{output}", *command[1:]], cwd=ROOT, check=True)
        ready = ROOT / f".devproxy-ready-{uuid.uuid4().hex}.exe"
        try:
            shutil.copyfile(output, ready)
            os.replace(ready, ROOT / executable_name)
        finally:
            ready.unlink(missing_ok=True)
    print('Built ' + executable_name)


if __name__ == "__main__":
    main()
