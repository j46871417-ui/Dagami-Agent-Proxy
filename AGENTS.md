# Repository guidance

Dagami Agent Proxy uses C# WinForms on Windows and Go with native toolkit
bindings for macOS and Linux. The Go gateway, existing bot adapters and
bug-report service are separate components.

## Layout

- `src/` contains the Windows client, manifest and embedded SystemDns.ps1.
- `cmd/` and `internal/` contain the Go client, gateway and build tool.
- `assets/` contains the icons and logo used by the desktop build and README.
- `tests/native/` and Go test files contain current desktop fixtures.
- `tools/run_native_checks.py` runs the extended Windows fixture suite.
- `deployment/managed/` contains working bot/controller adapters.
- `bug_reports/` contains the service and public form. Admin access is SSH-only.

## Validation

Run from the repository root.

```sh
go run ./cmd/dagami-build audit
go run ./cmd/dagami-build -test -source
```

On Windows add `-windows-native -native-checks`. The desktop build needs no
Python. Some extended fixtures use Python to provide loopback test services.

Use fixtures and loopback traffic. Do not run LicenseChecks against real
activation or change live DNS, shortcuts, accounts or installed binaries for
verification. Inspect additional suites before enabling them.

## Changes and source policy

Keep build output, logs, databases, credentials and server access details out
of Git. Keep native source and source ZIP allowlists current when moving files.
Update CHANGELOG.md and affected guides for user-visible changes.

Preserve caller behavior when removing duplicate code. Keep rollback ownership
explicit: managed proxy fields overwrite manual changes, while unrelated
settings must be preserved. Unknown binaries must not be patched.

Do not expose public admin routes or ports 3091/3093. Use SSH tunnelling for
administration. Website changes require a configuration/data backup, targeted
checks and rollback. Avoid restarting unrelated services.

User instructions and explicit publication authorization take precedence.
Delegate only when the user or applicable instructions explicitly request
parallel agents.
