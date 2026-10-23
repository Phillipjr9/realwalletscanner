#!/bin/bash
# =============================================================================
#  CodeHaven Workstation — build a standalone Mac app (one command)
#
#      bash scripts/macos/make-standalone-app.command
#
#  Produces /Applications/CodeHaven Workstation.app, which contains the whole
#  scanner (UI + Node runtime + crypto deps). After this runs you never need the
#  terminal again: launch it from Launchpad and it runs on its own.
#
#  Options:
#      --dmg        also produce a distributable .dmg in ./dist
#      --no-install build only, leave the .app in ./dist
#      --no-login   do not register the start-at-login item
#      --yes        no questions: install to /Applications + start at login
# =============================================================================
set -uo pipefail

APP_NAME="CodeHaven Workstation"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
WEB_DIR="$ROOT/WebWorkstation"
DESKTOP_DIR="$WEB_DIR/desktop"
OUT_DIR="$ROOT/dist"

BUILD_DMG=0
DO_INSTALL=1
ASSUME_YES=0
DO_LOGIN=1
for arg in "$@"; do
  case "$arg" in
    --dmg) BUILD_DMG=1 ;;
    --no-install) DO_INSTALL=0 ; DO_LOGIN=0 ;;
    --no-login) DO_LOGIN=0 ;;
    --yes|-y) ASSUME_YES=1 ;;
  esac
done

