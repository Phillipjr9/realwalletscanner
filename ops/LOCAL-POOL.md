# Running your own JLP pool

The public pool `pool.collisionprotocol.com:17403` has been unreachable since
2026-10-19, the reference server repo the docs link to is a 404, and the
client's last commit is 2026-06-15. The wire protocol is fully published, so
the pool has been rebuilt here as `ops/jlp_server.py`.

## What this is

A working JLP v4-compatible pool server, ~700 lines, no dependencies beyond
Python 3.9+. It speaks the same protocol the shipped `collider` binary speaks,
verified against the client's own parser and struct `static_assert`s.

## What this is not

**It is not the official pool, and it will not reconnect you to it.** Your
dashboard entry, your 23.21K credited DPs, the pool-wide 388.97M DP count —
none of that carries over. This is *your* pool. It also does not connect to
the official pool's distinguished points, because those live on a server
nobody can reach.

**It does not make the real puzzle solvable.** Puzzle #140 is a 139-bit range.
At the pool's own stated 2.6 DP/s it is ~21,789 years. Putting a server in
front of that changes who holds the ledger, not the arithmetic. A pool of your
laptops is *slower* than the 77-machine pool that was already running.

So there are two honest reasons to run this, and you should pick one
deliberately:

1. **You want a hunt with a finish line.** Generate a puzzle small enough that
   your own machines solve it, fund it with a small amount of BTC, and run the
   same software end to end. Same protocol, same kangaroo collision, an actual
   `[SOLVED]`. This is the only configuration in which something gets found.
2. **You want the official pool back.** This server is the compatibility
   harness for that: it proves your client, your binary, and your network work,
   and it is the thing to point at while you chase the maintainer. It also
   means that if the official pool returns, you have lost nothing.

## Getting the files

If you have never cloned it:

```bash
git clone -b arena/118369ec-realwalletscanner \
  https://github.com/Phillipjr9/realwalletscanner.git ~/rws
```

If `~/rws` already exists from an earlier attempt, it is an **old checkout of
this same repo** and will not have the new files. A plain `git clone` refuses
with *"destination path already exists and is not an empty directory"*, which
is what happens, and then `make-puzzle.py` is missing. Update it in place:

```bash
git -C ~/rws fetch origin
git -C ~/rws checkout arena/118369ec-realwalletscanner
git -C ~/rws reset --hard origin/arena/118369ec-realwalletscanner
```

`reset --hard` discards any local edits inside `~/rws`. If you have work
there you care about, clone somewhere else instead (`~/jlp`) and leave `~/rws`
alone.

### Python and OpenSSL

`hashlib.new("ripemd160")` is **not** guaranteed to work. OpenSSL 3.x moved
RIPEMD-160 into its legacy provider, so Homebrew Python (`python3.14` here)
often raises instead of returning a digest, and P2PKH address derivation fails
with an unhelpful error. The tooling carries a pure-Python RIPEMD-160 that is
used automatically when OpenSSL cannot provide one, so this should not block
you. To check which path your machine is on:

```bash
python3 -c "import hashlib; print(hashlib.new('ripemd160', b'').hexdigest())"
```

To force the fallback (useful for testing):

```bash
JLP_FORCE_RIPEMD_FALLBACK=1 python3 ops/make-puzzle.py --bits 40
```

## Quick start — a puzzle you can actually finish

On any machine (a laptop is fine):

```bash
cd ~/rws/ops
python3 make-puzzle.py --bits 40
```

That writes `puzzle.json`, which holds the secret. **Do not lose it and do not
share it.** It prints:

```
address     19piohMirmauVqXVXdZZLzaSzGvHu3rUBV
pubkey      03c857...
range       0x8000000000 .. 0xffffffffff
dp_bits     13
```

Start the pool in one terminal:

```bash
python3 jlp_server.py \
  --pubkey 03c857... \
  --range-start 0x8000000000 --range-end 0xffffffffff --dp-bits 13 \
  --puzzle puzzle.json
```

Point the client at it in another:

```bash
./collider --pool jlp://127.0.0.1:17403 \
  --worker bc1qcaugv5gdawqjxw3dm3uyx5gw5m9uqak9ues7wd --verbose
```

When the two halves collide, the server prints the key and writes
`solved.txt`:

```
[SOLVED] key = 00000000000000000000000000001a2b3c4d5e6f...
[SOLVED] written to solved.txt
```

Verify it independently — do not trust the server's own output:

```bash
python3 check-solution.py --puzzle puzzle.json --key $(cat solved.txt)
```

