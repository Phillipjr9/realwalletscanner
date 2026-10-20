#!/usr/bin/env bash
# pool-status.sh - is the Collision Protocol pool actually up right now?
#
# Run this BEFORE starting the miner. It answers one question: is there a
# listener on the JLP port? Nothing else you do matters if the answer is no.
#
#   ./ops/pool-status.sh
#   ./ops/pool-status.sh --host pool.collisionprotocol.com --port 17403
#   ./ops/pool-status.sh --watch            # block until the pool returns
#   ./ops/pool-status.sh --watch --interval 120
#
# --watch polls every --interval seconds (default 300) and exits as soon as the
# port answers, with a desktop notification on macOS. Start it in a terminal and
# forget about it; it tells you the moment the pool is back instead of you
# checking by hand.
#
# Exit codes: 0 = pool reachable, 1 = pool down, 2 = usage error.
#
# Distinguishes the three failures that matter:
#   Connection refused  -> a machine answered, nothing is listening. Server-side.
#   Timed out           -> packets dropped. Usually your firewall/VPN/ISP.
#   Could not resolve   -> DNS. Your network or the domain.
#
# Note: a transparent/corporate proxy answers every port, so "OPEN" there means
# nothing. If in doubt, point --host at a bogus IP; a real network refuses it.
set -uo pipefail

HOST="pool.collisionprotocol.com"
PORT="17403"
STATE="${HOME}/.collider/pool-status.state"
WATCH=0
INTERVAL=300

while [[ $# -gt 0 ]]; do
    case "$1" in
        --host) HOST="${2:-}"; shift 2 ;;
        --port) PORT="${2:-}"; shift 2 ;;
        --watch) WATCH=1; shift ;;
        --interval) INTERVAL="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

tcp_state() {
    local err rc
    err=$(nc -vz -w 6 "$1" "$2" 2>&1); rc=$?
    if [[ $rc -eq 0 ]]; then echo open; return; fi
    case "$err" in
        *"Connection refused"*)                   echo refused ;;
        *"Timed out"*|*"timed out"*)              echo timeout ;;
        *"Could not resolve"*|*"nodename"*)       echo dns ;;
        *)                                        echo error ;;
    esac
}

notify() {
    printf '\a'
    if [[ "$(uname -s)" == "Darwin" ]]; then
        osascript -e "display notification \"$2\" with title \"$1\"" >/dev/null 2>&1 || true
    fi
}

if [[ $WATCH -eq 1 ]]; then
    echo "Watching $HOST:$PORT every ${INTERVAL}s. Ctrl+C to stop."
    echo "You will get a notification when it answers."
    while true; do
        state=$(tcp_state "$HOST" "$PORT")
        if [[ "$state" == "open" ]]; then
            echo
            echo "[$(date '+%H:%M:%S')] POOL IS UP - $HOST:$PORT is answering"
            notify "Collision Protocol" "Pool is back up. Safe to start the miner."
            exit 0
        fi
        printf '.'
        sleep "$INTERVAL"
    done
fi

echo "Checking $HOST:$PORT"
echo

# ---- 1. DNS ---------------------------------------------------------------
if ! host_out=$(dscacheutil -q host -a name "$HOST" 2>/dev/null) \
   && ! host_out=$(getent hosts "$HOST" 2>/dev/null); then
    host_out=""
fi
if [[ -z "$host_out" ]]; then
    if ! ping -c 1 -W 2 "$HOST" >/dev/null 2>&1; then
        echo "  RESULT: DNS FAILED - cannot resolve $HOST"
        echo "          Your network is down or the domain is gone."
        exit 1
    fi
fi
echo "  DNS: ok"

# ---- 2. TCP --------------------------------------------------------------
# Capture nc's stderr: it tells refused from timeout.
nc_err=$(nc -vz -w 6 "$HOST" "$PORT" 2>&1)
nc_rc=$?

if [[ $nc_rc -eq 0 ]]; then
    echo "  TCP: OPEN - something is listening on $PORT"
    status_ok=1
else
    status_ok=0
    case "$nc_err" in
        *"Connection refused"*)
            echo "  TCP: REFUSED - the host is up, nothing is listening on $PORT"
            echo "        This is server-side. Nothing you can do locally."
            ;;
        *"Timed out"*|*"timed out"*|*"Operation timed out"*)
            echo "  TCP: TIMED OUT - packets are being dropped before they arrive"
            echo "        Usually a firewall, VPN, or ISP filtering this port."
            ;;
        *"Could not resolve"*|*"nodename"*)
            echo "  TCP: DNS FAILED"
            ;;
        *)
            echo "  TCP: FAILED"
            echo "        ${nc_err:-no output from nc}"
            ;;
    esac
fi

# ---- 3. Web side, to catch a frozen snapshot -----------------------------
echo
page_hash=""
if command -v curl >/dev/null 2>&1; then
    if body=$(curl -fsS --max-time 15 "https://collisionprotocol.com/pool" 2>/dev/null); then
        page_hash=$(printf '%s' "$body" | shasum -a 256 2>/dev/null | cut -d' ' -f1)
        [[ -z "$page_hash" ]] && page_hash=$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)

        # Pull the headline numbers if they are in the markup.
        dps=$(printf '%s' "$body" | grep -oE '[0-9]+\.[0-9]+M' | head -1)
        workers=$(printf '%s' "$body" | grep -oiE '[0-9]+ *active workers' | head -1)
        echo "  Web:  reachable${dps:+  DPs=$dps}${workers:+  $workers}"

        if [[ -f "$STATE" ]]; then
            prev=$(cat "$STATE" 2>/dev/null || echo "")
            if [[ -n "$prev" && "$prev" == "$page_hash" ]]; then
                echo "        WARNING: page is byte-identical to your last check."
                echo "        A live dashboard does not stay frozen - the snapshot"
                echo "        is probably stale, and the pool backend is likely down."
            fi
        fi
        mkdir -p "$(dirname "$STATE")" 2>/dev/null
        printf '%s' "$page_hash" > "$STATE" 2>/dev/null
    else
        echo "  Web:  unreachable (https://collisionprotocol.com/pool)"
    fi
fi

# ---- verdict -------------------------------------------------------------
echo
if [[ $status_ok -eq 1 ]]; then
    echo "VERDICT: pool looks UP. Safe to start the miner:"
    echo "  cd ~/puzzle && ./collider -c ~/.collider/pool-mode.yml --verbose"
    exit 0
fi

echo "VERDICT: pool is DOWN. Do not start the miner - it will just retry."
echo "         Re-check later; the operator is a single person and the pool"
echo "         has been offline before. Nothing on your machine can fix this."
exit 1
