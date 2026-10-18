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
    parser.add_argument('--output-dir', type=Path, default=ROOT / 'dist-portable' / 'windows',
                        help='Destination for the single executable (default: dist-portable/windows)')
    args = parser.parse_args()
    executable_name = 'DagamiAgentProxy.exe'
    destination = args.output_dir.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    unexpected = [path.name for path in destination.glob('*.exe')
                  if path.name.lower() != executable_name.lower()]
    if unexpected:
        raise RuntimeError('Output contains old or unrelated executables; archive them first: ' + ', '.join(sorted(unexpected)))
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
        "System.IO.Compression.dll", "System.Xml.dll",
    ]
    command = [str(compiler), "/nologo", '/target:winexe', "/platform:anycpu", "/optimize+"]
    command.extend(f"/reference:{name}" for name in references)
    sources = [
        "DevProxyApp.cs", "NativeUi.cs", "FluentUi.cs", "AntigravityIntegration.cs",
        "AntigravityUi.cs", "AntigravityLogin.cs", "NativeShortcuts.cs",
        "NativeLaunchBroker.cs", "NativeSessions.cs", "SystemDnsManager.cs", "NativeCli.cs", "Brand.cs",
        "LicenseManager.cs", "AppProxyPolicy.cs", "NativeCliApps.cs", "NativeUpdates.cs", "UpdateInstaller.cs", "UpdateUi.cs", "NativeStartup.cs"
    ]
    command.extend(sources)
    command.append("/win32manifest:app.manifest")
    command.append(r"/win32icon:devproxy_pkg\assets\dagami-agent-proxy.ico")
    command.append(r"/resource:devproxy_pkg\assets\dagami-agent-proxy.ico,Dagami.AgentProxy.ico")
    command.append(r"/resource:devproxy_pkg\assets\icon-64.png,Dagami.AgentProxy.png")
    command.append(r"/resource:SystemDns.ps1,DevProxy.SystemDns.ps1")
    # Keep the assembly name stable while replacing the previous executable
    # only after compilation succeeds.
    with tempfile.TemporaryDirectory(prefix=".devproxy-build-") as build:
        output = Path(build) / executable_name
        subprocess.run([command[0], f"/out:{output}", *command[1:]], cwd=ROOT, check=True)
        ready = destination / f".devproxy-ready-{uuid.uuid4().hex}.exe"
        try:
            shutil.copyfile(output, ready)
            os.replace(ready, destination / executable_name)
        finally:
            ready.unlink(missing_ok=True)
    print('Built ' + executable_name)


if __name__ == "__main__":
    main()
