# Running the hunt on MacBooks

Practical playbook for Apple Silicon. Short version: it works, but a MacBook is
the least forgiving rig for a job that wants to run for weeks. Three things will
end your session, and none of them are the pool — the **lid**, **idle sleep**,
and a **reboot with FileVault on**.

---

## 0. Before anything: is it worth it on this hardware

Your historical rate is ~93 DP/s, which implies ~25 GKeys/s. That is not
MacBook territory — a 4090-class card alone is ~8 GKeys/s and Apple GPUs are a
fraction of that for 256-bit EC math. Whatever produced those 1.40M DPs, a
MacBook fleet will be slower.

Measure instead of guessing, on each machine:

```bash
./collider --benchmark --benchmark-time 60
```

Then convert what the worker actually reports in `--verbose` mode:
`DPs ÷ seconds = DP/s`, and `DP/s × 2^28 ≈ keys/s` at `dp_bits 28`.

If a MacBook lands at 5 DP/s, it contributes ~0.05% of what your old rig did.
Run it because the laptop sits idle on a desk anyway, not as a replacement for
real hashpower.

---

## 1. Get the right build

Apple Silicon has no CUDA. Use the **Metal** build:

- `collider-macos-arm64.tar.gz` from the [v1.5.6 release](https://github.com/hevnsnt/collider/releases/tag/v1.5.6)
- Verify the hash against the release page before running it.

macOS quarantines anything downloaded through a browser, and launchd cannot
exec a quarantined binary. The installer clears the flag, but if you run it by
hand first:

```bash
xattr -d com.apple.quarantine ./collider
```

If Gatekeeper still refuses: System Settings → Privacy & Security → **Open
Anyway**, then try again. Confirm it sees the GPU:

```bash
./collider --benchmark --benchmark-time 30
```

If Metal is not auto-detected, force it with `--backend metal`. A `cpu` backend
fallback means it silently runs ~100× slower — check the startup banner.

---

## 2. Install it as a launchd job

Two modes. **Daemon** is what you want for an always-on hunt.

```bash
# from the repo: git fetch origin arena/118369ec-realwalletscanner && git checkout FETCH_HEAD -- ops
sudo ./ops/install-launchd.sh \
    --worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP \
    --binary /Users/progressive/puzzle/collider \
    --user progressive \
    --prevent-sleep
```

That installs to `/usr/local/collider`, runs as **you** (never root), restarts
on exit with a 60 s throttle, and wraps the worker in
`caffeinate -i -s` so the assertion lives exactly as long as the job.

**Agent mode** (no sudo, everything under `~/.collider`) if the daemon cannot
open a Metal device — some macOS versions restrict GPU access from the system
domain:

```bash
./ops/install-launchd.sh --agent \
    --worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP \
    --binary ~/puzzle/collider
```

An agent dies at logout, so pair it with auto-login if you go that way.

Check it took:

```bash
tail -f /usr/local/var/log/collider.out.log     # expect AUTH_OK, then WORK_ASN
pmset -g assertions | grep -i caffeinate        # assertion should be listed
```

---

## 3. Sleep: what actually works

| What you do | Lid open | Lid closed |
| --- | --- | --- |
| Nothing | sleeps on idle timer | sleeps immediately |
| `pmset sleep 0` | stays awake | **still sleeps** |
| `caffeinate -i -s` | stays awake (assertion) | **still sleeps** |
| clamshell mode (power + display + keyboard) | — | stays awake |
| `sudo pmset -a disablesleep 1` | stays awake | stays awake |

`caffeinate` creates a power *assertion*, and no assertion overrides a lid
close — that is an explicit sleep request, not idleness. Only `disablesleep`
(or real clamshell mode) covers it.

Also worth knowing:

- **`caffeinate -s` is AC-only.** On battery it silently does nothing. Keep
  MacBooks plugged in; the runbook's wrapper passes `-i -s` for that reason.
- **`disablesleep` persists across reboots** and is not tied to any process.
  Set it, forget it, and the machine will happily cook in a backpack. Always
  pair it with `sudo pmset -a disablesleep 0` when you are done.
- Check state with `pmset -g | grep SleepDisabled` (`1` = sleep disabled).
  Note the setting is reported as `SleepDisabled`, not as the word you typed.

### The lid-closed recommendation

**Don't.** On a MacBook the lid, keyboard deck and bottom case are all part of
the thermal path. A `collider` worker saturates the GPU for hours, and with the
lid shut that heat has nowhere to go: you will hit thermal throttling hard and,
over weeks, stress the battery.

Instead:

- Lid **open**, machine on a hard stand or a laptop cooler, in a cool room.
- Screen allowed to blank (`--prevent-sleep` sets `displaysleep 15`) — a dark
  screen does not stop the worker and saves a few watts.
- If you truly must close it, use `--lid-closed` and accept the throttle. Watch
  `sudo powermetrics --samplers smc -i 5000 -n 3` for `CPU die temperature` and
  the thermal pressure line; sustained "heavy" pressure means you are losing
  most of your throughput to heat.

---

## 4. The reboot trap: FileVault

This is the one that bites people who think they are covered by `KeepAlive`.

On a FileVault-encrypted Mac, a reboot leaves the machine at the **pre-boot
unlock screen**. macOS has not started, launchd has not run, and your worker
does not come back until a human types the password. An unattended reboot can
cost you days of uptime while everything looks "installed correctly".

Options, in order of preference for a dedicated rig:

1. **Schedule restarts when you are around** (software updates, power cuts).
2. **`sudo fdesetup authrestart`** — asks for your password once, reboots, and
   auto-unlocks at boot. Use this for planned restarts:
   `sudo fdesetup authrestart -delayminutes 1`
3. **Turn FileVault off** on a machine that only ever mines and holds no data
   you care about. Trade-off: physical theft becomes a total loss. Do not do
   this on your daily driver.

Also disable unattended auto-updates (System Settings → General → Software
Update → Automatic Updates), or a 3 a.m. install will reboot you and leave the
box sitting at the login screen.

---

## 5. Battery and long-term health

Running 24/7 plugged in at 100% is the fastest way to age a battery.

- Enable **Optimized Battery Charging** (System Settings → Battery), or the
  built-in 80% charge limit on models that have it.
- Third-party: AlDente can hold a specific charge level.
- Keep the machine out of direct sun and off soft surfaces (beds, cushions).
- A MacBook on a desk at 100% charge for a year will typically lose a
  noticeable chunk of capacity — decide whether that is a fair price for the
  DP share you are earning. (Given §0, it usually is not.)

---

## 6. Running several MacBooks

Good news: **the accrual unit is the address, not the machine.** The pool docs
are explicit — the same `worker_name` summed across every machine you run is one
bucket. So point every MacBook at the same `--worker` address and their DPs add
up in a single rank.

What to expect:

- Each connection gets its own chunk and its own TAME/WILD assignment
  (server round-robins), so two MacBooks do not duplicate each other's walks.
- No configuration is needed for this. Do not invent a suffix per machine —
  that splits you into separate workers each with a smaller share.
- Start them a few seconds apart so they do not all reconnect on the same
  schedule after a pool restart.
- Track per-machine DP rate in the logs. A machine that is 5× slower than its
  twin is being throttled — check `pmset -g therm` and dust.

A simple fleet check, run from any of them:

```bash
for h in mac1 mac2 mac3; do
  printf '%s  ' "$h"
  ssh "$h" 'tail -3 /usr/local/var/log/collider.out.log | grep -oE "dps[=: ]+[0-9.]+" | tail -1' 2>/dev/null || echo unreachable
done
```

(Adapt to whatever metric line your build prints; the point is a single
per-machine number you can compare.)

---

## 7. Network

Long-lived TLS sockets and Wi-Fi power management do not love each other.
The client reconnects with backoff, so a drop costs you a chunk reissue rather
than a ban — but on a laptop that naps, drops are frequent.

- Prefer **Ethernet** (USB-C adapter) on machines that stay put.
- If Wi-Fi only, keep the machine awake (§3) — most Wi-Fi drops correlate with
  sleep, not with signal.
- `AUTH_FAIL` after a long idle period is usually clock drift, not network:
  check `sudo sntp -sS time.apple.com`.

---

## 8. Monitoring

```bash
# is the job alive?
sudo launchctl print system/com.collider.pool | head -20

# is it working? (log should grow every few seconds with --verbose)
tail -f /usr/local/var/log/collider.out.log

# is it earning? (the only number that matters)
open "https://collisionprotocol.com/dashboard?worker=36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP"
```

Silence in the log is not idleness — it means the socket or the GPU died. The
launchd `KeepAlive` will restart the process within 60 s, so check timestamps in
`collider.err.log` for a restart loop.

---

## 9. macOS checklist

- [ ] Metal (arm64) build, hash verified, quarantine cleared
- [ ] `--benchmark` run; DP/s recorded per machine
- [ ] Address checksum validated (`ops/validate-worker.py`)
- [ ] Round-trip spend proven for the payout address
- [ ] Installer run with `--prevent-sleep`
- [ ] Lid open policy decided; `pmset -g | grep SleepDisabled` known
- [ ] FileVault restart plan chosen (`fdesetup authrestart` or scheduled)
- [ ] Auto-updates off, or reboot window scheduled when you are present
- [ ] Battery charging optimised, machine on a hard surface
- [ ] Dashboard DP count climbing after 5 minutes

---

## 10. When to stop

If the DP rate is low and the share is not growing meaningfully, the honest
answer is to let the MacBook be a laptop. The pool needs ~5.06 trillion DPs for
#140 against ~389M done; a few laptops adds noise, not progress. Keep one
machine running if you enjoy watching it, and do not let five laptops cook
themselves for a lottery ticket.
