# Changelog

## 0.4.0 beta — 2026-10-31

- Use a shared GUI/CLI binary restore path; report partial rollback explicitly
  and distinguish restored ownership records from active patches.
- Stop conflicting processes only by verified executable paths; wait for exit
  before patch/launch. Remove leaking basename-only process enumeration.
- Make unknown applications launcher/environment-only, honor actual console PE
  type, explain capabilities on add, avoid duplicate EXE entries and reject
  removing active/patched custom entries. Check custom-list save failures.
- Bound bridge work to 128 concurrent connections, return 503 under saturation,
  serialize expensive binary status checks, remove repeated verification from
  the 300 ms status refresh, and use skip-based binary signature search.
- Document per-application settings, CLI wrapper limits, Google-only scoped DNS,
  remote/daemon boundaries and the manual-add workflow in APPLICATIONS.md.
- Resolve Node entrypoints only from recognized invocation lines, skipping
  quoted paths in echo/comments; retain native CLI argument/stdin handling.

- Offer to force-close the listed active Dagami sessions before switching
  proxy/DNS or saving a changed connection. Wait for managed processes to exit;
  cancellation or stop failure leaves connection settings unchanged.

- Keep proxy failures explicit instead of silently connecting directly when Smart
  DNS is also enabled. Local backend/OAuth loopback routing remains available.
- Fix the OAuth route/resolver allowlist mismatch for `accounts.google.com`;
  direct connections use the Windows route without a second Smart DNS lookup.
- Preserve disabled proxy settings on save, honor the proxy flag in diagnostic
  and CLI patch routes, and retain DNS nodes in cooldown as final reserves.
- Preserve configured DNS provider order for AI dependencies. Clarify that an
  unauthenticated userinfo check does not verify Gemini model responses/region.
- Distinguish server-side model/region refusals from local routing failures;
  login success and fixture checks do not prove availability of every model.


- Prioritize active Dagami (`89.22.224.205`) and MagicDNS (`2.56.175.196`) DoH
  seeds, verify candidate nodes in parallel and prune unreachable fallback seeds
  when official `.mobileconfig` downloads fail.
- Require a two-target HTTPS preflight quorum in `SystemDns.ps1`, remove the
  broad `microsoft.com` wildcard from NRPT rules and add the ten Antigravity /
  Google Cloud Companion domains to NRPT and Unix split-DNS presets.
- Support `direct` and Smart DNS-only routes in `NativeCliApps.Run`,
  `diagnose login` and `patch`; support EDNS0 UDP payloads up to 1232 bytes and
  cache `dns-custom.json` by timestamp in `LocalDnsAgent`.
- Improve `ActivationForm` text contrast, add `<dpiAware>true</dpiAware>` to the
  Windows manifest, rotate `devproxy.log` at 2 MB and prune stale staged updates
  and DNS job history.
- Prepare the eight verified regional protobuf metadata signatures in supported
  combined CLI/IDE backends as well as the CLI gate; detect incomplete older
  CLI preparation instead of reporting the IDE ready.
- Normalize legacy UTF-8 BOM settings even when their JSON text is unchanged.

- Refuse Low-integrity startup before opening the bridge or launching children;
  explain how to use a normal user copy and add `--permissions-info` diagnostics.

- Restore the selected Smart DNS route for Cloud Code. A configured upstream no
  longer overrides it for the two core Antigravity API hosts.
- Check TLS/userinfo through the actual Dagami bridge, including direct Windows/VPN
  mode. A failed diagnostic no longer replaces or blocks the launch action.
- Launch using saved connection settings without rewriting the configuration.
- Keep Dagami → MagicDNS as the default chain. Other providers require explicit
  selection; DNS attempt budgets leave time for reserve nodes.
- Disable experimental TLS splitting and update blocking when loading older,
  unversioned settings. Explicit choices saved with the new policy are retained.
- Save settings atomically without changing ACLs/attributes or truncating the
  destination after a replacement failure. Report save failures, keep the active
  connection unchanged and avoid writing a second configuration that is not loaded.
- Restore only managed proxy fields; preserve unrelated update/extension settings.
- Do not adopt managed local proxy settings as an upstream during discovery.
- After a failed optional client preparation, verify rollback and allow ordinary
  proxy launch. A failed rollback still stops launch.