BOLD=$'\033[1m'; DIM=$'\033[2m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; CYAN=$'\033[36m'; RESET=$'\033[0m'
ok()   { printf '%s✓%s %s\n' "$GREEN" "$RESET" "$*"; }
warn() { printf '%s!%s %s\n' "$YELLOW" "$RESET" "$*"; }
fail() { printf '%s✗%s %s\n' "$RED" "$RESET" "$*"; }
step() { printf '\n%s%s%s\n' "$BOLD$CYAN" "$*" "$RESET"; }

printf '%s%s%s\n' "$BOLD" "CodeHaven Workstation — standalone Mac build" "$RESET"
printf '%srepo: %s%s\n' "$DIM" "$ROOT" "$RESET"

[[ "$(uname -s)" == "Darwin" ]] || { fail "Run this on your MacBook (it builds a macOS .app)."; exit 1; }

# ── Node.js ─────────────────────────────────────────────────────────────────
step "1/5  Checking Node.js"
NODE_BIN="$(command -v node || true)"
for candidate in /opt/homebrew/bin/node /usr/local/bin/node "$HOME/.nvm/versions/node"/*/bin/node "$HOME/.volta/bin/node"; do
  [[ -n "$NODE_BIN" && -x "$NODE_BIN" ]] && break
  [[ -x "$candidate" ]] && NODE_BIN="$candidate"
done
[[ -n "$NODE_BIN" ]] || { fail "Node.js 18+ is required. Install: brew install node"; exit 1; }
export PATH="$(dirname "$NODE_BIN"):$PATH"
NODE_MAJOR="$("$NODE_BIN" -v | sed 's/^v//' | cut -d. -f1)"
[[ "$NODE_MAJOR" -ge 18 ]] || { fail "Node.js $("$NODE_BIN" -v) is too old (need 18+)."; exit 1; }
ok "Node.js $("$NODE_BIN" -v)"

# ── Runtime dependencies ────────────────────────────────────────────────────
step "2/5  Installing scanner dependencies"
cd "$WEB_DIR" || exit 1
if [[ -d node_modules/bip39 && -d node_modules/@noble/hashes ]]; then
  ok "Dependencies already installed"
else
  npm install --no-audit --no-fund --loglevel=error || { fail "npm install failed"; exit 1; }
  ok "Dependencies installed"
fi
node -e "require('./crypto_engine.js')" >/dev/null 2>&1 \
  && ok "Crypto engine verified" \
  || { fail "Crypto engine failed to load"; exit 1; }

# ── App icon ────────────────────────────────────────────────────────────────
step "3/5  Preparing app icon"
mkdir -p "$DESKTOP_DIR/build"
ICON_OUT="$DESKTOP_DIR/build/icon.icns"

# Build an .icns from any square source art we can find (png / ico / jpg).
make_icns() {
  local src="$1" tmp_png
  tmp_png="$(mktemp -t chicon).png"
  sips -s format png "$src" --out "$tmp_png" >/dev/null 2>&1 || { rm -f "$tmp_png"; return 1; }
  local iconset; iconset="$(mktemp -d)/AppIcon.iconset"; mkdir -p "$iconset"
  local size dbl
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$tmp_png" --out "$iconset/icon_${size}x${size}.png" >/dev/null 2>&1 || true
    dbl=$((size * 2))
    sips -z "$dbl" "$dbl" "$tmp_png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null 2>&1 || true
  done
  iconutil -c icns "$iconset" -o "$ICON_OUT" >/dev/null 2>&1
  local rc=$?
  rm -rf "$(dirname "$iconset")" "$tmp_png"
  return $rc
}

if [[ -f "$ROOT/wallet.icns" ]]; then
  cp "$ROOT/wallet.icns" "$ICON_OUT" && ok "Using wallet.icns"
else
  ICON_SRC=""
  for candidate in "$ROOT/wallet.png" "$ROOT/wallet.ico" "$ROOT/wallet.jpg" "$ROOT/icon.png"; do
    [[ -f "$candidate" ]] && { ICON_SRC="$candidate"; break; }
  done
  if [[ -n "$ICON_SRC" ]] && make_icns "$ICON_SRC"; then
    ok "Generated icon.icns from $(basename "$ICON_SRC")"
  else
    rm -f "$ICON_OUT"
    warn "No usable app artwork — macOS will use the default Electron icon"
  fi
fi
# electron-builder picks up build/icon.icns automatically when it exists.

# ── Build ───────────────────────────────────────────────────────────────────
step "4/5  Building $APP_NAME.app"
cd "$DESKTOP_DIR" || exit 1
if [[ ! -d node_modules/electron || ! -x node_modules/.bin/electron-builder ]]; then
  printf '%sDownloading the Electron runtime + builder (one time, ~150 MB)…%s\n' "$DIM" "$RESET"
  npm install --no-audit --no-fund --loglevel=error || { fail "Electron install failed — check your connection"; exit 1; }
fi

BUILD_FLAGS=(--mac --dir --publish never)
if [[ "$BUILD_DMG" -eq 1 ]]; then
  BUILD_FLAGS=(--mac --publish never)
  printf '%sAlso producing a .dmg…%s\n' "$DIM" "$RESET"
fi

BUILDER="./node_modules/.bin/electron-builder"
[[ -x "$BUILDER" ]] || BUILDER="npx --yes electron-builder"
if ! "$BUILDER" "${BUILD_FLAGS[@]}"; then
  fail "electron-builder failed"
  exit 1
fi

BUILT_APP="$(find "$OUT_DIR" -maxdepth 3 -name "$APP_NAME.app" -type d 2>/dev/null | head -1)"
[[ -n "$BUILT_APP" ]] || { fail "Build finished but no .app was produced in $OUT_DIR"; exit 1; }
ok "Built: $BUILT_APP"

# Sanity check the bundle really carries the workstation
[[ -f "$BUILT_APP/Contents/Resources/workstation/server.js" ]] \
  && ok "Bundle contains the workstation server" \
  || warn "Bundle is missing Resources/workstation/server.js"

# Apple Silicon refuses to launch an unsigned, modified .app bundle, so apply an
# ad-hoc signature when electron-builder skipped signing (it does when there is
# no Developer ID). This is free, local and takes a second.
if ! codesign -dv "$BUILT_APP" >/dev/null 2>&1; then
  if codesign --force --deep --sign - "$BUILT_APP" >/dev/null 2>&1; then
    ok "Applied an ad-hoc signature (no Developer ID needed for your own Mac)"
  else
    warn "Could not ad-hoc sign the app — if it refuses to open, run: codesign --force --deep --sign - \"$BUILT_APP\""
  fi
else
  ok "App is already signed"
fi

# ── Install ─────────────────────────────────────────────────────────────────
step "5/5  Installing"
if [[ "$DO_INSTALL" -eq 1 ]]; then
  if [[ "$ASSUME_YES" -eq 0 ]]; then
    read -r -p "Install to /Applications? [Y/n] " ans
    [[ "${ans:-y}" =~ ^[Nn]$ ]] && DO_INSTALL=0
  fi
fi

TARGET="/Applications/$APP_NAME.app"
if [[ "$DO_INSTALL" -eq 1 ]]; then
  rm -rf "$TARGET"
  if cp -R "$BUILT_APP" "$TARGET"; then
    xattr -cr "$TARGET" 2>/dev/null || true
    ok "Installed to $TARGET"
  else
    warn "Could not write to /Applications — drag the app there from: $BUILT_APP"
    TARGET="$BUILT_APP"
  fi
else
  TARGET="$BUILT_APP"
fi

# Login item (LaunchAgent) — keeps the scanner running after a reboot
if [[ "$DO_LOGIN" -eq 1 ]]; then
  ENABLE_LOGIN=1
  if [[ "$ASSUME_YES" -eq 0 ]]; then
    read -r -p "Start automatically at login (runs hidden in the menu bar)? [Y/n] " auto
    [[ "${auto:-y}" =~ ^[Nn]$ ]] && ENABLE_LOGIN=0
  fi
  if [[ "$ENABLE_LOGIN" -eq 1 ]]; then
    bash "$SCRIPT_DIR/toggle-login-item.sh" enable "$TARGET" >/dev/null 2>&1 \
      && ok "Login item enabled — the scanner starts hidden at every login" \
      || warn "Could not register the login item (you can enable it in the app: menu bar → Launch at Login)"
  fi
fi

step "Done"
printf 'App:    %s\n' "$TARGET"
printf 'Data:   %s\n' "$HOME/Library/Application Support/CodeHaven Workstation"
printf 'Logs:   %s\n' "$HOME/Library/Logs/CodeHaven"
printf '\n%sOpen it from Launchpad, or:%s\n  open "%s"\n' "$DIM" "$RESET" "$TARGET"
printf '\n%sThe scanner binds to 127.0.0.1 only. Nothing is exposed to your network.%s\n' "$DIM" "$RESET"
printf '%sIt lives in the menu bar: quit with ⌘Q or the shield icon; reopen from Launchpad.%s\n' "$DIM" "$RESET"
printf '%sFirst launch after a download: right-click the app → Open (Gatekeeper).%s\n' "$DIM" "$RESET"
