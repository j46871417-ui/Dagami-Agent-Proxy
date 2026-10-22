# Native fixture checks

Application sources and resources are in `src/`; the build script is in `tools/`.
From the repository root on Windows:

```powershell
python tools/build_native_app.py --output-dir dist-portable/windows
python tools/run_native_checks.py --assembly dist-portable/windows/DagamiAgentProxy.exe --output-dir dist-portable/checks
```

The runner compiles fixture executables against the built application and runs an
explicit safe suite list. It uses a dedicated TEMP/TMP directory and preserves
check output there. `LicenseChecks` and live DNS/update-installation tests are not
included. SystemDnsChecks.ps1 resolves its default engine at `../../src/SystemDns.ps1`;
read its fixture/mutation setup before running it. Never use the real activation,
network settings or installed application files as writable test fixtures.
