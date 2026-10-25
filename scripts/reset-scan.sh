#!/usr/bin/env bash
# ============================================================================
#  CodeHaven — FULL SCAN RESET ("start fresh")
#
#  Resets EVERY place the scanner keeps memory:
#    1. the Terminal copy   → <repo>/WebWorkstation
#    2. the desktop app     → ~/Library/Application Support/CodeHaven Workstation
#    3. CODEHAVEN_DATA_DIR  → wherever you pointed it (if set)
#
#  For each home it moves (never deletes) the state into
#  StateArchive/<timestamp>/<home-name>/ : progress, queues, dedupe memory,
#  found/ever-held vaults, and the OLD funded-address bloom — so a NEW bloom
#  can be imported and the counters start from zero.
#
#  KEPT SAFE (never moved):
#    - api_keys_config.json            your API keys
#    - smtp_config.json / smtp.local.json / crash.log / capture/
#
#  Usage:
#    bash scripts/reset-scan.sh
#    …or double-click scripts/macos/reset-scan.command
#
#  One command is enough: it politely stops a running scanner (the desktop
#  app, the start-sturdy restart loop, and anything on the scan port), then
#  does the reset.
# ============================================================================
set -u

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
ARCHIVE_DIR="$REPO_DIR/StateArchive/$STAMP"
SCAN_PORT="${CODEHAVEN_PORT:-2525}"

echo "=========================================================="
echo "  CodeHaven — full scan reset"
echo "=========================================================="

# ---- the scanner homes (any that exist get reset) ---------------------------
HOMES=()
[ -n "${CODEHAVEN_DATA_DIR:-}" ] && HOMES+=("$CODEHAVEN_DATA_DIR")
HOMES+=("$REPO_DIR/WebWorkstation")
if [ "$(uname)" = "Darwin" ]; then
  HOMES+=("$HOME/Library/Application Support/CodeHaven Workstation")
elif [ -n "${APPDATA:-}" ]; then
  HOMES+=("$APPDATA/CodeHaven Workstation")
else
  HOMES+=("${XDG_DATA_HOME:-$HOME/.local/share}/codehaven-workstation")
fi

echo "Scanner memory locations checked:"
for h in "${HOMES[@]}"; do
  [ -d "$h" ] && echo "  • $h" || echo "  • $h  (not present yet)"
done
echo

# ---- 1. stop the scanner politely (so the reset is safe) --------------------
port_pids() {
  if command -v lsof >/dev/null 2>&1; then
    lsof -ti "TCP:$SCAN_PORT" -sTCP:LISTEN 2>/dev/null || true
  elif command -v ss >/dev/null 2>&1; then
    ss -ltnp 2>/dev/null | awk "/:$SCAN_PORT /" | grep -oP 'pid=\K[0-9]+' | sort -u || true
  fi
}

if [ -n "$(port_pids)" ]; then
  echo "Stopping the running scanner first…"
  # (a) quit the desktop app if it's open (macOS)
  if [ "$(uname)" = "Darwin" ] && command -v osascript >/dev/null 2>&1; then
    osascript -e 'quit application "CodeHaven Workstation"' >/dev/null 2>&1 && \
      echo "  ↪ quit the CodeHaven app" && sleep 2
  fi
  # (b) stop the start-sturdy restart loop BEFORE the server (it would restart it)
  if command -v pgrep >/dev/null 2>&1; then
    for pid in $(pgrep -f 'start-sturdy\.command$' 2>/dev/null); do
      [ "$pid" != "$$" ] && kill "$pid" 2>/dev/null && echo "  ↪ stopped the restart loop (pid $pid)"
    done
    sleep 1
  fi
  # (c) stop whatever is listening on the scan port (SIGTERM, then SIGKILL)
  for _try in 1 2 3 4 5; do
    pids="$(port_pids)"
    [ -z "$pids" ] && break
    for pid in $pids; do
      kill "$pid" 2>/dev/null && echo "  ↪ stopping scanner (pid $pid)…"
    done
    sleep 2
  done
  for pid in $(port_pids); do
    kill -9 "$pid" 2>/dev/null && echo "  ↪ force-stopped scanner (pid $pid)"
  done
  sleep 1
  if [ -n "$(port_pids)" ]; then
    echo "❌ Could not stop the scanner (port $SCAN_PORT is still open)."
    echo "   Quit the CodeHaven app by hand (tray icon → Quit), or press Ctrl+C"
    echo "   in the Terminal window running start-sturdy, then run this again."
    exit 1
  fi
  echo "Scanner stopped. Continuing with the reset…"
  echo
