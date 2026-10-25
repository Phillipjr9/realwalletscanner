# Changelog — CodeHaven Workstation (RealWalletScanner)

## 2.9.0-beta.6 — 2026-10-25 (BETA)

**Verdict correctness: a partially-checked wallet can no longer be buried as
"empty"** — plus BTC provider overhaul and the Telegram 409 fix.

The beta.5 diagnostics exposed a dangerous leak: an item was finalized
"verified" as soon as ONE chain answered. With BTC failing 51% of requests
(HTTP 429), a wallet whose BTC check failed but whose EVM checks returned
zero was marked **confirmed empty and permanently excluded** — even if it
held BTC. For a wallet scanner that is the one unforgivable bug.

- **Asked-vs-answered gate:** every chain check an item receives marks it
  "asked"; the verdict is only finalized when EVERY asked chain has replied.
  Partially-answered items are requeued automatically — and a partial retry
  never burns an attempt (no more parking real hits because a public endpoint
  sneezed).
- **BTC providers rebuilt:** BlockCypher joins the rotation
  (blockchain.info → blockcypher → mempool.space → blockstream.info),
  cooldowns grow to 8 minutes, payloads must cover EVERY requested address or
  the call is treated as a failure (no more half-answered batches), and the
  per-address esplora fallback only serves small batches in parallel — a big
  batch defers instead of crawling and starving the verify pump.
- **Telegram HTTP 409 ("webhook is active")** — the app now calls
  `deleteWebhook` automatically (Telegram's documented fix) and resumes
  polling. Re-saving the bot token was never going to fix this one.

No re-import needed: the bloom keeps its 256 MB / 0.012% size.

## 2.9.0-beta.5 — 2026-10-25 (BETA)

**Auto-sized bloom filter (11% → ~0.01% false positives) + BTC rate-limit rescue.**

A 57.7M-address dump in the old FIXED 32 MB filter gave ~11% false positives —
1 in 9 random wallets became a "lead" to verify. That flood filled the 50k
backlog and hammered the free BTC endpoint into 86% rate-limit failures.

- **Bloom filter auto-sizes to the dump** (~4 bytes per imported address,
  32 MB..1 GB; `RWS_BLOOM_MB` overrides). At 57.7M addresses that is 256 MB
  and ≈0.01% FP — 1000× fewer false leads. Size is pinned in the filter meta
  so it stays stable across restarts. **One-time: re-import the dump after
  this update** (the old 32 MB bit file cannot be rehashed in place — the
  filter will say "import the dump again" at boot).
- **BTC balances rotate across providers** (blockchain.info multiaddr →
  mempool.space → blockstream.info) with exponential per-provider cooldown on
  HTTP 429 — rate-limit storms back off instead of failing 86% of checks.
- Diagnostics "bloom FP" line now shows the theoretical rate when no
  empirical one is available (was showing 0.000% beside an 11% dump).

## 2.9.0-beta.4 — 2026-10-25 (BETA)

- **THE live-display freeze, fixed.** The feed pulled 48 rows every 220ms
  (~218/s) but only drew 1 row per 18ms tick (~55/s). Whenever the backlog hit
  140 the poller switched itself OFF - freezing the rate, counters and stream
  (the session timer, being just the local clock, kept ticking) - then burst
  again when the backlog drained: the 'keeps stopping every time' see-saw.
  Now: the poll never stalls, excess rows are dropped oldest-first (capped at
  96), the drawer handles 12 rows per tick, and the localStorage bookkeeping
  write is throttled to every 3s.

## 2.9.0-beta.3 — 2026-10-25 (BETA)

- Native-app path polished end to end: `make-standalone-app.command` now launches
  the installed CodeHaven Workstation.app itself (no browser, ever), the old
  lightweight browser-launcher is loudly deprecated, and `start-sturdy.command`
  no longer pops a browser window — it prints the URL instead.

## 2.9.0-beta.2 — 2026-10-25 (BETA)

- **Connectivity watcher**: when the internet comes back, the workstation heals
  itself — parked live-checks are re-queued automatically, prices refresh, and
  alerts retry. When the internet drops, the Self-Monitor says so plainly while
  scanning continues 100% offline. New `POST /api/connectivity/refresh` forces
  a check right now.

## 2.9.0-beta.1 — 2026-10-25 (BETA)

First packaged beta. Everything below is built, tested (34/34 UI checks green)
and running live. This is the release to install and live with for a week.

### Highlights since 2.7.2

**Recovery engine (ForensicPro, 5 modes)**
- Mode 2 — Mistyped-Word Repair: one-edit BIP39 fixes (swap / drop / extra /
  wrong letter) pruned by checksum and solved against your target addresses
- Missing 1–3 words, 25th-word passphrase cracker, scrambled-paper-grid
  descrambler, Ethereum keystore/MetaMask vault cracker (all pre-existing,
  all kept)
- Check tab suggests "did you mean" fixes inline when a phrase has typos

**Transferable-vs-locked verdict (Check tab)**
- Manual phrase/key inspection now answers "are ALL the coins transferable?"
  per coin, with the lock reason (multisig Safe / contract)
- Vault rule: funded + transferable → Wallets Found + Ever Held · funded +
  locked → reported, never recorded as recoverable
- Fixed a real input bug: 64-char hex private keys were being hashed as
  brainwallet phrases; raw keys now derive correctly

**New hunt families**
- 7 brainflayer-grade KDF families with parameters verified against
  brainflayer's C source: warpwallet, brainwallet.io, brainv2, keccak256,
  ether.camp camp2, NIST SHA3-256, rushwallet (heavy scrypt ones are
  opt-in via RWS_KDF_SLOW=1)
- Timestamp sweep (unix-time keys, sha256(time), sha256(iso)) 2009–2030
- Integer sweep is range-bounded (RWS_SWEEP_START / RWS_SWEEP_END)
- Funded-dump coverage report (audit GAP 3): per-chain counts, dead-weight
  warning, bloom FP estimate, /api/bloom-import for bigger dumps

**Stability — the "scanning keeps stopping" fixes**
- The engine survives SIGHUP: closing the Terminal window no longer quits it
- Crash diary (crash.log) records errors, signals and exit codes on disk
- State saves are atomic — no more truncated JSON after a hard kill
- Corrupt state files are quarantined once instead of re-warning forever
- Duplicate instances exit politely instead of fighting over the port
- scan.stalled watchdog restarts workers automatically
- scripts/macos/start-sturdy.command: keep-awake + auto-restart + /tmp/scanrun.log

### Install (Mac)
```
cd ~/realwalletscanner && git pull
bash scripts/macos/start-sturdy.command
```
Or build the double-clickable app: `bash scripts/macos/make-standalone-app.command`

## 2.8.4 · 2.8.3 · 2.8.2 · 2.8.1 · 2.8.0 · 2.7.3
Stability and feature increments; see the release notes of each tag.
