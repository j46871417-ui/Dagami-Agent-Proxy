# Changelog

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
