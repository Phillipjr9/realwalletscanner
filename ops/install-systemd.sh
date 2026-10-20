#!/usr/bin/env bash
# install-systemd.sh - run a Collision Protocol pool worker as a restarted service.
#
# Usage (as root):
#   sudo ./install-systemd.sh --worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP \
#                             --binary /path/to/collider
#
# What it does NOT do: download anything. Put the release tarball/binary on the
# box yourself (verify the hash against the GitHub release page), extract it,
# and point --binary at it. A tool that fetches and runs a binary as root on
# your behalf is how miners get backdoored.
set -euo pipefail

WORKER=""
BINARY=""
POOL_URL="${COLLIDER_POOL_URL:-jlps://pool.collisionprotocol.com:17403}"

die() { echo "ERROR: $*" >&2; exit 1; }
say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --worker) WORKER="${2:-}"; shift 2 ;;
    --binary) BINARY="${2:-}"; shift 2 ;;
    --url)    POOL_URL="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "run with sudo"
[[ -n "$WORKER" ]] || die "--worker <btc-address> is required"
[[ -n "$BINARY" && -f "$BINARY" ]] || die "--binary <path to collider> is required and must exist"

say "Validating payout address"
if command -v python3 >/dev/null 2>&1 && [[ -f "$(dirname "$0")/validate-worker.py" ]]; then
  python3 "$(dirname "$0")/validate-worker.py" "$WORKER" || die "address failed validation - refusing to install"
else
  echo "  (python3 or validate-worker.py missing - skipping checksum check)"
  echo "  Address on record: $WORKER"
fi

say "Checking host prerequisites"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi --query-gpu=index,name,driver_version --format=csv,noheader | sed 's/^/  GPU /'
else
  echo "  WARNING: nvidia-smi not found. The client needs an NVIDIA GPU + CUDA 12 driver."
fi
[[ -d /etc/ssl/certs ]] || echo "  WARNING: no /etc/ssl/certs - install ca-certificates or jlps:// TLS will fail at init."
if command -v timedatectl >/dev/null 2>&1; then
  timedatectl show -p NTPSynchronized --value | grep -qx yes \
    || echo "  WARNING: clock not NTP-synced. AUTH rejects >30s drift. Run: timedatectl set-ntp true"
fi

say "Installing binary"
id -u collider >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin collider
install -d -m 0755 /opt/collider
install -m 0755 "$BINARY" /opt/collider/collider
# Ship the sibling data/ dir if the release tarball came with one (puzzle_history.json).
if [[ -d "$(dirname "$BINARY")/data" ]]; then
  cp -r "$(dirname "$BINARY")/data" /opt/collider/
fi
chown -R root:root /opt/collider

say "Writing worker environment"
install -d -m 0750 /etc/collider
install -m 0640 -o root -g collider /dev/null /etc/collider/collider.env
cat > /etc/collider/collider.env <<EOF
# Payout identity. Every DP is credited to this string; payout lands here.
# The pool does not checksum it. Back up the wallet that controls it.
COLLIDER_WORKER=$WORKER
COLLIDER_POOL_URL=$POOL_URL
EOF

say "Installing unit"
install -m 0644 "$(dirname "$0")/collider-pool.service" /etc/systemd/system/collider-pool.service
systemctl daemon-reload
systemctl enable --now collider-pool.service

sleep 5
say "Status"
systemctl --no-pager --lines=0 status collider-pool.service || true
echo
echo "Last lines:"
journalctl -u collider-pool.service -n 20 --no-pager || true
cat <<'EOF'

Next:
  journalctl -u collider-pool.service -f          # watch it work
  systemctl stop collider-pool.service            # graceful stop (flushes queued DPs)
  systemctl restart collider-pool.service

Confirm the dashboard moved:
  https://collisionprotocol.com/dashboard?worker=<your address>
EOF
