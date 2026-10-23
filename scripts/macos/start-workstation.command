#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — quick start (no install, no Electron)
#
#  Double-click in Finder to run the scanner locally and open the UI in your
#  browser. Close the Terminal window (or press Ctrl+C) to stop the scanner.
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WEB_DIR="$ROOT/WebWorkstation"
PORT="${CODEHAVEN_PORT:-2525}"

printf '\033[1mCodeHaven Workstation\033[0m\n\n'

# Node lookup (Finder launches with a minimal PATH)
NODE_BIN="$(command -v node || true)"
for candidate in /opt/homebrew/bin/node /usr/local/bin/node "$HOME/.nvm/versions/node"/*/bin/node; do
  [[ -n "$NODE_BIN" ]] && break
  [[ -x "$candidate" ]] && NODE_BIN="$candidate"
done

if [[ -z "$NODE_BIN" ]]; then
  printf '\033[31mNode.js is not installed.\033[0m\n\nInstall it with:\n  brew install node\nor download it from https://nodejs.org/en/download\n\n'
  read -r -p "Open the download page? [y/N] " a
  [[ "${a:-}" =~ ^[Yy]$ ]] && open "https://nodejs.org/en/download"
  exit 1
fi

printf 'Node.js %s\n' "$("$NODE_BIN" -v)"
cd "$WEB_DIR" || exit 1

if [[ ! -d node_modules/bip39 ]]; then
  printf 'Installing dependencies (one time)…\n'
  npm install --no-audit --no-fund --loglevel=error || { printf '\033[31mnpm install failed\033[0m\n'; exit 1; }
fi

if /usr/bin/curl -fsS -m 1 "http://127.0.0.1:$PORT/api/brain-status" >/dev/null 2>&1; then
  printf 'Scanner already running — opening the UI.\n'
  open "http://127.0.0.1:$PORT/"
  exit 0
fi

printf 'Starting scanner on http://127.0.0.1:%s\n' "$PORT"
printf 'Press Ctrl+C to stop.\n\n'

HOST=127.0.0.1 PORT="$PORT" \
  CODEHAVEN_DATA_DIR="$HOME/Library/Application Support/CodeHaven Workstation" \
  "$NODE_BIN" "$WEB_DIR/server.js" &
SERVER_PID=$!

for _ in $(seq 1 160); do
  if /usr/bin/curl -fsS -m 1 "http://127.0.0.1:$PORT/api/brain-status" >/dev/null 2>&1; then
    open "http://127.0.0.1:$PORT/"
    break
  fi
  sleep 0.25
done

trap 'kill -TERM "$SERVER_PID" 2>/dev/null; exit 0' INT TERM
wait "$SERVER_PID"
