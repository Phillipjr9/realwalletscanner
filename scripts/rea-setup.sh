#!/usr/bin/env bash
# Restore the REA reverse-engineering toolkit in this workspace.
# Safe to run repeatedly; used to re-arm after node_modules / venv wipes.
#
#   bash scripts/rea-setup.sh            # REA CLI + pwntools engine (fast)
#   bash scripts/rea-setup.sh --with-rizin   # also build rizin (~15 min)
#
# Env vars needed afterwards are printed at the end.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REAHOME="${REAHOME:-$HOME/rea-workspace}"
mkdir -p "$REAHOME"
cd "$REAHOME"

echo "== 1/3 REA CLI (npm) =="
if [ -d node_modules/rea-agents ]; then
  echo "rea-agents already present: $(node -p "require('./node_modules/rea-agents/package.json').version" 2>/dev/null)"
else
  npm install rea-agents --no-audit --no-fund
fi
# frida is optional (its installer needs network to frida.re; skip quietly on failure)
npm install frida --no-audit --no-fund >/dev/null 2>&1 && echo "frida: ok" || echo "frida: skipped (installer download blocked)"

echo "== 2/3 pwntools engine (inspect-binary-layout) =="
if [ -x .venv/bin/python ] && .venv/bin/python -c "import pwnlib, elftools, unicorn" 2>/dev/null; then
  echo "venv already pinned"
else
  python3 -m venv .venv
  .venv/bin/pip install --quiet "pwntools==4.15.0" "pyelftools==0.33" "unicorn==2.1.2"
  echo "venv pinned: pwntools 4.15.0 / pyelftools 0.33 / unicorn 2.1.2"
fi

echo "== 3/3 rizin (optional extra disassembly engine) =="
if [ -x rizin-install/bin/rizin ]; then
  echo "rizin already built: $(rizin-install/bin/rizin -v 2>/dev/null | head -1)"
elif [ "${1:-}" = "--with-rizin" ]; then
  echo "building rizin from source (codeload + meson)…"
  .venv/bin/pip install --quiet meson ninja
  curl -sL "https://codeload.github.com/rizinorg/rizin/tar.gz/refs/tags/v0.9.1" -o rizin-src.tar.gz
  rm -rf rizin-0.9.1 && tar -xzf rizin-src.tar.gz
  # wraps: rewrite blocked release-asset URLs to codeload mirrors where possible
  python3 - <<'PY'
import hashlib, io, os, re, tarfile, urllib.request
SUB = os.path.join(os.getcwd(), 'rizin-0.9.1', 'subprojects')
CACHE = os.path.join(os.getcwd(), 'wrapcache'); os.makedirs(CACHE, exist_ok=True)
FIX = {
    'pcre2.wrap':   ('PCRE2Project/pcre2', 'pcre2-10.47', 'pcre2-10.47'),
    'liblzma.wrap': ('tukaani-project/xz', 'v5.8.3', 'xz-5.8.3'),
    'libzip.wrap':  ('nih-at/libzip', 'v1.11.4', 'libzip-1.11.4'),
    'zlib.wrap':    ('madler/zlib', 'v1.3.2', 'zlib-1.3.2'),
    'zstd.wrap':    ('facebook/zstd', 'v1.5.7', 'zstd-1.5.7'),
}
for wrap, (repo, tag, dirname) in FIX.items():
    raw = urllib.request.urlopen(f'https://codeload.github.com/{repo}/tar.gz/refs/tags/{tag}', timeout=120).read()
    tin = tarfile.open(fileobj=io.BytesIO(raw)); members = tin.getmembers()
    top = members[0].name.split('/')[0]
    outpath = os.path.join(CACHE, dirname + '.tar.gz')
    with tarfile.open(outpath, 'w:gz') as tout:
        for m in members:
            m.name = m.name.replace(top, dirname, 1)
            tout.addfile(m, tin.extractfile(m) if m.isfile() else None)
    sha = hashlib.sha256(open(outpath, 'rb').read()).hexdigest()
    wp = os.path.join(SUB, wrap); text = open(wp).read()
    text = re.sub(r'source_url = .*', f'source_url = file://{outpath}', text)
    text = re.sub(r'source_fallback_url = .*', f'source_fallback_url = file://{outpath}', text)
    text = re.sub(r'source_hash = .*', f'source_hash = {sha}', text)
    open(wp, 'w').write(text)
# zydis: sources already vendored in packagefiles/zydis/amalgamated-dist — any tarball works
os.makedirs('zydis-dummy', exist_ok=True)
open('zydis-dummy/README', 'w').write('placeholder')
tarfile.open(os.path.join(CACHE, 'zydis-amalgamated.tar.gz'), 'w:gz').add('zydis-dummy/README', 'README')
sha = hashlib.sha256(open(os.path.join(CACHE, 'zydis-amalgamated.tar.gz'), 'rb').read()).hexdigest()
wp = os.path.join(SUB, 'zydis.wrap'); text = open(wp).read()
text = re.sub(r'source_url = .*', f'source_url = file://{CACHE}/zydis-amalgamated.tar.gz', text)
text = re.sub(r'source_hash = .*', f'source_hash = {sha}', text)
open(wp, 'w').write(text)
print('wraps rewired to codeload')
PY
  # pcre2's sljit git submodule is empty in codeload archives — fetch the pinned commit
  SLJIT_SHA=$(cd rizin-0.9.1 && cat .gitmodules >/dev/null 2>&1; echo 45f910b78c6605ebf5b53d3ec7cb00f2312fe417)
  curl -sL "https://codeload.github.com/zherczeg/sljit/tar.gz/$SLJIT_SHA" -o /tmp/sljit.tar.gz
  mkdir -p rizin-0.9.1/subprojects/pcre2-10.47/deps/sljit
  tar -xzf /tmp/sljit.tar.gz -C rizin-0.9.1/subprojects/pcre2-10.47/deps/sljit --strip-components=1
  export PATH="$REAHOME/.venv/bin:$PATH"
  (cd rizin-0.9.1 && meson setup build --prefix="$REAHOME/rizin-install" && ninja -C build && ninja -C build install)
else
  echo "skipped (pass --with-rizin to build)"
fi

cat <<EOF

== ready ==
export REA_PWNTOOLS_PYTHON=$REAHOME/.venv/bin/python
export PATH=$REAHOME/rizin-install/bin:\$PATH   # only if rizin was built

Quick check:  npx rea doctor          (from $REAHOME)
EOF
