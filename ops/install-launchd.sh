#!/bin/bash
# install-launchd.sh - run a Collision Protocol pool worker on macOS.
#
# Usage (daemon - survives logout and reboot):
#   sudo ./install-launchd.sh --worker <btc-address> --binary /path/to/collider \
#                             [--user macuser] [--prevent-sleep] [--lid-closed]
#
# Usage (agent - runs in your login session, no sudo):
#   ./install-launchd.sh --agent --worker <btc-address> --binary /path/to/collider
#
# Uninstall:
#   sudo ./install-launchd.sh --uninstall
#
# Downloads nothing. Put the arm64 release on the Mac, verify its hash against
# the GitHub release page, and pass --binary.
#
# --agent           LaunchAgent in ~/Library/LaunchAgents, everything under
#                   ~/.collider. Stops at logout; enable auto-login if you use
#                   this. Try it if the daemon cannot reach Metal.
# --prevent-sleep   pmset: no system/disk idle sleep, low power mode off.
#                   Requires the lid to stay open.
# --lid-closed      pmset disablesleep 1 - keeps running with the lid shut.
#                   Read ops/MACBOOKS.md first: this does not fix cooling.
set -euo pipefail

LABEL="com.collider.pool"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="$SCRIPT_DIR/launchd/com.collider.pool.plist.template"
WRAPPER_SRC="$SCRIPT_DIR/launchd/collider-pool-wrapper.sh"

WORKER=""
BINARY=""
RUN_USER=""
POOL_URL="jlps://pool.collisionprotocol.com:17403"
MODE="daemon"
PREVENT_SLEEP=0
LID_CLOSED=0
UNINSTALL=0

die()  { echo "ERROR: $*" >&2; exit 1; }
say()  { printf '\n\033[1m== %s\033[0m\n' "$*"; }
warn() { printf '\033[33mWARNING: %s\033[0m\n' "$*" >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --worker)  WORKER="${2:-}"; shift 2 ;;
        --binary)  BINARY="${2:-}"; shift 2 ;;
        --user)    RUN_USER="${2:-}"; shift 2 ;;
        --url)     POOL_URL="${2:-}"; shift 2 ;;
        --agent)   MODE="agent"; shift ;;
        --daemon)  MODE="daemon"; shift ;;
        --prevent-sleep) PREVENT_SLEEP=1; shift ;;
        --lid-closed)    LID_CLOSED=1; shift ;;
        --uninstall)     UNINSTALL=1; shift ;;
        -h|--help) sed -n '2,27p' "$0"; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done

[[ "$(uname -s)" == "Darwin" ]] || die "this installer is macOS only"

# ---- paths differ by mode -------------------------------------------------
if [[ "$MODE" == "daemon" ]]; then
    BASE_DIR="/usr/local/collider"
    WRAPPER="/usr/local/bin/collider-pool"
    LOG_DIR="/usr/local/var/log/collider"
    ENV_DIR="/usr/local/etc/collider"
    ENV_FILE="$ENV_DIR/collider.env"
    PLIST_DST="/Library/LaunchDaemons/$LABEL.plist"
    DOMAIN="system"
else
    BASE_DIR="$HOME/.collider"
    WRAPPER="$BASE_DIR/collider-pool"
    LOG_DIR="$HOME/Library/Logs/collider"
    ENV_FILE="$BASE_DIR/collider.env"
    PLIST_DST="$HOME/Library/LaunchAgents/$LABEL.plist"
    DOMAIN="gui/$(id -u)"
fi
BIN_DST="$BASE_DIR/collider"

# In daemon mode writes outside $HOME need root; in agent mode never sudo.
root() {
    if [[ "$MODE" == "daemon" ]]; then
        if [[ $EUID -eq 0 ]]; then "$@"; else sudo "$@"; fi
    else
        "$@"
    fi
}

