#!/bin/bash
# /usr/local/bin/collider-pool - wrapper that launchd starts.
#
# Why a wrapper instead of putting collider in the plist directly:
#   - launchd has no EnvironmentFile, so the worker address cannot live in an
#     env file unless something sources it.
#   - We want caffeinate in the process tree so an assertion is held for the
#     entire lifetime of the worker, not just until someone closes a terminal.
#
# caffeinate flags:
#   -i  prevent idle system sleep      (works on battery, always needed)
#   -s  prevent system sleep           (AC only; silently ignored on battery)
#   NOT -d: the display is allowed to sleep. Blanking the screen saves power and
#   heat and does not stop the worker. Use -d only if you need to watch the TUI.
set -euo pipefail

ENV_FILE="${COLLIDER_ENV:-/usr/local/etc/collider/collider.env}"
if [ -r "$ENV_FILE" ]; then
    # shellcheck disable=SC1090
    . "$ENV_FILE"
fi

: "${COLLIDER_WORKER:?COLLIDER_WORKER is not set - see $ENV_FILE}"

POOL_URL="${COLLIDER_POOL_URL:-jlps://pool.collisionprotocol.com:17403}"
BIN="${COLLIDER_BIN:-/usr/local/collider/collider}"

[ -x "$BIN" ] || { echo "collider-pool: $BIN is not executable" >&2; exit 78; }

# shellcheck disable=SC2086  # COLLIDER_EXTRA_ARGS is intentionally word-split
exec /usr/bin/caffeinate -i -s "$BIN" \
    --pool "$POOL_URL" \
    --worker "$COLLIDER_WORKER" \
    --no-tui \
    --verbose \
    ${COLLIDER_EXTRA_ARGS:-}
