# The stock collider's wild herd is broken on Metal/CPU (and the fix)

## Symptom

Against `ops/jlp_server.py --assignment both`, the stock v1.5.6 macOS client
authenticates, takes work, and submits DPs — but the server stores **tame DPs
only**:

```
[dp] ... accepted 4 (tame=4 wild=0) stored tame=11119 wild=0
[dp] ... also rejected {'verify_wild': 2}
```

Every wild DP fails cryptographic verification under both wild conventions
(spec `Q - range_start*G` and Metal/standalone `Q`). With no valid wild DPs, a
tame/wild collision is impossible, no matter how long it runs.

## Root cause

`src/core/crypto_cpu.hpp`, `collider::cpu::ec_add(ECPoint& R, const ECPoint& P,
Qx, Qy)` is **not alias-safe**: it writes `R.X` and `R.Y` before the last reads
of `P.X`/`P.Y`. All three wild-seed builders call it in-place:

- `src/gpu/metal_kangaroo_backend.mm` — `ec_add(wp, wp, ox, oy)` (pool, Metal)
- `src/gpu/metal_multi_gpu_kangaroo.mm` — `ec_add(wp, wp, ox, oy)` (standalone)
- `src/core/kangaroo.hpp` — `ec_add(wild.point, wild.point, ox, oy)` (CPU)

The result has the **correct X** for `Q + off*G` but a **corrupted Y** — the
seed point is off the curve. The kernel then walks from that invalid point, so
reported wild `(x, d)` pairs satisfy no relation with `Q` at all. Tame seeds
only use `ec_mul`, which is why tame DPs verify fine.

This was proven by compiling the client's own header in isolation: for test
secret/offset values, non-aliased `ec_add` equals `(s+off)*G` on-curve, while
the aliased call returns the same X, a different Y, `on_curve=NO`. After the
patch below, the aliased call also equals `(s+off)*G`, on-curve.

There is no server-side salvage: the wild walk's X sequence is generated from
an invalid point, so wild DPs carry no recoverable information about the key.
The client must be patched and rebuilt.

## Fix

`ops/collider-ecadd-alias.patch` snapshots the inputs at the top of `ec_add`
and `ec_double`, making both safe for `R == P`. One header change repairs all
three call sites.

Build a patched Free client on the Mac (Apple Silicon). Note: do **not** use
the repo's `build_macos.sh` for this — in the public tree it runs
`git pull origin develop` (which would fight the patch) and checks for a Pro
artifact name. Use CMake directly:

```bash
brew install cmake ninja openssl@3
git clone https://github.com/hevnsnt/collider.git /Users/progressive/collider-patched
git -C /Users/progressive/collider-patched apply /Users/progressive/rws/ops/collider-ecadd-alias.patch
cmake -S /Users/progressive/collider-patched -B /Users/progressive/collider-patched/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCOLLIDER_USE_METAL=ON -DCOLLIDER_USE_CUDA=OFF -DOPENSSL_ROOT_DIR=/opt/homebrew/opt/openssl@3
cmake --build /Users/progressive/collider-patched/build --parallel
```

Artifact: `/Users/progressive/collider-patched/build/collider`.

Then run it against the local pool exactly like the stock binary:

```bash
/Users/progressive/collider-patched/build/collider --pool jlp://127.0.0.1:17403 --worker bc1qcaugv5gdawqjxw3dm3uyx5gw5m9uqak9ues7wd --verbose
```

Success looks like both counters climbing on the server:

```
[dp] ... accepted 5 (tame=3 wild=2) stored tame=... wild=...
```

## Status / honesty

- The patch was verified here by compiling the client's EC header and by
  applying the patch to a fresh clone. The full macOS/Metal binary cannot be
  built in the Linux sandbox, so the first Mac build may still surface ordinary
  build/toolchain issues — those are separate from this bug.
- This bug also means the stock client's Metal/CPU wild herd could never have
  contributed a valid wild DP to the official pool either. CUDA workers use a
  different backend and are unaffected.