# --------------------------------------------------------------------------
if (( UNINSTALL )); then
    if [[ "$MODE" == "daemon" ]]; then
        [[ $EUID -eq 0 ]] || die "run with sudo to uninstall the daemon"
    fi
    say "Unloading $LABEL"
    launchctl bootout "system/$LABEL" 2>/dev/null || true
    launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
    sudo rm -f "/Library/LaunchDaemons/$LABEL.plist" 2>/dev/null || true
    rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
    pkill -f "collider" 2>/dev/null || true
    say "Removed. Left in place: $BASE_DIR, $LOG_DIR, $ENV_FILE"
    echo "Delete those by hand if you are done."
    if pmset -g 2>/dev/null | grep -qi 'SleepDisabled[[:space:]]*1'; then
        echo "Lid-closed sleep is still disabled. Undo with:"
        echo "  sudo pmset -a disablesleep 0"
    fi
    exit 0
fi

[[ -n "$WORKER" ]] || die "--worker <btc-address> is required"
[[ -n "$BINARY" && -f "$BINARY" ]] || die "--binary <path to collider> is required and must exist"
[[ -f "$TEMPLATE" ]] || die "missing $TEMPLATE - run this from the repo's ops/ directory"
[[ -f "$WRAPPER_SRC" ]] || die "missing $WRAPPER_SRC"

if [[ "$MODE" == "daemon" ]]; then
    [[ $EUID -eq 0 ]] || die "daemon mode needs sudo (or use --agent)"
    RUN_USER="${RUN_USER:-${SUDO_USER:-}}"
    [[ -n "$RUN_USER" && "$RUN_USER" != "root" ]] \
        || die "pass --user <your-mac-username>; do not run the miner as root"
    id "$RUN_USER" >/dev/null 2>&1 || die "no such user: $RUN_USER"
else
    [[ $EUID -ne 0 ]] || die "do not run agent mode as root"
    RUN_USER="$(id -un)"
fi

# --------------------------------------------------------------------------
say "Validating payout address"
if command -v python3 >/dev/null 2>&1; then
    if [[ -f "$SCRIPT_DIR/validate-worker.py" ]]; then
        python3 "$SCRIPT_DIR/validate-worker.py" "$WORKER" \
            || die "address failed checksum validation - refusing to install"
    else
        python3 - "$WORKER" <<'PY' || die "address failed checksum validation"
import hashlib, sys
a = sys.argv[1]
AL = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
try:
    n = 0
    for c in a:
        n = n * 58 + AL.index(c)
    raw = b"\0" * (len(a) - len(a.lstrip("1"))) + n.to_bytes((n.bit_length() + 7) // 8, "big")
    payload, chk = raw[:-4], raw[-4:]
    ok = hashlib.sha256(hashlib.sha256(payload).digest()).digest()[:4] == chk
except ValueError:
    ok = False
if not ok:
    sys.exit(1)
print("OK    checksum valid, %s" % {0x00: "P2PKH", 0x05: "P2SH"}.get(payload[0], "mainnet"))
PY
    fi
else
    warn "python3 not found; skipping checksum validation. Verify the address by hand."
fi

say "Checking the binary"
ARCH="$(file -b "$BINARY" 2>/dev/null || true)"
case "$ARCH" in
    *arm64*)  echo "  $(echo "$ARCH" | cut -c1-90)" ;;
    *x86_64*) warn "Intel binary - on Apple Silicon it needs Rosetta and will be slow" ;;
    *)        warn "could not identify architecture for $BINARY" ;;
esac
if xattr -p com.apple.quarantine "$BINARY" >/dev/null 2>&1; then
    echo "  clearing quarantine flag (launchd cannot exec a quarantined binary)"
    xattr -d com.apple.quarantine "$BINARY" 2>/dev/null || true
fi
if ! "$BINARY" --help >/dev/null 2>&1; then
    warn "'$BINARY --help' failed. Gatekeeper may be blocking it: System Settings"
    warn "> Privacy & Security > Open Anyway. Check it is not a damaged download."
fi

