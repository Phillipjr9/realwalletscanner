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
#  Stop the scanner / quit the app first — this script refuses to run while
#  the scanner is listening on its port.
# ============================================================================
set -u

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WORK_DIR="${CODEHAVEN_DATA_DIR:-$REPO_DIR/WebWorkstation}"
STAMP="$(date +%Y%m%d-%H%M%S)"
ARCHIVE_DIR="$REPO_DIR/StateArchive/$STAMP"

echo "=========================================================="
echo "  CodeHaven — full scan reset"
echo "=========================================================="
echo "Scanner folder: $WORK_DIR"
echo

# ---- 1. refuse to run while the scanner is up -------------------------------
if command -v lsof >/dev/null 2>&1; then
  if lsof -ti TCP:2525 -sTCP:LISTEN >/dev/null 2>&1; then
    echo "❌ The scanner is still running (port 2525 is open)."
    echo "   Quit the CodeHaven app (tray icon → Quit) or press Ctrl+C in the"
    echo "   Terminal window running start-sturdy, then run this again."
    exit 1
  fi
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
