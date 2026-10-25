# Changelog — CodeHaven Workstation (RealWalletScanner)

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
