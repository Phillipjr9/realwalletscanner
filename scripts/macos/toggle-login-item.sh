#!/bin/bash
# =============================================================================
#  Enable or disable "start at login" for CodeHaven Workstation on macOS.
#
#      bash scripts/macos/toggle-login-item.sh enable  ["/Applications/CodeHaven Workstation.app"]
#      bash scripts/macos/toggle-login-item.sh disable
#      bash scripts/macos/toggle-login-item.sh status
#
#  Uses a per-user LaunchAgent (no admin rights, no Automation permission
#  prompts). The app starts hidden in the menu bar; open the window from the
#  shield icon whenever you want it.
# =============================================================================
set -uo pipefail

ACTION="${1:-status}"
APP_PATH="${2:-/Applications/CodeHaven Workstation.app}"
LABEL="app.codehaven.workstation.login"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

case "$ACTION" in
  enable)
    if [[ ! -d "$APP_PATH" ]]; then
      echo "App not found: $APP_PATH" >&2
      exit 1
    fi
    mkdir -p "$HOME/Library/LaunchAgents"
    cat > "$PLIST" <<PLISTEOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/bin/open</string>
    <string>-a</string>
    <string>$APP_PATH</string>
    <string>--args</string>
    <string>--hidden</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>ProcessType</key><string>Interactive</string>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
</dict>
</plist>
PLISTEOF
    launchctl bootout "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || true
    launchctl bootstrap "gui/$(id -u)" "$PLIST" >/dev/null 2>&1 \
      || launchctl load -w "$PLIST" >/dev/null 2>&1 \
      || true
    echo "Login item enabled: $LABEL"
    ;;
  disable)
    launchctl bootout "gui/$(id -u)/$LABEL" >/dev/null 2>&1 \
      || launchctl unload -w "$PLIST" >/dev/null 2>&1 \
      || true
    rm -f "$PLIST"
    echo "Login item disabled"
    ;;
  status)
    if [[ -f "$PLIST" ]]; then
      echo "enabled ($PLIST)"
    else
      echo "disabled"
    fi
    ;;
  *)
    echo "usage: $0 {enable|disable|status} [app-path]" >&2
    exit 2
    ;;
esac
