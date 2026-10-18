#!/usr/bin/env bash
set -euo pipefail

# Install the checked-out sources without downloads, sudo or extra launchers.
TASK_SOURCE_DIR="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if ! command -v python3 >/dev/null 2>&1; then
    echo 'Python 3.10+ with Tk is required.' >&2
    exit 1
fi
if [ "$(id -u)" -eq 0 ]; then
    echo 'Run from your user session. DNS requests admin rights separately.' >&2
    exit 1
fi
exec python3 "$TASK_SOURCE_DIR/install_portable.py" "$@"
