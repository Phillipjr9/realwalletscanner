# Collision Protocol — hunt operations

Runbook for keeping a pool worker alive on a real machine, and for knowing
whether "alive" is actually earning anything. Written against theCollider
**v1.5.6 / JLP v3** (pool protocol v3; v1.4.x clients are refused with
`UPGRADE_REQUIRED`).

Worker in this kit: `36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP`

---

## TL;DR — current state

| Item | Value | Read |
| --- | --- | --- |
| Rank | #12 of ~55 workers | 1.40M DPs total |
| Pool share | 0.3598% | would pay ~0.0478 BTC net **if** #140 is solved |
| Last session | 4h 12m uptime, last activity **10/19 7:31 AM** | stopped, not running |
| Puzzle | #140, 14 BTC | needs ~**5.06 trillion** DPs pool-wide |
| Work actually done | ~389M of ~5.06T | **0.0076%** |
| Your DP rate | ~93 DP/s | implies ~25 GKeys/s, i.e. ~3× an RTX 4090 |

You are not currently hunting — the dashboard's "No submissions in this window"
is the tell. A browser tab cannot mine; it only displays the numbers the server
already has. Everything below is about the machine with the GPU.

---

## 1. What the tab can and cannot do

There is no web miner, no "launch app" mining session, and no cloud fallback.
`collisionprotocol.com/dashboard?worker=…` is a read-only HTML view of server
state. The DPs are produced by the native `collider` binary running CUDA
kernels against the pool socket. If that process is not running on a machine
you own, your rank decays and nothing accrues.

Two implications:

