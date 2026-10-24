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
step "1/6  Checking Node.js"
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
step "2/6  Installing scanner dependencies"
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
step "3/6  Preparing app icon"
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

# ── Bundled Node runtime ────────────────────────────────────────────────────
# Electron's BoringSSL has no secp256k1, so `crypto.createECDH('secp256k1')`
# (used to derive every public key) fails inside the Electron binary. Ship an
# official Node runtime and run the scanner with that instead.
step "4/6  Bundling the Node.js runtime"
NODE_ARCH="$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )"
NODE_BUNDLE_DIR="$DESKTOP_DIR/build/node"
BUNDLED_NODE="$NODE_BUNDLE_DIR/bin/node"

if [[ -x "$BUNDLED_NODE" ]] && "$BUNDLED_NODE" -e "require('crypto').createECDH('secp256k1')" >/dev/null 2>&1; then
  ok "Node runtime already bundled ($("$BUNDLED_NODE" -v), $NODE_ARCH)"
else
  VERSIONS=()
  [[ -n "${CODEHAVEN_NODE_VERSION:-}" ]] && VERSIONS+=("${CODEHAVEN_NODE_VERSION#v}")
  printf '%sLooking up the current Node LTS…%s\n' "$DIM" "$RESET"
  LATEST_LTS="$(curl -fsSL --max-time 15 https://nodejs.org/dist/index.json 2>/dev/null \
    | "$NODE_BIN" -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{try{const r=JSON.parse(d).find(x=>x.lts);process.stdout.write(r.version.replace(/^v/,""))}catch(e){}}) ' 2>/dev/null || true)"
  [[ -n "$LATEST_LTS" ]] && VERSIONS+=("$LATEST_LTS")
  VERSIONS+=(22.14.0 22.11.0 20.18.3 18.20.4)

  for v in "${VERSIONS[@]}"; do
    URL="https://nodejs.org/dist/v$v/node-v$v-darwin-$NODE_ARCH.tar.gz"
    TMP_TAR="$(mktemp "${TMPDIR:-/tmp}/chnode-tar.XXXXXX")"
    printf '%sDownloading Node %s for %s (one time, ~45 MB)…%s\n' "$DIM" "$v" "$NODE_ARCH" "$RESET"
    if curl -fL --retry 2 --max-time 600 -o "$TMP_TAR" "$URL" >/dev/null 2>&1; then
      TMP_EXTRACT="$(mktemp -d "${TMPDIR:-/tmp}/chnode-dir.XXXXXX")"
      if tar -xzf "$TMP_TAR" -C "$TMP_EXTRACT" "node-v$v-darwin-$NODE_ARCH/bin/node" >/dev/null 2>&1; then
        mkdir -p "$NODE_BUNDLE_DIR/bin"
        cp "$TMP_EXTRACT/node-v$v-darwin-$NODE_ARCH/bin/node" "$BUNDLED_NODE"
        chmod +x "$BUNDLED_NODE"
        tar -xzf "$TMP_TAR" -C "$TMP_EXTRACT" "node-v$v-darwin-$NODE_ARCH/LICENSE" >/dev/null 2>&1 \
          && cp "$TMP_EXTRACT/node-v$v-darwin-$NODE_ARCH/LICENSE" "$NODE_BUNDLE_DIR/LICENSE" 2>/dev/null
        rm -rf "$TMP_EXTRACT" "$TMP_TAR"
        ok "Bundled Node $v ($NODE_ARCH)"
        break
      fi
      rm -rf "$TMP_EXTRACT"
    fi
    rm -f "$TMP_TAR"
    warn "Could not fetch Node $v — trying the next version"
  done

  if [[ ! -x "$BUNDLED_NODE" && -x "$NODE_BIN" ]]; then
    # Last resort: reuse the Node that is building the app. It works, but the
    # resulting bundle is only self-contained if that binary has no extra deps.
    mkdir -p "$NODE_BUNDLE_DIR/bin"
    if cp "$NODE_BIN" "$BUNDLED_NODE" 2>/dev/null; then
      chmod +x "$BUNDLED_NODE"
      warn "Download failed — copied your local Node ($NODE_BIN) instead"
    fi
  fi

  if [[ ! -x "$BUNDLED_NODE" ]]; then
    fail "Could not obtain a Node runtime. Set CODEHAVEN_NODE_VERSION=<version> and retry."
    exit 1
  fi
fi

if "$BUNDLED_NODE" -e "require('crypto').createECDH('secp256k1')" >/dev/null 2>&1; then
  ok "Bundled runtime supports secp256k1 ($("$BUNDLED_NODE" -v))"
else
  fail "The bundled Node cannot create secp256k1 keys — the scanner would not start"
  exit 1
fi

# ── Build ───────────────────────────────────────────────────────────────────
step "5/6  Building $APP_NAME.app"
cd "$DESKTOP_DIR" || exit 1
if [[ ! -d node_modules/electron || ! -x node_modules/.bin/electron-builder ]]; then
  printf '%sDownloading the Electron runtime + builder (one time, ~150 MB)…%s\n' "$DIM" "$RESET"
  npm install --no-audit --no-fund --loglevel=error || { fail "Electron install failed — check your connection"; exit 1; }
fi

BUILD_FLAGS=(--mac "--$NODE_ARCH" --publish never)
[[ "$BUILD_DMG" -eq 0 ]] && BUILD_FLAGS+=(--dir)
[[ "$BUILD_DMG" -eq 1 ]] && printf '%sAlso producing a .dmg…%s\n' "$DIM" "$RESET"

