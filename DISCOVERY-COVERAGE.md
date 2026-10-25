# Discovery Coverage — "all the necessary ways of getting wallets with balance"

Audit of the candidate-generation pipeline (v2.7.1), grounded in a REA
evidence pass over `WebWorkstation/` and a direct read of the generators
(`hunter_worker.js` 6-mode worker rotation, `server.js` server-side families).
Goal: **maximize finds per hour, not scan-hours** — exhaust the finite,
knowable families before leaning on the infinite lottery arms.

## The one rule that makes this efficient

Discovery yield = (weak-secret families searched) × (funded-dump coverage)
÷ (wasted candidates). Three levers:

1. **Finite families are checklists, not lotteries.** Small-integer keys,
   famous phrases, dev mnemonics, single words, timestamp seeds — each is
   countable. Exhausting one is a real, reportable milestone.
2. **Infinite families never "finish".** Random BIP39 and CSPRNG hex keys
   have probability ~0 per candidate; they are background lottery tickets.
3. **The funded dump bounds everything.** A derived address can only become
   a lead if its chain/format exists in the dump. LTC/DOGE/SOL/XRP/TRX
   derivation is pure waste if the 2 GB fabric is BTC/ETH-only.

## Family inventory (what the scanner searches today)

| # | Family | Where | Type | Verdict |
|---|--------|-------|------|---------|
| 1 | Human brainwallets (SHA-256 passphrase, comp+uncomp 2011-2015 style) | worker mode 3, server #4 | **finite-ish** | ✅ strong — 4 sub-patterns (word+suffix, root+suffix, two words, PIN/date) |
| 2 | Electrum v2 seeds (all 4 version prefixes, m/0/0 + m/0'/0/0) | worker mode 1 | **finite-ish** | ✅ strong |
| 3 | Weak PRNG: Unix-second timestamp seeds 2013–2024 | server #3 | **finite** | ✅ good — genuine historical vector |
| 4 | Low-entropy patterned BIP39 (repeated word ×11, alternating pair, valid checksum) | worker mode 4 | **finite** | ✅ good — 2,048 single-word seeds alone |
| 5 | Semantic/bigram cluster BIP39 | worker modes 0/2/5, server #2 | infinite | ✅ present (lottery arm) |
| 6 | Custom HD paths (Ledger Live, MEW, Bitcoin Core) | worker mode 0 | infinite w/ paths | ✅ good |
| 7 | CSPRNG hex-key burst | `keys` burst loop | infinite | ✅ present (lottery arm) |
| 8 | Subspace repulsion filter | server `repulsion` | filter | ✅ present |

Derivation per candidate is already broad: BTC 44'/49'/84'/86' (legacy,
nested, native segwit, taproot) + change + index 1–2, ETH + index 1, TRX,
XRP, SOL (SLIP-0010), LTC (44'/2' + 84'/2'), DOGE (44'/3'), Electrum import
block. Server-side verification: BTC, ETH, ARB, BASE, OP, AVAX, MATIC, BNB,
TRX, XRP (10 chains).

## Gaps found (priority order)

### GAP 1 — raw small-integer private keys (the oldest finite family) — MISSING
`private key = 1, 2, 3, … N` as raw secp256k1 scalars. Every recovery hunter
checks these first; they are trivially cheap (no BIP39 work) and **finite**.
Add a dedicated family sweeping 1…N (multi-chain derive per key), reported as
"small-int keys through N" so progress is a visible milestone.

### GAP 2 — family-level yield stats — MISSING
The FIND FUNNEL aggregates everything. Per-`vectorKey` lead/verdict/confirm
counters would show which family actually pays (and let us re-weight the
uniform 1/6 worker rotation toward proven families).

### GAP 3 — dump/derived-chain mismatch check — MISSING
Nothing verifies that the funded dump's chain coverage matches the derived
address space. If the dump is BTC/ETH-only, all LTC/DOGE/SOL/XRP/TRX work
is dead weight. The funnel should show per-chain fabric counts.

### GAP 4 — LTC/SOL/DOGE server-side verification (known task 17) — OPEN
Derived everywhere, verified only while the browser is open.

### GAP 5 — famous exact phrases — PARTIAL
Human roots are generic ('dragon', 'apollo', …). The canonical public
brainwallet phrases ("correct horse battery staple", "Satoshi Nakamoto",
"password", empty string…) are a tiny finite list worth adding.

## Recommended order of work

1. GAP 1 (small-int family) + GAP 5 (famous phrases) — both finite checklists
2. GAP 2 (per-family funnel) — makes every later decision evidence-based
3. GAP 3 (fabric chain report) — kills wasted derivation early
4. GAP 4 (task 17) — unlocks LTC/SOL/DOGE confirmation

*Boundary: all of this searches secrets that are already public or
predictable (weak keys, test material, abandoned wallets) against public
chain data. It is not aimed at extracting keys from third-party software.*
