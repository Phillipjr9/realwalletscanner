#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — macOS launcher (used by the lightweight .app bundle)
#
#  Starts the scanner on loopback only, waits until it is healthy, then opens
#  the workstation UI in the default browser. Keeps running so the app stays in
#  the Dock; quitting the app stops the scanner.
# =============================================================================
set -uo pipefail

APP_NAME="CodeHaven Workstation"
DEFAULT_PORT="${CODEHAVEN_PORT:-2525}"
LOG_DIR="$HOME/Library/Logs/CodeHaven"
LOG_FILE="$LOG_DIR/workstation.log"
DATA_DIR="${CODEHAVEN_DATA_DIR:-$HOME/Library/Application Support/CodeHaven Workstation}"

notify_error() {
  local msg="$1"
  printf '%s\n' "$msg" >&2
  if command -v osascript >/dev/null 2>&1; then
    osascript -e "display alert \"$APP_NAME\" message \"${msg//\"/\\\"}\" as critical" >/dev/null 2>&1 || true
  fi
}

mkdir -p "$LOG_DIR" "$DATA_DIR" || { notify_error "Could not create $LOG_DIR"; exit 1; }
exec >>"$LOG_FILE" 2>&1
echo ""
echo "──────────────────────────────────────────────────────────────"
echo "$(date '+%Y-%m-%d %H:%M:%S')  launching $APP_NAME"

# ── Locate the repository ───────────────────────────────────────────────────
ROOT="${CODEHAVEN_HOME:-}"
if [[ -z "$ROOT" && -f "${BASH_SOURCE[0]}" ]]; then
  # Running straight from the checkout: scripts/macos/launch-workstation.sh -> repo root
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fi
if [[ -z "$ROOT" || ! -f "$ROOT/WebWorkstation/server.js" ]]; then
  # Fall back to a few conventional checkout locations
  for candidate in "$HOME/realwalletscanner" "$HOME/Projects/realwalletscanner" "$HOME/Developer/realwalletscanner" "/Applications/realwalletscanner"; do
    if [[ -f "$candidate/WebWorkstation/server.js" ]]; then ROOT="$candidate"; break; fi
  done
fi
if [[ -z "$ROOT" || ! -f "$ROOT/WebWorkstation/server.js" ]]; then
  notify_error "Could not find the CodeHaven repository. Set CODEHAVEN_HOME to your checkout path."
  exit 1
fi
WEB_DIR="$ROOT/WebWorkstation"
echo "root: $ROOT"

# ── Locate Node.js (GUI apps get a minimal PATH) ────────────────────────────
NODE_BIN=""
for candidate in \
  "$(command -v node 2>/dev/null || true)" \
  /opt/homebrew/bin/node \
  /usr/local/bin/node \
  /usr/bin/node \
  "$HOME/.nvm/versions/node"/*/bin/node \
  "$HOME/.volta/bin/node"; do
  if [[ -n "$candidate" && -x "$candidate" ]]; then NODE_BIN="$candidate"; break; fi
done
if [[ -z "$NODE_BIN" ]]; then
  notify_error "Node.js 18+ is required. Install it with 'brew install node' or from nodejs.org, then reopen the app."
  exit 1
fi
export PATH="$(dirname "$NODE_BIN"):$PATH"
echo "node: $($NODE_BIN -v)"

# ── Ensure runtime dependencies exist ──────────────────────────────────────
cd "$WEB_DIR" || { notify_error "Missing $WEB_DIR"; exit 1; }
if [[ ! -d node_modules/bip39 ]]; then
  echo "installing dependencies…"
  NPM_BIN="$(command -v npm || dirname "$NODE_BIN")/npm"
  if [[ -x "$NPM_BIN" ]]; then
    "$NPM_BIN" install --no-audit --no-fund --loglevel=error || {
      notify_error "Dependency install failed. Open a terminal, run 'cd \"$WEB_DIR\" && npm install', then reopen."
      exit 1
    }
  fi
fi

# ── Reuse or start the server ──────────────────────────────────────────────
health() {
  /usr/bin/curl -fsS -m 1 "http://127.0.0.1:$1/api/brain-status" >/dev/null 2>&1
}

PORT=""
for candidate in "$DEFAULT_PORT" 2526 3100 3101 3102 3103; do
  if health "$candidate"; then
    PORT="$candidate"
    echo "attached to running workstation on $candidate"
    break
  fi
done

SERVER_PID=""
if [[ -z "$PORT" ]]; then
  for candidate in "$DEFAULT_PORT" 3100 3101 3102 3103; do
    if ! /usr/bin/nc -z 127.0.0.1 "$candidate" >/dev/null 2>&1; then
      PORT="$candidate"
      break
    fi
  done
  PORT="${PORT:-3104}"
  echo "starting server on 127.0.0.1:$PORT"

  HOST=127.0.0.1 PORT="$PORT" CODEHAVEN_DATA_DIR="$DATA_DIR" NODE_ENV=production \
    "$NODE_BIN" "$WEB_DIR/server.js" &
  SERVER_PID=$!

  for _ in $(seq 1 120); do
    if health "$PORT"; then break; fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      notify_error "The scanner exited during startup. See $LOG_FILE"
      exit 1
    fi
    sleep 0.25
  done

  if ! health "$PORT"; then
    notify_error "The scanner did not become ready on port $PORT. See $LOG_FILE"
    kill "$SERVER_PID" 2>/dev/null || true
    exit 1
  fi
fi

URL="http://127.0.0.1:$PORT/?desktop=1"
/usr/bin/open "$URL"
echo "opened $URL"

# Stop the scanner when the app is quit from the Dock.
cleanup() {
  echo "$(date '+%Y-%m-%d %H:%M:%S')  quitting"
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill -TERM "$SERVER_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$SERVER_PID" 2>/dev/null || break
      sleep 0.25
    done
    kill -KILL "$SERVER_PID" 2>/dev/null || true
  fi
  exit 0
}
trap cleanup INT TERM HUP EXIT

if [[ -n "$SERVER_PID" ]]; then
  wait "$SERVER_PID"
else
  # Attached to an external scanner: stay resident until the app is quit.
  while health "$PORT"; do sleep 5; done
fi