`VERIFIED` means the key derives the puzzle address. That address is now
yours to spend from.

### Sizing

Expected work is roughly `1.15 * 2^(bits/2)` group operations, so it is the
*square root* of the range:

| bits | work | laptop time |
| ---: | ---: | --- |
| 32 | ~100 K ops | seconds |
| 40 | ~2 M ops | seconds–minutes |
| 44 | ~6 M ops | minutes |
| 48 | ~19 M ops | tens of minutes |
| 56 | ~300 M ops | hours |
| 140 | 2^70 | never |

Pick 40 to prove the pipeline, 48–56 if you want it to feel like a hunt.

### Making the prize real

Generate the puzzle, **send BTC to the printed address first**, then solve.
The key you recover controls it. Only send an amount you are willing to lose
if the tooling turns out to have a bug — this is new code.

## Quick start — the compatibility harness

If your goal is the official pool:

```bash
cd ~/rws/ops
python3 jlp_server.py --pubkey <any valid 33-byte compressed key> \
  --range-start 0x8000000000000000000000000000000000000000000 \
  --range-end   0xfffffffffffffffffffffffffffffffffffffffff --dp-bits 20
```

then connect the real client with `--pool jlp://127.0.0.1:17403`. If you get
`[Pool] Authentication successful` and the server logs
`[work] ... type=TAME`, your binary and network are fine and the only thing
broken is `pool.collisionprotocol.com`. That is a clean, decisive test.

## Tests

```bash
python3 test_jlp_server.py    # 30 checks, no GPU, ~2 s
python3 demo-solve.py         # spawns the real server process, solves a 24-bit puzzle
python3 jlp_server.py --check-known-answer   # proves the DP→key math alone
```

`demo-solve.py` is the one that matters: it runs `jlp_server.py` as a
subprocess, talks to it over TCP, and asserts that `solved.txt` was written
and `check-solution.py` accepts the key.

## Protocol gotchas that cost real time

Full detail in `PROTOCOL-notes.md`. The three that bite:

1. **The version byte must be exactly `4`.** The doc says the server
   negotiates `min(client, 4)`; the shipped client hard-disconnects on any
   inbound frame whose flags byte differs from its own `PROTOCOL_VERSION = 4`.
   Implement `min()` faithfully and you get an instant disconnect.
2. **`WORK_ASN` must be exactly 126 bytes.** Not "at most" — the client
   rejects any other length.
3. **Never send a populated `AUTH_OK` advert.** A non-empty advert makes the
   client download a binary over HTTPS, verify its sha256, and replace itself.
   A self-hosted pool has no business doing that. The server sends a
   zero-payload `AUTH_OK`.

## Exposing it to other machines

`jlp://` is plaintext — worker names, DPs, and the payout address travel in
the clear. On a LAN that is usually acceptable; over the internet it is not.

To let your other Macs join, bind to the LAN interface and open the port:

```bash
python3 jlp_server.py --listen 0.0.0.0 --port 17403 ... 
```

```bash
# macOS firewall prompt will ask; allow it, then from the other Mac:
./collider --pool jlp://192.168.1.50:17403 --worker bc1qcaug... --verbose
```

If you need this over an untrusted network, put it behind a TLS terminator and
use `jlps://` — the client validates against the system trust store with SNI
and `NO_PARTIAL_WILDCARDS`, so a self-signed cert will be refused.

Note the current server keeps DP state in memory: restarting it loses the
distinguished points collected so far. For a 40-bit puzzle that is seconds of
work and does not matter. For anything larger, it does.

## Honest status

| Piece | State |
| --- | --- |
| Framing, all structs vs client `static_assert` | verified |
| AUTH / AUTH_OK / AUTH_FAIL | verified over TCP |
| WORK_REQ / WORK_ASN | verified over TCP |
| DP_BATCH_V2 (78 B) and V3 (114 B) | verified |
| DP validation (tame + wild, curve math) | verified |
| Key reconstruction from a collision | verified end to end |
| STATS_RSP, PING/PONG | verified |
| `solved.txt` + independent address check | verified |
| **Real `collider` binary against this server** | **NOT verified** |

That last row is the one to be careful about. The build environment here is a
Linux sandbox with no GPU and no Metal, so the actual `collider` binary could
not be run against it. Every struct size was checked against the shipped
client's `static_assert`s and every message against its parser, but "the bytes
are right" and "the real client connects" are different claims. Treat first
contact on your Mac as a debugging session, not a formality. The server prints
every AUTH, WORK, and DP it receives, so `--verbose` on the client plus the
server's stdout will show exactly where it diverges.
