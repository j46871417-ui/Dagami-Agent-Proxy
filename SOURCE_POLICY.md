# Source files in Git

Keep code, tests, build recipes and required assets in Git. Put ready-made
packages in Releases. Keep keys, cookies, sessions, databases, logs, build
output, private notes and backup copies out of Git.

Before a commit, run `python tools/check_source_hygiene.py`. To run the
same check on every local commit, use `git config core.hooksPath .githooks`.
The check reads the Git index, so a changed working copy cannot hide a
staged secret. CI also runs Gitleaks on all fetched Git history. Neither
check prints secret values. Duplicate install assets must be listed in
`tools/source-policy.json`; new unexplained copies fail the check.

`.gitignore` does not untrack existing files. Use `git rm --cached` after
review; keep local files if needed. Never force-add a live credential.
Use placeholder values in `.env.example` and secrets in the CI secret store.

If a real key leaks, revoke it. History cleanup cannot revoke access or
remove other people's clones and GitHub's cached views. After a history
rewrite, clone again rather than merging the old history back.
