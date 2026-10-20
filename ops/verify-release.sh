#!/usr/bin/env bash
# verify-release.sh - check a downloaded collider archive against the digest
# GitHub publishes for the official release.
#
# The release page does not show a checksum, so people skip this step. GitHub's
# API does publish one per asset; this fetches it and compares.
#
# Usage:
#   ./ops/verify-release.sh --file ~/puzzle/collider.tar.gz
#   ./ops/verify-release.sh --file ./collider.tar.gz --tag v1.5.6
#   ./ops/verify-release.sh --file ./collider-linux-x64.tar.gz \
#                           --asset collider-linux-x64.tar.gz
#
# On a mismatch, do not run the binary. Delete it and download again from
# https://github.com/hevnsnt/collider/releases
set -uo pipefail

REPO="hevnsnt/collider"
TAG=""
FILE=""
ASSET=""

die()  { echo "ERROR: $*" >&2; exit 1; }
ok()   { printf '  \033[32mPASS\033[0m  %s\n' "$*"; }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --file)  FILE="${2:-}"; shift 2 ;;
        --tag)   TAG="${2:-}"; shift 2 ;;
        --asset) ASSET="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done

[[ -n "$FILE" && -f "$FILE" ]] || die "--file <downloaded archive> is required"
command -v curl >/dev/null || die "curl is required"
command -v shasum >/dev/null || command -v sha256sum >/dev/null || die "no sha256 tool"

sha256_of() {
    if command -v shasum >/dev/null; then shasum -a 256 "$1" | cut -d' ' -f1
    else sha256sum "$1" | cut -d' ' -f1; fi
}

size_of() {
    if [[ "$(uname -s)" == "Darwin" ]]; then stat -f%z "$1"; else stat -c%s "$1"; fi
}

# Pick the asset name from the running platform unless given.
if [[ -z "$ASSET" ]]; then
    OS="$(uname -s)"; MACH="$(uname -m)"
    case "$OS-$MACH" in
        Darwin-arm64)   ASSET="collider-macos-arm64.tar.gz" ;;
        Darwin-x86_64)  ASSET="collider-macos-arm64.tar.gz" ;;   # only mac build published
        Linux-x86_64)   ASSET="collider-linux-x64.tar.gz" ;;
        *) die "cannot guess the asset for $OS-$MACH; pass --asset" ;;
    esac
    echo "Guessed asset for this platform: $ASSET"
fi

# Find the tag if not supplied: newest release that has this asset.
if [[ -z "$TAG" ]]; then
    echo "Looking up the newest release containing $ASSET ..."
    TAG=$(curl -fsSL "https://api.github.com/repos/$REPO/releases?per_page=10" \
        | python3 -c "
import sys, json
want = '$ASSET'
for r in json.load(sys.stdin):
    if any(a['name'] == want for a in r.get('assets', [])):
        print(r['tag_name']); break
" 2>/dev/null)
    [[ -n "$TAG" ]] || die "could not find a release publishing $ASSET"
fi

echo "Release: $TAG"
JSON=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/tags/$TAG") \
    || die "could not fetch release $TAG"

read -r PUB_SIZE PUB_DIGEST < <(printf '%s' "$JSON" | python3 -c "
import sys, json
want = '$ASSET'
d = json.load(sys.stdin)
for a in d.get('assets', []):
    if a['name'] == want:
        print(a['size'], (a.get('digest') or '').replace('sha256:', ''))
        break
else:
    print('0 none')
" 2>/dev/null)

[[ "$PUB_SIZE" != "0" ]] || die "asset $ASSET not found in release $TAG"
[[ "$PUB_DIGEST" != "none" && -n "$PUB_DIGEST" ]] \
    || die "this release publishes no digest; verify manually"

LOCAL_SIZE=$(size_of "$FILE")
LOCAL_SHA=$(sha256_of "$FILE")

echo
echo "  asset:    $ASSET"
echo "  size:     local $LOCAL_SIZE / published $PUB_SIZE"
echo "  sha256:   local     $LOCAL_SHA"
echo "            published $PUB_DIGEST"
echo

if [[ "$LOCAL_SIZE" != "$PUB_SIZE" ]]; then
    bad "size differs - this is not the published archive"
    exit 1
fi
if [[ "$LOCAL_SHA" != "$PUB_DIGEST" ]]; then
    bad "sha256 differs - archive is corrupted or tampered with"
    exit 1
fi

ok "archive matches the published digest"
echo
echo "  Extract and run it from the extracted directory:"
echo "    tar xzf \"$FILE\" && cd \$(basename \"$FILE\" .tar.gz)"
echo
echo "  The digest proves the archive is byte-identical to what the maintainer"
echo "  published. It does NOT prove the maintainer is trustworthy - for that"
echo "  you would have to read the source."