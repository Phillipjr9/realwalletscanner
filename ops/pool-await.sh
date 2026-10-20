#!/usr/bin/env bash
# pool-await.sh - wait for the Collision Protocol pool, then start mining.
#
# The pool has been down (connection refused) since roughly 2026-10-19. This
# script does not pretend otherwise: it validates your payout address, polls the
# endpoint, and only launches `collider` once a listener actually answers. If the
# worker later drops, it restarts it. If the pool goes away again, it goes back
# to waiting instead of spamming reconnects.
#
# Usage:
#   ./pool-await.sh --worker bc1q... --binary ~/puzzle/collider
#   ./pool-await.sh --worker bc1q... --binary ~/puzzle/collider --interval 600
#   ./pool-await.sh --worker bc1q... --binary ~/puzzle/collider --dry-run
#
# --dry-run   validate the address and check the pool, but never start anything
# --once      single pass: check once, start only if the pool answers, then run
# --log FILE  tee the miner output here (default ./collider.log)
#
# Exit codes: 0 = the miner exited 0, 1 = pool never came up and --once was set,
#             2 = usage/config error.
set -uo pipefail

WORKER=""
BINARY=""
POOL_URL="jlps://pool.collisionprotocol.com:17403"
HOST="pool.collisionprotocol.com"
PORT="17403"
INTERVAL=300
LOG="./collider.log"
DRY_RUN=0
ONCE=0
EXTRA_ARGS=""

die() { echo "ERROR: $*" >&2; exit 2; }
say() { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --worker)   WORKER="${2:-}"; shift 2 ;;
        --binary)   BINARY="${2:-}"; shift 2 ;;
        --pool)     POOL_URL="${2:-}"; shift 2 ;;
        --interval) INTERVAL="${2:-}"; shift 2 ;;
        --log)      LOG="${2:-}"; shift 2 ;;
        --extra)    EXTRA_ARGS="${2:-}"; shift 2 ;;
        --dry-run)  DRY_RUN=1; shift ;;
        --once)     ONCE=1; shift ;;
        -h|--help)  sed -n '2,23p' "$0"; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done

[[ -n "$WORKER" ]] || die "--worker <btc-address> is required"
[[ -n "$BINARY" ]] || die "--binary <path to collider> is required"
[[ -x "$BINARY" ]] || die "not executable: $BINARY"

# Host/port come from the URL so a different pool works unchanged.
stripped="${POOL_URL#*://}"
HOST="${stripped%%:*}"
PORT="${stripped##*:}"
[[ "$PORT" =~ ^[0-9]+$ ]] || die "could not parse a port from --pool $POOL_URL"

# ---- 1. The address is the only irreversible thing. Check it first. -------
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$here/validate-worker.py" ]] && command -v python3 >/dev/null 2>&1; then
    if ! python3 "$here/validate-worker.py" --quiet "$WORKER"; then
        echo "ERROR: payout address failed validation - refusing to start." >&2
        python3 "$here/validate-worker.py" "$WORKER" >&2 || true
        exit 2
    fi
    say "payout address: checksum valid"
else
    say "WARNING: validator unavailable; cannot check $WORKER"
fi

say "pool:     $POOL_URL"
say "binary:   $BINARY"
say "interval: ${INTERVAL}s   log: $LOG"

# ---- 2. One probe. Returns 0 only if something is listening. -------------
probe() {
    local err
    err=$(nc -z -w 6 "$HOST" "$PORT" 2>&1)
    [[ $? -eq 0 ]]
}

if (( DRY_RUN )); then
    if probe; then
        say "POOL ANSWERS - would start the miner now"
        echo "  $BINARY --pool $POOL_URL --worker $WORKER --verbose"
        exit 0
    fi
    say "pool does not answer - the miner would keep waiting"
    exit 1
fi

# ---- 3. Wait for the pool, then supervise the worker. --------------------
launch() {
    say "pool is answering - starting miner"
    # shellcheck disable=SC2086
    "$BINARY" --pool "$POOL_URL" --worker "$WORKER" --verbose $EXTRA_ARGS 2>&1 | tee -a "$LOG"
    return "${PIPESTATUS[0]}"
}

trap 'say "stopping"; exit 0' INT TERM

while true; do
    if ! probe; then
        if (( ONCE )); then
            say "pool does not answer ($HOST:$PORT) - not starting"
            exit 1
        fi
        say "pool down (refused/timeout) - retrying in ${INTERVAL}s, Ctrl+C to stop"
        sleep "$INTERVAL"
        continue
    fi

    say "pool is UP"
    launch
    rc=$?

    if (( ONCE )); then
        exit "$rc"
    fi

    # Worker exited. Distinguish "pool vanished" from "client crashed" so a
    # dead pool does not turn into a tight reconnect loop.
    if probe; then
        say "worker exited (rc=$rc) but the pool is still up - restarting in 30s"
        sleep 30
    else
        say "worker exited (rc=$rc) and the pool is gone again - back to waiting"
        sleep "$INTERVAL"
    fi
done
