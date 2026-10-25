#!/bin/bash
# Double-clickable fresh start — just runs scripts/reset-scan.sh and keeps
# the window open so you can read the result.
cd "$(dirname "$0")/../.." || exit 1
bash scripts/reset-scan.sh
echo
read -n 1 -s -r -p "Press any key to close this window…"
echo