- Remove recursive elevation/deletion fallback for legacy startup tasks.
- CLI connection setup supports `direct` and reports failed persistence.
- Preserve prior fixes for late DNS probe completion, metadata requests, normal
  Windows privileges, optional shortcuts and GUI lifetime after a proxy crash.

## 0.3.6 alpha — 2026-10-28

- Show whether the selected Antigravity backend is actually prepared and make
  ordinary-launch setup the primary action until a shortcut or CLI shim exists.
  Report setup results explicitly and explain automatic background startup.
- Simplify Windows navigation to Applications, DNS and Settings; put diagnostics,
  journal, launch preferences and system DNS controls in the settings pages.
- Add encrypted DNS for managed application routes and an optional Windows
  loopback DNS service. Refresh verified addresses from official Dagami and
  MagicDNS profiles, retain the last working list and use Dagami before MagicDNS.
  The whole-network option requires selected adapters and verified IPv4/IPv6.
- Recognize the local eligibility gate in compatible x64 Antigravity CLI builds,
  preserve the original file with hash-checked rollback and reject unknown shapes.
  Server-side account, region and subscription decisions are not changed.
- Add CLI doctor, error classification, a read-only patch preview and DNS commands.
- Detect the IDE-downloaded backend at `%USERPROFILE%/.gemini/bin/agy.exe` in
  native IDE preparation/CLI discovery and the portable CLI adapter. Keep fixture
  profile roots separate from installed user backends during regression checks.

- Managed Smart DNS uses verified DNS-over-HTTPS nodes; custom resolvers stay
  separate and retry over TCP when UDP fails.
- Validate DNS question/answer ownership and CNAME chains; respect short/zero TTL.
- System DNS/NRPT can store several verified addresses. Unavailable nodes are
  excluded before any changes; old single-server backups remain restorable.
- Replace the obsolete system DNS default with `45.147.31.32`.
- TLS diagnostics identify the host, connection layer and certificate policy/chain
  error codes while retaining validation and omitting certificate identities.
- Login diagnostics expose upstream CONNECT failures such as HTTP 407 instead
  of reporting only the bridge's HTTP 502.
- Add loopback DNS/TLS regression suites and reject native test runs without a
  PASS report, including silent execution failures.

## 0.3.0 alpha — 2026-10-28

- Fixed Codex Desktop detection for Microsoft Store/MSIX installations whose main
  executable is `app/ChatGPT.exe`: query current-user package registration and
  validate package identity/manifest, independent of a running process.
- Added detection of `Antigravity IDE.exe` alongside Manager, separate IDE settings,
  the `extensions/agy/bin` layout and the shared per-user `agy/bin/agy.exe` backend.
- Kept local RPC/CONNECT traffic on loopback in the native and Python bridges;
  preserved CSRF/auth, request bodies and streaming trailers without proxy credentials.
- Rejected a proxy bridge used as `antigravity.serverUrl`; stopped old active
  bridge connections on shutdown and kept partial preparation warnings visible.
- Removed the shortcut toggle, duplicate journal bug-report button and duplicate
  Antigravity action menu. Preparation/rollback now use the application actions.
- Added confirmation before resetting managed settings or restoring a backend.
- Added IDE 2.6 regression fixtures and repaired the static UI-preview check.
- Followed up on previous-version user logs: CONNECT/authentication checks replace
  TCP-only checks, bridge startup falls back to a Windows-assigned port, synthetic
  OAuth diagnostics compare routes, and UI/updater errors retain sanitized reasons.

## 0.2.0 alpha — 2026-10-22

- Native C# sources, manifest and DNS resource moved into `src/`.
- Build/install scripts moved into `tools/`; packaging, tests and guides updated.
- Added project agent guidance and empty hook/MCP configuration templates.
- Added a global bug-report button and journal export with a Save File dialog.
- Admin website/API removed from public access; administration requires an SSH
  tunnel to a loopback-only interface. Public report submission remains available.

- Repaired shortcut discovery/recovery and partial-failure rollback.
- Expanded application policies and compatible Antigravity/agy preparation.
- Preserved interactive CLI console/input through a console-subsystem launcher.
- Added report submission with sanitized logs, optional image and protected queue.
- Added the report service, private authenticated viewer and status management.
- Preserved source cleanup and kept license issuance/private data outside Git.

See `RELEASE_NOTES.md` for installation details and remaining alpha limitations.
