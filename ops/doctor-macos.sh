#!/bin/bash
# doctor-macos.sh - diagnose "Failed to connect to pool" and general worker
# health on macOS, without touching the pool's state.
#
#   ./ops/doctor-macos.sh                       # default pool endpoint
#   ./ops/doctor-macos.sh --binary ~/puzzle/collider
#
# Read-only: no DPs submitted, no config written, nothing needs sudo.
set -uo pipefail

HOST="pool.collisionprotocol.com"
PORT="17403"
BINARY=""
WORKER=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --host)   HOST="${2:-}"; shift 2 ;;
        --port)   PORT="${2:-}"; shift 2 ;;
        --binary) BINARY="${2:-}"; shift 2 ;;
        --worker) WORKER="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

ok()   { printf '  \033[32mPASS\033[0m  %s\n' "$*"; }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAILED=$((FAILED+1)); }
warn() { printf '  \033[33mWARN\033[0m  %s\n' "$*"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$*"; }
FAILED=0

# perl gives us a portable timeout (macOS has no timeout(1) by default).
tmo() { perl -e 'alarm shift; exec @ARGV' "$@"; }

head_ "1. The single most common cause"
cat <<'EOF'
  The client treats a bare host:port as PLAINTEXT. Only "jlps://" enables TLS,
  and port 17403 speaks TLS. From src/pool/pool_manager.cpp::parse_pool_url:
      if (scheme.empty() || scheme == "jlp") use_tls = false;
  A plaintext connection to a TLS port gets reset, which the client reports as
  "Failed to connect to pool".

  Use this exact form:
      --pool jlps://pool.collisionprotocol.com:17403
EOF

head_ "2. DNS resolution"
A_REC=$(tmo 5 dig +short A "$HOST" 2>/dev/null | tail -1)
AAAA_REC=$(tmo 5 dig +short AAAA "$HOST" 2>/dev/null | tail -1)
if [[ -n "$A_REC" ]]; then ok "A     $HOST -> $A_REC"; else bad "no A record for $HOST (DNS or offline)"; fi
if [[ -n "$AAAA_REC" ]]; then warn "AAAA  $HOST -> $AAAA_REC (may be IPv6-only reachable)"; else echo "        no AAAA record (IPv4 only - fine)"; fi

head_ "3. TCP reachability on port $PORT"
if tmo 8 bash -c "nc -4 -z -G 5 '$HOST' '$PORT'" 2>/dev/null; then
    ok "IPv4 TCP connect to $HOST:$PORT"
else
    bad "IPv4 TCP connect to $HOST:$PORT"
    echo "        Try: nc -vz -4 $HOST $PORT"
    echo "        A hang usually means a firewall/VPN is dropping (not rejecting)."
fi
if [[ -n "$AAAA_REC" ]]; then
    if tmo 8 bash -c "nc -6 -z -G 5 '$HOST' '$PORT'" 2>/dev/null; then
        ok "IPv6 TCP connect"
    else
        warn "IPv6 TCP connect failed (harmless if IPv4 works; macOS may prefer v6)"
    fi
fi

head_ "4. Does the port actually speak TLS?"
TLS_OUT=$(tmo 10 bash -c "echo | openssl s_client -connect '$HOST:$PORT' -servername '$HOST' 2>&1 | head -25")
if echo "$TLS_OUT" | grep -q "BEGIN CERTIFICATE"; then
    SUBJ=$(echo "$TLS_OUT" | grep -m1 "subject=" || true)
    ok "TLS handshake succeeded  ${SUBJ}"
    echo "        This confirms the endpoint is TLS-only -> you must use jlps://"
elif echo "$TLS_OUT" | grep -qi "connect: connection refused\|Connection refused"; then
    bad "connection refused - the listener is down or firewalled"
elif echo "$TLS_OUT" | grep -qi "certificate verify"; then
    warn "TLS spoke but cert verification complained (system CA issue)"
    echo "        Check: /etc/ssl/cert.pem exists; try again after a reboot."
else
    bad "no TLS response"
    echo "$TLS_OUT" | sed 's/^/        /' | head -8
fi

head_ "5. Clock skew (AUTH rejects > 30s)"
LOCAL_EPOCH=$(date -u +%s)
REMOTE_DATE=$(tmo 8 curl -sI "https://collisionprotocol.com" 2>/dev/null | grep -i '^date:' | head -1 | cut -d' ' -f2-)
if [[ -n "$REMOTE_DATE" ]]; then
    REMOTE_EPOCH=$(date -u -j -f "%a, %d %b %Y %T GMT" "$REMOTE_DATE" +%s 2>/dev/null)
    if [[ -n "$REMOTE_EPOCH" ]]; then
        SKEW=$(( LOCAL_EPOCH - REMOTE_EPOCH ))
        ABS=${SKEW#-}
        if (( ABS <= 5 )); then ok "skew ${SKEW}s"
        elif (( ABS <= 30 )); then warn "skew ${SKEW}s - close to the 30s limit"
        else bad "skew ${SKEW}s - fix with: sudo sntp -sS time.apple.com"
        fi
    fi
else
    warn "could not read the server Date header (offline?)"
fi
echo "        NTP on?  systemsetup -getusingnetworktime"

head_ "6. Reachability of the dashboard (HTTP path)"
if tmo 10 curl -sf -o /dev/null "https://collisionprotocol.com/pool"; then
    ok "https://collisionprotocol.com/pool reachable"
else
    bad "dashboard unreachable - general network problem, not collider-specific"
fi

if [[ -n "$BINARY" ]]; then
    head_ "7. Local binary"
    if [[ -x "$BINARY" ]]; then ok "executable: $BINARY"; else bad "not executable: $BINARY"; fi
    ARCH=$(file -b "$BINARY" 2>/dev/null)
    case "$ARCH" in
        *arm64*)  ok "arch arm64 (Apple Silicon native)" ;;
        *x86_64*) warn "Intel binary - needs Rosetta on Apple Silicon, much slower" ;;
        *)        warn "unknown arch: $ARCH" ;;
    esac
    if xattr -p com.apple.quarantine "$BINARY" >/dev/null 2>&1; then
        bad "quarantined - launchd cannot exec it: xattr -d com.apple.quarantine '$BINARY'"
    else
        ok "not quarantined"
    fi
    if tmo 15 "$BINARY" --help >/dev/null 2>&1; then
        ok "--help runs (Gatekeeper is not blocking it)"
    else
        warn "--help failed; try running it by hand to see the real error"
    fi

    head_ "8. GPU"
    GPU=$(system_profiler SPDisplaysDataType 2>/dev/null | grep -E "Chipset Model|Metal" | head -4)
    if [[ -n "$GPU" ]]; then
        echo "$GPU" | sed 's/^ */        /'
        ok "a GPU is visible to Metal"
    else
        warn "no GPU info from system_profiler (headless/VM?)"
    fi
    echo "        Benchmark: '$BINARY' --benchmark --benchmark-time 30"
fi

head_ "Verdict"
if (( FAILED == 0 )); then
    cat <<'EOF'
  Endpoint checks pass. If the client still says "Failed to connect", the
  cause is almost certainly the URL: you must pass jlps://, not host:port.

      ./collider --pool jlps://pool.collisionprotocol.com:17403 \
                 --worker <your-address> --verbose

  Watch for: TLS connection established -> AUTH_OK -> WORK_ASN
  AUTH_FAIL instead means clock drift or a bad worker name, not networking.
EOF
else
    cat <<EOF
  $FAILED check(s) failed. If TCP failed but the dashboard check passed, a
  firewall/VPN/ISP is blocking port $PORT - try a phone hotspot to confirm,
  then allow the app through your firewall or use a VPN that permits egress
  on that port. If BOTH failed, this machine is offline.
EOF
fi
echo
echo "Pool status:  https://collisionprotocol.com/pool"
echo "Your worker:  https://collisionprotocol.com/dashboard?worker=${WORKER:-<your-address>}"
exit $(( FAILED > 0 ? 1 : 0 ))