say "Installing to $BASE_DIR"
root mkdir -p "$BASE_DIR" "$LOG_DIR"
root install -m 0755 "$BINARY" "$BIN_DST"
if [[ -d "$(dirname "$BINARY")/data" ]]; then
    root cp -R "$(dirname "$BINARY")/data" "$BASE_DIR/"
fi
root install -m 0755 "$WRAPPER_SRC" "$WRAPPER"

TMP_ENV="$(mktemp)"
cat > "$TMP_ENV" <<EOF
# Collision Protocol worker identity.
# Every DP is credited to this string and the payout is sent here.
# The pool does NOT validate it. Back up the wallet that controls it.
COLLIDER_WORKER=$WORKER
COLLIDER_POOL_URL=$POOL_URL
COLLIDER_BIN=$BIN_DST
# Extra flags appended verbatim, e.g. --gpus 0 or --no-update
#COLLIDER_EXTRA_ARGS=--no-update
EOF
root install -m 0600 "$TMP_ENV" "$ENV_FILE"
root chown "$RUN_USER" "$ENV_FILE" 2>/dev/null || true
root chown -R "$RUN_USER" "$LOG_DIR" 2>/dev/null || true
rm -f "$TMP_ENV"

say "Installing launchd job ($MODE)"
GENERATED="$(mktemp -t colliderplist)"
sed -e "s|__LABEL__|$LABEL|g" \
    -e "s|__USER__|$RUN_USER|g" \
    -e "s|__WRAPPER__|$WRAPPER|g" \
    -e "s|__WORKDIR__|$BASE_DIR|g" \
    -e "s|__ENV_FILE__|$ENV_FILE|g" \
    -e "s|__LOG_DIR__|$LOG_DIR|g" \
    "$TEMPLATE" > "$GENERATED"
plutil -lint "$GENERATED" >/dev/null || die "generated plist is not valid"

# UserName/GroupName belong to daemons; drop them for an agent.
if [[ "$MODE" == "agent" ]]; then
    plutil -remove UserName "$GENERATED" 2>/dev/null || true
    plutil -remove GroupName "$GENERATED" 2>/dev/null || true
fi

root mkdir -p "$(dirname "$PLIST_DST")"
root install -m 0644 "$GENERATED" "$PLIST_DST"
rm -f "$GENERATED"

# Replace any previous copy.
launchctl bootout "system/$LABEL" 2>/dev/null || true
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true

launchctl bootstrap "$DOMAIN" "$PLIST_DST"
launchctl enable "$DOMAIN/$LABEL"
launchctl kickstart -k "$DOMAIN/$LABEL" 2>/dev/null || true

# --------------------------------------------------------------------------
if (( PREVENT_SLEEP )); then
    say "Disabling idle sleep (keep the lid OPEN)"
    root pmset -a sleep 0 disksleep 0 powernap 0 lowpowermode 0 displaysleep 15
    echo "  the display still blanks after 15 min - that does not stop the worker"
fi

if (( LID_CLOSED )); then
    say "Enabling lid-closed operation"
    root pmset -a disablesleep 1
    warn "A MacBook cooling with the lid shut will throttle, and the chassis gets hot."
    warn "Hard surface only, never in a bag. Undo with: sudo pmset -a disablesleep 0"
fi

sleep 5
say "Status"
launchctl print "$DOMAIN/$LABEL" 2>/dev/null | sed -n '1,10p' || true

echo
echo "Log:        tail -f $LOG_DIR/collider.out.log"
echo "Assertions: pmset -g assertions | grep -i caffeinate"
echo "Sleep flag: pmset -g | grep -i SleepDisabled"
echo "Dashboard:  https://collisionprotocol.com/dashboard?worker=$WORKER"
echo
echo "Stop:       sudo launchctl bootout $DOMAIN/$LABEL"
echo "Restart:    sudo launchctl kickstart -k $DOMAIN/$LABEL"
echo "Uninstall:  sudo ./install-launchd.sh --uninstall"