- Keep the **GPU box** up, not the tab. The tab is a health check.
- Closing the tab is harmless. Rebooting the GPU box is not free — every
  restart loses DPs computed but not yet flushed (a few seconds' worth) and
  triggers a chunk reissue.

---

## 2. Where you actually stand

The dashboard's "Est. Payout" is a projection conditional on the pool solving
#140. It is not a balance that grows.

Expected DPs to solve #140: `1.15 × 2^70 / 2^28 ≈ 5.06e12`.
Pool total so far: `≈ 3.9e8`. Ratio: **~13,000× more work remains.**

At your measured 93 DP/s, one machine needs **~1,700 years** of wall clock to
finish what the pool has not. The pool solves this only if it grows by orders
of magnitude, or gets very lucky (a real solve can land at 0.5×–2× the
expectation, and it only takes one tame/wild collision).

Cost side, for a 300 W rig at $0.15/kWh: ~2,628 kWh/yr ≈ **$394/yr**.

Expected return, using the pool as it stands today:
`0.3598% × 14 BTC × 7.63e-5 ≈ 3.8e-6 BTC` — a few tens of cents a year.

That is roughly a **1,000:1** cost-to-EV ratio. Run it because you find the
problem interesting and the hardware is already on, not because it pays. Do not
buy a GPU for this.

Two structural facts worth knowing before you commit serious uptime:

- **The pool went 0-for-1 on #135.** It was solved by an outsider in June 2026
  and swept; the pool recovered nothing and paid nothing. Payouts depend
  entirely on the operator's integrity — there is no escrow and no on-chain
  enforcement, and the v1.5 TAME/WILD split deliberately makes the operator the
  only party who can ever hold a recovered key.
- **Share is a moving denominator.** Your 0.36% is high because the pool is
  small. If it grows 10×, you are 0.036% of a prize that is 10× closer.

---

## 3. The only irreversible mistake: the payout address

The pool docs are explicit: *"The server does not validate the address format
(so a typo will not bounce AUTH); it just uses the string as a key."* The client
only enforces `[A-Za-z0-9_.-]{1,64}`. Everything you earn can be silently
credited to a string nobody controls.

Do this once, before the next long run:

```bash
python3 ops/validate-worker.py 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP
```

Then the test that actually matters — **prove you can spend**:

1. Send a few thousand sats to the address from any wallet.
2. Send it back out to a different address you control.
3. If the outbound transaction confirms, you own the key. If it does not, do
   not use this address.

Also:

- **Not an exchange deposit address.** Exchanges rotate deposit addresses and
  may refuse or misroute an inbound payout. `36…` is a P2SH address, the format
  most exchange deposit addresses use — confirm it came from your own wallet.
- **Back up the seed** before you commit months of GPU time. Lost seed = lost
  share, exactly like a lost wallet.
- **Do not change it casually.** Accrual is keyed on the literal string. A new
  address starts a new bucket at zero.
- The address is public and shows on the leaderboard. That is fine — it cannot
  be spent from without your key. It is not a secret; treat it as a username.

---

## 4. Start it and keep it up

### Linux (recommended for a dedicated rig)

```bash
# 1. get a release you trust, verify the hash against the GitHub release page,
#    extract it, then:
sudo ./ops/install-systemd.sh \
    --worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP \
    --binary /opt/src/collider/collider

journalctl -u collider-pool.service -f
```

The unit starts after `network-online.target` and `time-sync.target` (AUTH
rejects >30 s clock drift), restarts forever on crash, and stops with SIGTERM
so queued DPs flush.

### Windows

```powershell
.\ops\Install-ColliderTask.ps1 `
    -Worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP `
    -ColliderPath C:\collider\collider.exe `
    -PreventSleep
```

Registers a boot-time SYSTEM task with restart-on-failure and no time limit.
`-PreventSleep` disables standby/hibernate on AC.

### macOS

Apple Silicon runs the Metal backend. Use launchd so it survives logout:

```xml
<!-- ~/Library/LaunchAgents/com.collider.pool.plist -->
<key>ProgramArguments</key>
<array>
  <string>/Applications/collider/collider</string>
  <string>--pool</string><string>jlps://pool.collisionprotocol.com:17403</string>
  <string>--worker</string><string>36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP</string>
  <string>--no-tui</string>
</array>
<key>KeepAlive</key><true/>
<key>RunAtLoad</key><true/>
<key>StandardOutPath</key><string>/tmp/collider.log</string>
```

Then `launchctl load ~/Library/LaunchAgents/com.collider.pool.plist`.
A laptop is a poor rig: thermals throttle the walk rate and lid-close drops
the TLS session (the client reconnects, but you lose the gap).

### No service manager (tmux / WSL / weird hosts)

```bash
COLLIDER_BIN=/opt/collider/collider nohup ./ops/watchdog.sh >/dev/null 2>&1 &
```

Restarts on exit, rotates the log at 100 MB, and restarts if the log goes silent
for 30 min (`STALE_SECS`, `RESTART_ON_STALE=0` to warn only).

### Config file

`ops/config.yml` is a ready pool config. Put it beside the binary (or in
`~/.collider/config.yml` / `%USERPROFILE%\.collider\config.yml`). CLI flags win
over it. Run once with `--debug` to see the resolved configuration.

---

## 5. Sleep, reboots, Windows Update

| Event | Effect | Mitigation |
| --- | --- | --- |
| System sleep / lid close | TLS dies; client notices on next write and reconnects; one chunk reissue | `-PreventSleep` (Win), `systemctl mask sleep.target` (Linux desktop), `caffeinate` (mac) |
| Reboot | Worker resumes with same identity; unflushed DPs lost | boot-time service/Task; `systemctl enable` |
| Windows Update | Unexpected reboot, hunt offline until login | Set active hours; the task is `AtStartup` so it returns without a login |
| Clock drift > 30 s | `AUTH_FAIL`, then exit | NTP on; `timedatectl set-ntp true` / `w32tm /resync` |
| Suspend/resume cycles | Reconnects fine, but accrual pauses | keep the box awake |

DPs already `DP_ACK`-ed are credited server-side and survive any restart. Only
in-flight work is lost.

---

## 6. Updates and version pinning

The client can self-update in pool mode. That keeps you compatible (the v1.5
protocol bump hard-refused every v1.4 client), but it also means a binary you
did not inspect replaces one you did.

Pick one and be explicit:

- **Default (recommended if you are not auditing):** leave auto-update on. You
  get protocol compatibility automatically; accept the supply-chain risk.
- **Pinned:** add `--no-update` and check
  [releases](https://github.com/hevnsnt/collider/releases) yourself. If you let
  it drift behind a protocol bump, you will see `AUTH_FAIL: UPGRADE_REQUIRED`
  and stop earning silently.

Whichever you choose, verify the release artifact hash before first run, and
know that a self-updating binary running as root/SYSTEM is a real trust
decision, not a formality.

---

## 7. Monitoring: is it actually earning?

Three independent checks; any one failing means you are offline:

1. **Process** — `systemctl status collider-pool` / `Get-ScheduledTaskInfo`.
2. **Log growth** — with `--verbose` the client writes per-DP lines every few
   seconds. A file untouched for 10+ minutes is wedged, not idle.
3. **Dashboard** — `https://collisionprotocol.com/dashboard?worker=36vjp…`.
   DPs should climb; the hourly chart should never say "No submissions in this
   window" while you believe you are running.

Sanity check of the number itself: `DPs ÷ uptime` should be stable around your
GPUs' capability. A sudden 10× rise means something is wrong (usually a
misreported assignment, which is the pool's problem, not yours); a fall to zero
means the socket or the GPU died.

---

## 8. Stopping cleanly

```bash
systemctl stop collider-pool        # SIGTERM, flushes queued DPs, 30s grace
```

Never `SIGKILL` (`taskkill /F`) casually — you lose whatever is queued. On
restart the server treats you as the same worker as long as `--worker` is
unchanged, and your accrued total continues.

---

## 9. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `AUTH_FAIL` immediately | clock drift > 30 s | `sudo systemctl restart systemd-timesyncd`, `w32tm /resync` |
| `AUTH_FAIL: UPGRADE_REQUIRED` | client is v1.4.x | update to ≥ v1.5.0 |
| TLS "missing trust anchor" | no CA bundle | `apt install ca-certificates` |
| Disconnects then exits after 3 tries | malformed worker name or IP ban | check the address; check anti-cheat status |
| Permanent IP ban | a DP submitted with the wrong tame/wild type | do **not** patch the client to work around it — that is what the check exists to catch |
| `nvidia-smi` shows nothing | driver/CUDA missing | install driver 12.x; client needs a CUDA device |
| Dashboard stuck at old number | process died, or you are looking at a cached tab | hard-refresh; check process |
| Disk filling | verbose logs | `watchdog.sh` rotates at 100 MB; logrotate for systemd |

The pool itself has run out of disk before under heavy submission volume, so if
every worker stalls at once it is probably not you.

---

## 10. Security and phishing

Nothing about joining this pool requires a secret. Memorise the rule:

> **Anyone asking for a seed phrase, private key, keystore file, or "wallet
> validation" signature is stealing from you.** No legitimate pool, support
> agent, or dashboard needs them.

Specifically:

- The pool only ever needs the **public address**. There is no sign-up.
- `--worker-key` exists in the client for signing proofs, but you do not need
  it for the public pool. Don't create or store a key file "for verification"
  because a stranger asked.
- Expect DMs: "your payout is pending, connect wallet", fake airdrops, fake
  "Collision Protocol support" accounts, and wallet-drainer sites that mirror
  the dashboard. Type the domain yourself; do not follow links from DMs.
- The free client is MIT and auditable. The Pro build is closed-source, adds a
  100M-address funded-wallet bloom filter, and should never touch a machine
  holding keys you care about.
- Run the worker as a dedicated user, not root/Administrator, if you can.

---

## 11. Scope of this kit

This repo (`realwalletscanner`) is a Windows WPF wallet-recovery/scanning
application. It is **not** a kangaroo solver and cannot contribute DPs, so
nothing here was wired into it.

Deliberately not done: connecting `Core/ExchangeHackKeys.cs`,
`Core/BrainWalletScanner.cs`, the funded-address bloom filter, or the ERC-20
auto-sweep path to this or anything else. Those exist to find and move other
people's coins. The puzzle is a sanctioned target; a live wallet someone else
holds keys for is not. Keeping them in separate universes is the whole point.

What this kit does contain:

```
ops/validate-worker.py        offline base58check/bech32 address verification
ops/config.yml                pinned pool config
ops/collider-pool.service     systemd unit (restart forever, waits for clock sync)
ops/install-systemd.sh        installs binary + unit + env, validates address first
ops/watchdog.sh              process/log watchdog for non-systemd hosts
ops/Install-ColliderTask.ps1  Windows scheduled task + optional prevent-sleep
ops/HUNT-OPS.md               this file
```

---

## Bottom line

Keep it running if you like the hunt: it costs electricity and a little
attention, and the upside is a lottery ticket with a real (if small) chance of
a 0.05 BTC payout. Fix the two things that actually matter — **prove you can
spend from that address**, and **stop letting the box go offline between
sessions** — and let it sit. Do not spend money on hardware for it, and do not
hand anyone a seed phrase to "claim" anything.