BUILD_EPOCH="$(date +%s)"
BUILDER="./node_modules/.bin/electron-builder"
[[ -x "$BUILDER" ]] || BUILDER="npx --yes electron-builder"
if ! "$BUILDER" "${BUILD_FLAGS[@]}"; then
  fail "electron-builder failed"
  exit 1
fi

# electron-builder writes to dist/mac-<arch>/ (or dist/mac/). Pick the app THIS
# build just produced — never a leftover from an earlier run — preferring the
# architecture we asked for.
BUILT_APP=""
BEST_SCORE=0
while IFS= read -r cand; do
  [[ -d "$cand" ]] || continue
  mtime="$(stat -f %m "$cand" 2>/dev/null || echo 0)"
  score="$mtime"
  case "$cand" in
    *"/mac-$NODE_ARCH/"*) score=$((mtime + 100000000000)) ;;
  esac
  # Ignore anything older than this build (stale leftovers from previous runs).
  [[ "$mtime" -ge "$((BUILD_EPOCH - 300))" ]] || score=0
  if [[ "$score" -gt "$BEST_SCORE" ]]; then BEST_SCORE="$score"; BUILT_APP="$cand"; fi
done < <(find "$OUT_DIR" -maxdepth 3 -name "$APP_NAME.app" -type d 2>/dev/null)

if [[ -z "$BUILT_APP" || "$BEST_SCORE" -eq 0 ]]; then
  fail "Build finished but no fresh .app was produced in $OUT_DIR"
  exit 1
fi
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
step "6/6  Installing"
if [[ "$DO_INSTALL" -eq 1 ]]; then
  if [[ "$ASSUME_YES" -eq 0 ]]; then
    read -r -p "Install to /Applications? [Y/n] " ans
    [[ "${ans:-y}" =~ ^[Nn]$ ]] && DO_INSTALL=0
  fi
fi

TARGET="/Applications/$APP_NAME.app"
if [[ "$DO_INSTALL" -eq 1 ]]; then
  # Replacing a running .app confuses macOS (and keeps the old scanner on the port),
  # so quit the current copy first.
  if pgrep -f "$APP_NAME.app/Contents/MacOS" >/dev/null 2>&1; then
    printf '%sQuitting the copy that is already running…%s\n' "$DIM" "$RESET"
    osascript -e "tell application \"$APP_NAME\" to quit" >/dev/null 2>&1 || true
    sleep 2
    pkill -f "$APP_NAME.app/Contents/MacOS" >/dev/null 2>&1 || true
    sleep 1
  fi
  if [[ -d "$TARGET" ]]; then
    OLD_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$TARGET/Contents/Info.plist" 2>/dev/null || true)"
    if [[ "$OLD_ID" == "app.codehaven.workstation.lite" ]]; then
      warn "Replacing the lightweight browser launcher (an older build) with the standalone app"
    fi
  fi
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

# Prove the installed bundle can actually run the scanner's crypto engine
BUNDLED_RUNTIME="$TARGET/Contents/Resources/node/bin/node"
if [[ -x "$BUNDLED_RUNTIME" && -d "$TARGET/Contents/Resources/workstation" ]]; then
  if ( cd "$TARGET/Contents/Resources/workstation" && "$BUNDLED_RUNTIME" -e "require('./crypto_engine.js')" >/dev/null 2>&1 ); then
    ok "Installed bundle verified: crypto engine loads under the bundled Node"
  else
    warn "The installed bundle failed its self-check — open the app and read the diagnostics log"
  fi

  # Boot smoke test: the installed scanner must start and answer over HTTP.
  SMOKE_PORT="$("$BUNDLED_RUNTIME" -e "const n=require('net');const s=n.createServer();s.listen(0,'127.0.0.1',()=>{process.stdout.write(String(s.address().port));s.close();});" 2>/dev/null || true)"
  SMOKE_DATA="$(mktemp -d "${TMPDIR:-/tmp}/chsmoke.XXXXXX")"
  if [[ -n "$SMOKE_PORT" && -d "$SMOKE_DATA" ]]; then
    (
      cd "$TARGET/Contents/Resources/workstation" || exit 1
      export CODEHAVEN_DATA_DIR="$SMOKE_DATA" PORT="$SMOKE_PORT" HOST=127.0.0.1 LOCAL_SMTP_PORT="$((SMOKE_PORT + 1))"
      exec "$BUNDLED_RUNTIME" server.js > "$SMOKE_DATA/boot.log" 2>&1
    ) &
    SMOKE_PID=$!
    SMOKE_OK=0
    for _ in $(seq 1 40); do
      if curl -fsS --max-time 2 "http://127.0.0.1:$SMOKE_PORT/" >/dev/null 2>&1; then SMOKE_OK=1; break; fi
      sleep 0.5
    done
    kill "$SMOKE_PID" 2>/dev/null || true
    sleep 1
    kill -9 "$SMOKE_PID" 2>/dev/null || true
    if [[ "$SMOKE_OK" -eq 1 ]]; then
      ok "Installed bundle boot-tested: the scanner started and answered on 127.0.0.1:$SMOKE_PORT"
    else
      warn "The installed scanner did not answer within 20 s. Boot log:"
      tail -8 "$SMOKE_DATA/boot.log" 2>/dev/null || true
    fi
    rm -rf "$SMOKE_DATA"
  fi
else
  warn "No bundled Node runtime in the installed app — the scanner will fall back to a system Node"
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
