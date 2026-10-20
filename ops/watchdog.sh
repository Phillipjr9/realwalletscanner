#!/usr/bin/env bash
# watchdog.sh - keep a collider pool worker alive without systemd.
#
# For tmux/nohup setups, WSL, or any box where you do not want a unit file.
# If you can use install-systemd.sh instead, do that - it is more robust.
#
# Usage:
#   ./watchdog.sh                      # foreground; Ctrl+C stops both it and the worker
#   nohup ./watchdog.sh >/dev/null 2>&1 &
#
# Config (env or edit the defaults below):
#   COLLIDER_BIN   path to the collider binary            (default ./collider)
#   COLLIDER_ARGS  arguments after the binary path        (default: pool + worker below)
#   COLLIDER_LOG   log file                               (default ./collider.log)
#   INTERVAL       seconds between health checks          (default 60)
#   STALE_SECS     log age that counts as "not working"   (default 1800)
#   RESTART_ON_STALE  1 = restart after STALE_SECS of silence, 0 = warn only (default 1)
#
# Restarting is safe: DP_ACK-ed points are already credited server-side, and the
# server reissues the in-flight chunk to the same worker name. You lose only DPs
# computed but not yet flushed (a few seconds).
set -uo pipefail

COLLIDER_BIN="${COLLIDER_BIN:-./collider}"
COLLIDER_ARGS="${COLLIDER_ARGS:---pool jlps://pool.collisionprotocol.com:17403 --worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP --no-tui --verbose}"
COLLIDER_LOG="${COLLIDER_LOG:-./collider.log}"
INTERVAL="${INTERVAL:-60}"
STALE_SECS="${STALE_SECS:-1800}"
RESTART_ON_STALE="${RESTART_ON_STALE:-1}"
MAX_LOG_BYTES=$((100 * 1024 * 1024))

cd "$(dirname "$0")" || exit 1
[[ -x "$COLLIDER_BIN" ]] || { echo "watchdog: $COLLIDER_BIN not executable" >&2; exit 1; }

# Single instance.
exec 9>".watchdog.lock"
flock -n 9 || { echo "watchdog: another instance is running" >&2; exit 1; }

say() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }
PID=""

rotate() {
  local size
  size=$(stat -c%s "$COLLIDER_LOG" 2>/dev/null || echo 0)
  if (( size > MAX_LOG_BYTES )); then
    mv -f "$COLLIDER_LOG" "$COLLIDER_LOG.1" 2>/dev/null || true
    say "rotated log (>${MAX_LOG_BYTES} bytes)"
  fi
}

start_worker() {
  rotate
  # shellcheck disable=SC2086
  nohup "$COLLIDER_BIN" $COLLIDER_ARGS >>"$COLLIDER_LOG" 2>&1 &
  PID=$!
  echo "$PID" > .watchdog.pid
  say "started collider pid=$PID"
}

stop_worker() {
  local pid="${1:-}"
  [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null || return 0
  say "SIGTERM pid=$pid (lets queued DPs flush)"
  kill -TERM "$pid" 2>/dev/null || true
  for _ in $(seq 1 30); do
    kill -0 "$pid" 2>/dev/null || { say "exited cleanly"; return 0; }
    sleep 1
  done
  say "did not exit in 30s, SIGKILL"
  kill -9 "$pid" 2>/dev/null || true
}

cleanup() { say "shutting down"; stop_worker "${PID:-}"; exit 0; }
trap cleanup INT TERM

if [[ -f .watchdog.pid ]]; then
  OLD=$(cat .watchdog.pid 2>/dev/null || echo "")
  if [[ -n "$OLD" ]] && kill -0 "$OLD" 2>/dev/null; then
    PID="$OLD"; say "adopting existing worker pid=$PID"
  fi
fi
[[ -n "$PID" ]] || start_worker

FAILS=0
while true; do
  sleep "$INTERVAL"

  if [[ -z "$PID" ]] || ! kill -0 "$PID" 2>/dev/null; then
    FAILS=$((FAILS + 1))
    say "worker is gone (failure #$FAILS) - restarting in $(( FAILS < 5 ? 5 : 60 ))s"
    sleep $(( FAILS < 5 ? 5 : 60 ))
    start_worker
    continue
  fi
  FAILS=0

  # Liveness by log growth. Steady state with --verbose writes constantly; a
  # quiet log means the pool connection is wedged or the GPU has fallen over.
  if [[ -f "$COLLIDER_LOG" ]]; then
    AGE=$(( $(date +%s) - $(stat -c %Y "$COLLIDER_LOG") ))
    if (( AGE > STALE_SECS )); then
      say "log untouched for ${AGE}s"
      if [[ "$RESTART_ON_STALE" == "1" ]]; then
        say "restarting (RESTART_ON_STALE=1)"
        stop_worker "$PID"; PID=""; start_worker
      fi
    fi
  fi
done
