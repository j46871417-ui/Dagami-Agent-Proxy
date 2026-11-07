# Source files in Git

Keep code, tests, build recipes and required assets in Git. Store binaries in
GitHub Releases. Credentials, databases, logs, caches and build output belong
outside tracked source.

Run the index audit before committing.

```sh
go run ./cmd/dagami-build audit
```

For local commit checks, use `git config core.hooksPath .githooks`. The hook
and GitHub Actions use the same Go audit. It checks staged blobs for ignored
files, duplicate content, missing source and recognized secret formats.
GitHub Actions also runs Gitleaks against fetched history. Values are redacted.

The desktop source ZIP uses the native C# allowlist, Go source and explicitly
listed guides and assets. Update `cmd/dagami-build/main.go` when moving source
or resources. Server source lives in `deployment/managed` and `bug_reports`.

The previous Python desktop implementation and packaging scripts are retained
in Git history through `v0.5.0-beta`. Python still supports the working bot,
bug-report service and Windows test runner.

License issuers and private keys stay outside this repository. Legacy offline
keys remain compatible with the client. Managed proxy access requires a
registered device. Removing files from the current tree does not revoke leaked
credentials or remove copies from old tags and releases.
