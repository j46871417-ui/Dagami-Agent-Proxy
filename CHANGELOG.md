# Changelog

## Unreleased — 2026-10-28

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
