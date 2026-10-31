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

`DnsTransportChecks` binds only loopback fixtures and checks UDP/TCP framing,
truncation, backup selection, transport cooldown, TTL and malformed/unsafe replies.
`TlsDiagnosticsChecks` creates a disposable certificate/key in fixture TEMP and
uses Python/OpenSSL as the loopback TLS server; no Windows key or trust store is
modified. It verifies rejection and diagnostic detail for an untrusted certificate.
`SystemDnsUiChecks` and `UserInfoChecks` verify multi-server request/status handling
and upstream status propagation without applying real settings or using account tokens.
`DnsProviderChecks` and `DnsAgentChecks` exercise trusted profile parsing, DoH
framing, local DNS forwarding and service transactions with a fake service controller.
`AntigravityPatchChecks` covers recognized CLI branch shapes, unknown builds and
exact rollback. `SystemDnsChecks.ps1` covers encrypted IPv4/IPv6 adapter rollback.
The runner requires an explicit PASS report; a silent exit is not counted as success.
`SessionStopChecks` uses only its own fixture processes to verify route-change
confirmation/cancellation, stop failures, actual Job descendant termination,
tracked launchers outside a Job and preservation of unrelated processes/settings.
`HardReviewChecks` covers unknown JSON isolation, console/GUI distinction,
duplicate executable lookup, full binary unpatch and failed restore reporting,
same-name processes from separate installations, signature-search equivalence,
and bridge saturation/recovery without external traffic.

`Ide26Checks` covers the separate IDE filename/profile, old/new plugin directories,
external agy discovery, safe preparation/restore, local CONNECT and streamed RPC
with CSRF/auth and chunked trailers, upstream credential isolation and bridge shutdown.
`LocalUiChecks` uses preview mode with no polling timer, checks default/minimum
control bounds and saves screenshots; it does not verify every DPI/theme combination.
`ProxyFailureChecks` uses only loopback fixtures for CONNECT 407/403/503, escaped
credentials, port-range fallback and synthetic OAuth route comparison. It never
contacts proxy addresses copied from user logs.
`CodexDiscoveryChecks` validates MSIX identity/manifest and Desktop/CLI separation.
If Codex is installed for the current user, it also verifies package discovery
without launching or patching the installed app; otherwise that integration is skipped.

`ConnectionRecoveryChecks` verifies versioned opt-in migration, rejection of a
managed bridge as an automatically detected upstream, effective-route diagnostics,
failed-save rollback, locked-file preservation and ownership of restored fields.
`DnsTransportChecks` also transmits bytes through both Cloud Code Smart DNS
routes even when an external upstream is configured, and covers proxy-only mode.
Native output decoding is tolerant of Windows console code-page differences.
Routing regression checks also cover OAuth resolution with both route flags,
preservation of disabled proxy credentials across save/load, cooldown reserve
retention, and proxy failure without an attempted direct dial. The latter uses
a synthetic connector that intercepts before DNS/network; local backend routing
is covered separately and remains intentionally available.
The interactive console fixture uses ASCII pipe input and a Unicode working
folder, avoiding corruption of absolute paths by the cmd input code page.

A build launched from a restricted Codex workspace can inherit restricted Windows
rights. Run verification builds/fixtures from a dedicated normal user output
folder when testing Windows profile/COM access; do not change profile ACLs to
compensate. An environment failure is not a PASS.
