#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — macOS installer / builder
#  Double-click this file in Finder (or run: bash scripts/macos/install-macos.command)
#
#  What it does:
#    1. Verifies Node.js 18+ is available
#    2. Installs the workstation's runtime dependencies (bip39)
#    3. Optionally installs the Electron desktop shell
#    4. Builds "CodeHaven Workstation.app" and offers to install it to /Applications
# =============================================================================
set -uo pipefail

APP_NAME="CodeHaven Workstation"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
WEB_DIR="$ROOT/WebWorkstation"
DESKTOP_DIR="$WEB_DIR/desktop"
OUT_DIR="$ROOT/dist"

BOLD=$'\033[1m'; DIM=$'\033[2m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; CYAN=$'\033[36m'; RESET=$'\033[0m'

say()  { printf '%s\n' "$*"; }
ok()   { printf '%s✓%s %s\n' "$GREEN" "$RESET" "$*"; }
warn() { printf '%s!%s %s\n' "$YELLOW" "$RESET" "$*"; }
fail() { printf '%s✗%s %s\n' "$RED" "$RESET" "$*"; }
head1(){ printf '\n%s%s%s\n' "$BOLD$CYAN" "$*" "$RESET"; }

clear
say "${BOLD}CodeHaven Workstation — macOS installer${RESET}"
say "${DIM}Repository: $ROOT${RESET}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  fail "This installer targets macOS. On Linux/Windows run: cd WebWorkstation && npm start"
  exit 1
fi

# ── 1. Node.js ───────────────────────────────────────────────────────────────
head1 "1. Checking Node.js"
NODE_BIN="$(command -v node || true)"
if [[ -z "$NODE_BIN" ]]; then
  for candidate in /opt/homebrew/bin/node /usr/local/bin/node "$HOME/.nvm/versions/node"/*/bin/node; do
    if [[ -x "$candidate" ]]; then NODE_BIN="$candidate"; break; fi
  done
fi

if [[ -z "$NODE_BIN" ]]; then
  fail "Node.js was not found."
  say ""
  say "Install it in one of these ways, then re-run this installer:"
  say "  • Homebrew:  ${BOLD}brew install node${RESET}"
  say "  • Official:  https://nodejs.org/en/download  (choose the macOS .pkg installer)"
  say ""
  read -r -p "Open the Node.js download page now? [y/N] " answer
  if [[ "${answer:-}" =~ ^[Yy]$ ]]; then open "https://nodejs.org/en/download"; fi
  exit 1
fi

NODE_VERSION="$("$NODE_BIN" -v 2>/dev/null | sed 's/^v//')"
NODE_MAJOR="${NODE_VERSION%%.*}"
if [[ "${NODE_MAJOR:-0}" -lt 18 ]]; then
  fail "Node.js $NODE_VERSION found, but 18 or newer is required."
  say "Upgrade with: ${BOLD}brew upgrade node${RESET}"
  exit 1
fi
ok "Node.js v$NODE_VERSION ($NODE_BIN)"
export PATH="$(dirname "$NODE_BIN"):$PATH"

NPM_BIN="$(command -v npm || true)"
if [[ -z "$NPM_BIN" ]]; then
  fail "npm was not found alongside Node.js."
  exit 1
fi
ok "npm $(npm -v)"

# ── 2. Runtime dependencies ─────────────────────────────────────────────────
head1 "2. Installing scanner dependencies"
cd "$WEB_DIR" || exit 1
if [[ -d node_modules/bip39 ]]; then
  ok "Dependencies already present"
else
  say "${DIM}npm install (this only needs to run once)…${RESET}"
  if npm install --no-audit --no-fund --loglevel=error; then
    ok "Dependencies installed"
  else
    fail "npm install failed. Check your internet connection and try again."
    exit 1
  fi
fi

# Quick smoke test: the server must be able to load its crypto engine
if node -e "require('./crypto_engine.js')" >/dev/null 2>&1; then
  ok "Crypto engine loads correctly"
else
  warn "Crypto engine smoke test failed — the app may still run, check the log if it does not."
fi

# ── 3. Build the native app ─────────────────────────────────────────────────
head1 "3. Building $APP_NAME.app"
say ""
say "Choose how to install:"
say "  ${BOLD}1${RESET}) Native window app (Electron)  — real Dock app, native menu bar, ~250 MB"
say "  ${BOLD}2${RESET}) Lightweight app (browser UI)   — starts the scanner and opens your browser, ~5 MB"
say "  ${BOLD}3${RESET}) Both"
say ""
read -r -p "Selection [1]: " choice
choice="${choice:-1}"

build_electron() {
  cd "$DESKTOP_DIR" || return 1
  if [[ ! -d node_modules/electron ]]; then
    say "${DIM}Installing Electron toolchain (one-time, downloads ~100 MB)…${RESET}"
    npm install --no-audit --no-fund --loglevel=error || { fail "Electron install failed"; return 1; }
  fi
  say "${DIM}Packaging app…${RESET}"
  npx electron-builder --mac --dir --publish never || { fail "electron-builder failed"; return 1; }
  return 0
}

if [[ "$choice" == "1" || "$choice" == "3" ]]; then
  if build_electron; then
    BUILT_APP="$(find "$OUT_DIR" -maxdepth 3 -name "$APP_NAME.app" -type d 2>/dev/null | head -1)"
    if [[ -n "$BUILT_APP" ]]; then
      ok "Built: $BUILT_APP"
      xattr -cr "$BUILT_APP" 2>/dev/null || true
      read -r -p "Copy it to /Applications? [Y/n] " ans
      if [[ ! "${ans:-y}" =~ ^[Nn]$ ]]; then
        rm -rf "/Applications/$APP_NAME.app"
        if cp -R "$BUILT_APP" "/Applications/$APP_NAME.app"; then
          xattr -cr "/Applications/$APP_NAME.app" 2>/dev/null || true
          ok "Installed to /Applications/$APP_NAME.app"
          NATIVE_INSTALLED=1
        else
          warn "Could not copy to /Applications (permission denied?)"
          say "Drag it there manually from: $BUILT_APP"
        fi
      fi
    else
      warn "electron-builder finished but no .app was found in $OUT_DIR"
    fi
  fi
fi

if [[ "$choice" == "2" || "$choice" == "3" ]]; then
  bash "$SCRIPT_DIR/build-macos-app.sh" "$OUT_DIR" || warn "Lightweight app build failed"
  if [[ -d "$OUT_DIR/$APP_NAME.app" ]]; then
    xattr -cr "$OUT_DIR/$APP_NAME.app" 2>/dev/null || true
    read -r -p "Copy the lightweight app to /Applications? [Y/n] " ans2
    if [[ ! "${ans2:-y}" =~ ^[Nn]$ ]]; then
      rm -rf "/Applications/$APP_NAME Lite.app"
      cp -R "$OUT_DIR/$APP_NAME.app" "/Applications/$APP_NAME Lite.app" 2>/dev/null \
        && ok "Installed to /Applications/$APP_NAME Lite.app" \
        || warn "Copy failed — drag it manually from $OUT_DIR"
    fi
  fi
fi

# ── 4. Done ─────────────────────────────────────────────────────────────────
head1 "Done"
say "Launch it from Launchpad / Applications, or run:"
say "  ${BOLD}open \"/Applications/$APP_NAME.app\"${RESET}"
say ""
say "${DIM}• Data, vaults and logs live in: ~/Library/Application Support/CodeHaven Workstation${RESET}"
say "${DIM}• The scanner binds to 127.0.0.1 only and is never exposed to your network.${RESET}"
say "${DIM}• First launch of an unsigned app: right-click → Open (macOS Gatekeeper).${RESET}"
say ""
read -r -p "Open the app now? [y/N] " launch
if [[ "${launch:-}" =~ ^[Yy]$ ]]; then
  if [[ -d "/Applications/$APP_NAME.app" ]]; then open "/Applications/$APP_NAME.app"; fi
fi
