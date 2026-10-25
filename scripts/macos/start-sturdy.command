#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — STURDY scanner runner
#
#  Double-click in Finder (or run from Terminal). This version:
#    • keeps the Mac awake while it runs (caffeinate)
#    • survives the Terminal window closing (the engine keeps running!)
#    • restarts the engine automatically within 5 seconds if it ever dies
#    • writes everything to /tmp/scanrun.log so we can see WHY it stopped
#
#  Stop everything: Ctrl+C in this window, or:  kill \$(lsof -ti:2525)
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

already_running() {
  /usr/bin/curl -fsS -m 1 "http://127.0.0.1:$PORT/api/brain-status" >/dev/null 2>&1
}

if already_running; then
  printf 'Scanner already running at http://127.0.0.1:%s\n' "$PORT"
  printf 'The native app (CodeHaven Workstation.app) is the interface - no browser needed.\n'
  exit 0
fi

printf 'Starting scanner on http://127.0.0.1:%s\n' "$PORT"
printf 'You can close this window later — the engine survives it. Ctrl+C stops it now.\n\n'

STOP_REQUESTED=0
trap 'STOP_REQUESTED=1; printf "\nStopped by you. Bye!\n"; exit 0' INT TERM

while true; do
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] starting scanner..." >> "$LOG"
  caffeinate -i env HOST=127.0.0.1 PORT="$PORT" \
    CODEHAVEN_DATA_DIR="$HOME/Library/Application Support/CodeHaven Workstation" \
    "$NODE_BIN" "$WEB_DIR/server.js" 2>&1 | tee -a "$LOG"
  EXIT_CODE=${PIPESTATUS[0]}
  [[ "$STOP_REQUESTED" == "1" ]] && exit 0
  if already_running; then
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] another instance is serving port $PORT — this launcher exits." | tee -a "$LOG"
    exit 0
  fi
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] scanner exited (code ${EXIT_CODE}) — restarting in 5s..." | tee -a "$LOG"
  sleep 5
done
