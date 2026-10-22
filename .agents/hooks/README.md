# Optional hooks

The existing `.githooks/pre-commit` runs `tools/check_source_hygiene.py`. Enabling it
is an explicit local choice (`git config core.hooksPath .githooks`). `hooks.json`
contains no automatic commands. Keep optional integrations here without secrets.
