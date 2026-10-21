#!/bin/bash
# install-poolwatch-macos.sh - check the pool every 15 minutes in the
# background and notify you only when it comes back up.
#
# After this is installed you never have to type the hostname or the port
# again. No output, no terminal window, no reminders while it is still down.
#
#   ./install-poolwatch-macos.sh                 # uses ~/ps.sh if present
#   ./install-poolwatch-macos.sh --interval 600  # check every 10 minutes
#   ./install-poolwatch-macos.sh --uninstall
#
# Requires a working pool-status script. It looks for ~/ps.sh first, then
# ./ops/pool-status.sh next to this installer, and copies whichever it finds
# to ~/.collider/pool-status.sh so the agent has a stable path.
set -euo pipefail

LABEL="com.collider.poolwatch"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
CHECK="$HOME/.collider/pool-status.sh"
LOG="$HOME/Library/Logs/collider-poolwatch.log"
INTERVAL=900

die() { echo "ERROR: $*" >&2; exit 1; }
say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --interval) INTERVAL="${2:-}"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done

[[ "$(uname -s)" == "Darwin" ]] || die "macOS only"

if [[ "${UNINSTALL:-0}" == "1" ]]; then
    launchctl unload "$PLIST" 2>/dev/null || true
    rm -f "$PLIST"
    say "Watcher removed."
    echo "Left in place: $CHECK, $LOG"
    exit 0
fi

say "Locating a status script"
SRC=""
for candidate in "$HOME/ps.sh" "$(dirname "$0")/pool-status.sh"; do
    if [[ -f "$candidate" ]]; then SRC="$candidate"; break; fi
done
[[ -n "$SRC" ]] || die "no pool-status.sh found (looked for ~/ps.sh and ./pool-status.sh)"
mkdir -p "$(dirname "$CHECK")" "$(dirname "$LOG")"
cp "$SRC" "$CHECK"
chmod +x "$CHECK"
echo "  installed $CHECK"

say "Writing launch agent (every ${INTERVAL}s)"
command -v plutil >/dev/null || die "plutil missing - this is not a normal macOS"
cat > "$PLIST" <<PLISTEOF
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/bash</string>
        <string>-c</string>
        <string>if $CHECK >/dev/null 2>&amp;1; then osascript -e 'display notification "Pool is answering again - safe to start the miner" with title "Collision Protocol"'; fi</string>
    </array>
    <key>StartInterval</key>
    <integer>$INTERVAL</integer>
    <key>RunAtLoad</key>
    <true/>
    <key>StandardErrorPath</key>
    <string>$LOG</string>
</dict>
</plist>
PLISTEOF
plutil -lint "$PLIST" >/dev/null || die "generated plist is invalid"

launchctl unload "$PLIST" 2>/dev/null || true
launchctl load "$PLIST"
say "Running"
echo "  Checks every $((INTERVAL / 60)) minutes in the background."
echo "  You will get one notification, and only when the pool answers."
echo
echo "  Stop:     launchctl unload $PLIST"
echo "  Remove:   $0 --uninstall"
echo "  Log:      $LOG"
echo
echo "  While the pool is down this is silent by design - no output,"
echo "  no notification, nothing to respond to."
