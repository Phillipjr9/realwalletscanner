#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — macOS installer
#
#  Double-click this file in Finder (or run it from Terminal). It is a friendly
#  front door for the one real installer:
#
#      scripts/macos/make-standalone-app.command
#
#  That builds "CodeHaven Workstation.app" (the full scanner: UI, server,
#  workers, crypto dependencies and its own Node runtime), installs it to
#  /Applications, verifies it by boot-testing it, and offers to start it at
#  login. After it finishes you never need Terminal again — open the app from
#  Launchpad.
#
#  Options are forwarded as-is, e.g.:
#      bash scripts/macos/install-macos.command --yes
#      bash scripts/macos/install-macos.command --dmg
#      bash scripts/macos/install-macos.command --no-login
# =============================================================================
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

clear
printf '\033[1mCodeHaven Workstation — macOS installer\033[0m\n'
printf '\033[2mBuilding and installing the standalone app. This is the only installer you need.\033[0m\n\n'

if [[ "$(uname -s)" != "Darwin" ]]; then
  printf '\033[31mThis installer targets macOS. On Linux/Windows run: cd WebWorkstation && npm start\033[0m\n'
  exit 1
fi

exec bash "$SCRIPT_DIR/make-standalone-app.command" "$@"
