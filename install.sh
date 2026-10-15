#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="j46871417-ui/devproxy"
ARCHIVE_URL="https://github.com/${REPOSITORY}/archive/refs/heads/main.tar.gz"

if ! command -v python3 >/dev/null 2>&1; then
    echo "[-] Python 3 is required." >&2
    exit 1
fi

if [ "$(id -u)" -eq 0 ]; then
    BIN_DIR="/usr/local/bin"
    APP_DIR="/usr/local/lib/devproxy"
else
    BIN_DIR="${HOME}/.local/bin"
    APP_DIR="${HOME}/.local/lib/devproxy"
fi

mkdir -p "$BIN_DIR" "$(dirname "$APP_DIR")"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT INT TERM
ARCHIVE="$TEMP_DIR/devproxy.tar.gz"

if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$ARCHIVE_URL" -o "$ARCHIVE"
elif command -v wget >/dev/null 2>&1; then
    wget -qO "$ARCHIVE" "$ARCHIVE_URL"
else
    echo "[-] curl or wget is required." >&2
    exit 1
fi

tar -xzf "$ARCHIVE" -C "$TEMP_DIR"
SOURCE_DIR="$(find "$TEMP_DIR" -mindepth 1 -maxdepth 1 -type d -name 'devproxy-*' -print -quit)"
if [ -z "$SOURCE_DIR" ] || [ ! -f "$SOURCE_DIR/devproxy.py" ] || [ ! -d "$SOURCE_DIR/devproxy_pkg" ]; then
    echo "[-] Downloaded archive does not contain a complete DevProxy application." >&2
    exit 1
fi

STAGE_DIR="${APP_DIR}.new.$$"
rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR"
cp "$SOURCE_DIR/devproxy.py" "$STAGE_DIR/devproxy.py"
cp -R "$SOURCE_DIR/devproxy_pkg" "$STAGE_DIR/devproxy_pkg"
cp "$SOURCE_DIR/devproxy" "$STAGE_DIR/devproxy"
chmod +x "$STAGE_DIR/devproxy"

if [ -d "$APP_DIR" ]; then
    mv "$APP_DIR" "${APP_DIR}.old.$$"
fi
mv "$STAGE_DIR" "$APP_DIR"
rm -rf "${APP_DIR}.old.$$"
ln -sfn "$APP_DIR/devproxy" "$BIN_DIR/devproxy"

echo "[+] DevProxy installed: $BIN_DIR/devproxy"
if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
    echo "[!] Add $BIN_DIR to PATH."
fi
