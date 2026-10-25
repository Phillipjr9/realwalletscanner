#!/usr/bin/env bash
# ============================================================================
#  CodeHaven — FULL SCAN RESET ("start fresh")
#  Archives the old state to StateArchive/<timestamp>/ and clears everything the
#  scanner has learned: progress, queues, dedupe memory, found vaults, and the
#  OLD funded-address bloom (so a NEW bloom can be imported).
#
#  KEPT SAFE (never deleted):
#    - api_keys_config.json        your API keys
#    - smtp_config.json / smtp.local.json / smtp_alerts_outbox kept in archive
#    - capture/                    your alert evidence
#    - crash.log                   diagnostics
#
#  Usage:  bash scripts/reset-scan.sh
#  One command is enough: it politely stops a running scanner (the desktop
#  app, the start-sturdy restart loop, and anything on the scan port), then
#  does the reset.
# ============================================================================
set -u

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WORK_DIR="${CODEHAVEN_DATA_DIR:-$REPO_DIR/WebWorkstation}"
STAMP="$(date +%Y%m%d-%H%M%S)"
ARCHIVE_DIR="$REPO_DIR/StateArchive/$STAMP"
SCAN_PORT="${CODEHAVEN_PORT:-2525}"

echo "=========================================================="
echo "  CodeHaven — full scan reset"
echo "=========================================================="
echo "Scanner folder: $WORK_DIR"
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

# ---- 2. archive (move, never destroy) --------------------------------------
mkdir -p "$ARCHIVE_DIR"
move_if_there() {
  for item in "$@"; do
    if [ -e "$WORK_DIR/$item" ]; then
      mv "$WORK_DIR/$item" "$ARCHIVE_DIR/" 2>/dev/null && echo "  ↪ archived: $item"
    fi
  done
}

echo "Archiving old state (nothing is deleted, only moved)…"
# whole state dirs
move_if_there PersistentState .runtime_state Results
# progress / queues / dedupe memory
move_if_there scanned_empty_wallets.json scanned_bloom_100m.bin scan_checkpoint.json
move_if_there cognitive_brain_state.json cognitive_brain_state.json.corrupt
move_if_there target_addresses.json
# found / ever-held vaults
move_if_there FOUND_WALLETS_VAULT.json FOUND_WALLETS.txt
# the OLD funded-address bloom + dump imports (a NEW bloom replaces these)
move_if_there funded_addresses_bloom.bin funded_addresses_meta.json funded_bloom_meta.json
move_if_there funded_addresses.json funded_wallets.txt funded_addresses.sqlite3
move_if_there funded_dump.jsonl.gz funded-address-dump.safetensors
# session summaries / market caches if any sit at the root
for f in "$WORK_DIR"/SummarySession*.json "$WORK_DIR"/HistorySamples*.json \
         "$WORK_DIR"/market_price*.json "$WORK_DIR"/ratesLog*.jsonl \
         "$WORK_DIR"/seen_inputs* "$WORK_DIR"/pending_batches*.json \
         "$WORK_DIR"/batches.json "$WORK_DIR"/found.jsonl "$WORK_DIR"/pending_manual.jsonl; do
  [ -e "$f" ] && mv "$f" "$ARCHIVE_DIR/" 2>/dev/null && echo "  ↪ archived: $(basename "$f")"
done

echo
echo "✅ Reset complete. The scanner memory is now factory-fresh:"
echo "   • 0 wallets checked · 0 found · 0 ever-held · empty queues"
echo "   • no dedupe memory (every input is new again)"
echo "   • no funded-address bloom (waiting for your NEW one)"
echo
echo "Old state is safe in:  $ARCHIVE_DIR"
echo
echo "Next steps:"
echo "  1. Start the scanner (double-click CodeHaven Workstation, or"
echo "     bash scripts/macos/start-sturdy.command)"
echo "  2. Open the 'Bloom Importer' tab and stream in your new funded-address"
echo "     dump (the .gz / address list) — it builds the fresh bloom."
echo "  3. Press Resume. The counters start from zero with the new data."
echo "=========================================================="
