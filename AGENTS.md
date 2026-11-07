# Repository guidance

Dagami Agent Proxy is a Windows native C# application with portable Python sources
and a separate bug-report service. The new desktop packages and build pipeline use Go and Rust; older Python sources are retained as a migration reference. User instructions and explicit publication
authorization take precedence; do not ask again for an already approved action.

## Layout

- `src/`: native application C#, `app.manifest`, embedded `SystemDns.ps1`.
- `devproxy_pkg/`: portable Python application; `devproxy_gui.py` is its entry point.
- `tools/`: build/install scripts and source-policy checks.
- `tests/native/`: native fixture checks; `tests/test_*.py`: Python tests.
- `bug_reports/`: public service/frontend sources. Production admin access is SSH-only.
- `.agents/`: project guidance and configuration templates, without credentials.

## Validation

Run from the repository root:

```sh
rustup target add wasm32-unknown-unknown
go run ./cmd/dagami-build audit
go run ./cmd/dagami-build -test -source
```

On Windows add `-windows-native -native-checks`. The desktop source archive and
binary packages exclude the legacy Python tree. The legacy tests remain useful
when changing legacy behavior. The bug-report service has its own requirements.

Use fixtures and loopback traffic. Do not run `LicenseChecks` against the real user
activation or change live DNS, shortcuts, accounts or installed application binaries
for verification. Inspect additional test suites before enabling them.

## Changes and source policy

Keep builds, logs, reports, databases, cookies, keys and server access details out
of Git. Keep the native source allowlist and source ZIP allowlist current when
moving files. Update `CHANGELOG.md` and relevant guides for user-visible changes.
Keep rollback behavior explicit: managed proxy fields overwrite manual changes;
unrelated settings must be preserved. Unknown binary signatures must not be patched.

Do not make public admin routes or expose ports 3091/3093 on external interfaces.
Use SSH tunnelling for administration. Website changes need a configuration/data
backup, targeted checks and rollback; avoid restarting unrelated services.

The JSON files in `.agents/` are empty templates. They do not install hooks or MCP
servers automatically. Never put real tokens into tracked configuration. Delegate
only when the user or applicable instructions explicitly request parallel agents.
