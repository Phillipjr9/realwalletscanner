#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — STURDY scanner runner
#
#  Double-click in Finder (or run from Terminal). This version:
#    • keeps the Mac awake while it runs (caffeinate)
#    • restarts the engine automatically within 5 seconds if it ever dies
#    • writes everything to /tmp/scanrun.log so we can see WHY it stopped
#
#  Stop it with Ctrl+C (or close this window).
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WEB_DIR="$ROOT/WebWorkstation"
PORT="${CODEHAVEN_PORT:-2525}"
LOG=/tmp/scanrun.log

printf '\033[1mCodeHaven Workstation — sturdy scanner\033[0m\n'
printf 'log file: %s\n\n' "$LOG"

# Node lookup (Finder launches with a minimal PATH)
NODE_BIN="$(command -v node || true)"
for candidate in /opt/homebrew/bin/node /usr/local/bin/node "$HOME/.nvm/versions/node"/*/bin/node; do
  [[ -n "$NODE_BIN" ]] && break
  [[ -x "$candidate" ]] && NODE_BIN="$candidate"
done

if [[ -z "$NODE_BIN" ]]; then
  printf '\033[31mNode.js is not installed.\033[0m\nInstall it with:  brew install node\n'
  exit 1
fi

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

printf 'Starting scanner on http://127.0.0.1:%s  (keep this window open)\n' "$PORT"
printf 'If the engine ever dies it restarts itself in 5 seconds. Ctrl+C to stop.\n\n'

while true; do
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] starting scanner..." >> "$LOG"
  caffeinate -i env HOST=127.0.0.1 PORT="$PORT" \
    CODEHAVEN_DATA_DIR="$HOME/Library/Application Support/CodeHaven Workstation" \
    "$NODE_BIN" "$WEB_DIR/server.js" 2>&1 | tee -a "$LOG"
  EXIT_CODE=${PIPESTATUS[0]}
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] scanner exited (code ${EXIT_CODE}) — restarting in 5s..." | tee -a "$LOG"
  sleep 5
done