fi

# ---- 2. archive + clear each home (move, never destroy) --------------------
echo "Archiving old state (nothing is deleted, only moved)…"
move_if_there() {
  home="$1"; shift
  for item in "$@"; do
    if [ -e "$home/$item" ]; then
      mkdir -p "$dest"
      mv "$home/$item" "$dest/" 2>/dev/null && echo "  ↪ archived: $label/$item"
    fi
  done
}

for home in "${HOMES[@]}"; do
  [ -d "$home" ] || continue
  label="$(basename "$home")"
  # keep the two homes from colliding in the archive (both can be named oddly)
  case "$home" in
    "$REPO_DIR/WebWorkstation") dest="$ARCHIVE_DIR/WebWorkstation" ;;
    *) dest="$ARCHIVE_DIR/AppData-$(echo "$home" | tr -c 'A-Za-z0-9._-' '_')" ;;
  esac
  # whole state dirs
  move_if_there "$home" PersistentState .runtime_state Results
  # progress / queues / dedupe memory
  move_if_there "$home" scanned_empty_wallets.json scanned_bloom_100m.bin scan_checkpoint.json
  move_if_there "$home" cognitive_brain_state.json cognitive_brain_state.json.corrupt
  move_if_there "$home" target_addresses.json
  # found / ever-held vaults
  move_if_there "$home" FOUND_WALLETS_VAULT.json FOUND_WALLETS.txt
  # the OLD funded-address bloom + dump imports (a NEW bloom replaces these)
  move_if_there "$home" funded_addresses_bloom.bin funded_addresses_meta.json funded_bloom_meta.json
  move_if_there "$home" funded_addresses.json funded_wallets.txt funded_addresses.sqlite3
  move_if_there "$home" funded_dump.jsonl.gz funded-address-dump.safetensors
  # session summaries / market caches / stray queues if any sit at the root
  for f in "$home"/SummarySession*.json "$home"/HistorySamples*.json \
           "$home"/market_price*.json "$home"/ratesLog*.jsonl \
           "$home"/seen_inputs* "$home"/pending_batches*.json \
           "$home"/batches.json "$home"/found.jsonl "$home"/pending_manual.jsonl; do
    [ -e "$f" ] && mkdir -p "$dest" && mv "$f" "$dest/" 2>/dev/null && echo "  ↪ archived: $label/$(basename "$f")"
  done
done

echo
echo "✅ Reset complete. Every copy of the scanner memory is factory-fresh:"
echo "   • 0 wallets checked · 0 found · 0 ever-held · empty queues"
echo "   • no dedupe memory (every input is new again)"
echo "   • no funded-address bloom (waiting for your NEW one)"
echo
echo "Old state is safe in:  $ARCHIVE_DIR"
echo
echo "Next steps:"
echo "  1. Open the CodeHaven app (double-click it) — it will start at zero."
echo "     If the window doesn't appear, look for the CodeHaven icon in the"
echo "     top menu bar and choose Open."
echo "  2. Open the 'Bloom Importer' tab and stream in your new funded-address"
echo "     dump (the .gz / address list) — it builds the fresh bloom."
echo "  3. Press Resume. The counters start from zero with the new data."
echo "=========================================================="
