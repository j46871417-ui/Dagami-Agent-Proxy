# Changelog

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
