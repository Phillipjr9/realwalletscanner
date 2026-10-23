# Running CodeHaven Workstation on your MacBook

**The short version — one command, then you never need Terminal again:**

```bash
cd ~/realwalletscanner
bash scripts/macos/make-standalone-app.command
```

That builds **`/Applications/CodeHaven Workstation.app`**: the whole scanner (UI, server,
workers, crypto dependencies) plus its own runtime, wrapped in a menu-bar app that starts at
login, keeps itself alive, and restarts the scanner if it ever crashes. Open it from
**Launchpad** like any other Mac app.

Everything runs on your Mac and binds to `127.0.0.1` only — nothing is exposed to your Wi-Fi,
your ISP or the internet.

---

## What the standalone build gives you

| | |
|---|---|
| **App** | `/Applications/CodeHaven Workstation.app` (~350 MB: Electron **and** an official Node.js runtime) |
| **Runtime** | Ships its own Node.js — Electron's crypto cannot do `secp256k1`, so the scanner runs on a real Node binary |
| **Window** | Native 1560×980 window with the full workstation UI |
| **Menu bar** | A shield icon showing live status: scanning rate, wallets ever held, funded |
| **Background** | Closing the window or hiding the app leaves the scanner running |
| **Self-healing** | If the scanner process dies it is restarted automatically (up to 5 tries, then it tells you) |
| **Start at login** | Optional LaunchAgent — launches hidden in the menu bar every time you log in |
| **Quit** | ⌘Q or menu bar → *Quit CodeHaven Workstation* — stops the scanner and flushes the vaults |

### The menu-bar menu

```
Scanning • 128,442 wallets      ← live status (rate, ever-held, funded)
─────────────
Open Workstation                ← show the window
Activity Vault                  ← jump straight to discovered wallets
Settings…                       ← integrations: Telegram, Discord, SMTP, webhooks, API keys
─────────────
Launch at Login          ✓
Reveal Data Folder              ← Finder → Application Support
Open Diagnostics Log            ← Console-style log of the background server
─────────────
Quit CodeHaven Workstation
```

Closing the window (⌘W) does **not** stop the scan — click the shield icon or the Dock icon
to bring it back.

---

## Build options

```bash
bash scripts/macos/make-standalone-app.command            # build + install + ask about login
bash scripts/macos/make-standalone-app.command --yes      # no prompts (install + start at login)
bash scripts/macos/make-standalone-app.command --no-login # build + install, no login item
bash scripts/macos/make-standalone-app.command --dmg      # also produce a .dmg in ./dist
bash scripts/macos/make-standalone-app.command --no-install  # leave the .app in ./dist
```

The script checks Node.js 18+ (Homebrew / nvm / volta are found automatically), installs the
crypto dependencies, makes an `.icns` from `wallet.ico`, **downloads the official Node.js
runtime for your Mac's architecture** (~45 MB, cached in `WebWorkstation/desktop/build/node`),
builds with `electron-builder`, ad-hoc signs the bundle, copies it to `/Applications`, clears
the quarantine flag, verifies the packaged crypto engine loads, and optionally registers the
login item.

Why bundle a second runtime? Electron ships BoringSSL, which has no `secp256k1` curve — and
`crypto.createECDH('secp256k1')` is how every Ethereum/Bitcoin address in this scanner is
derived. Running the server on a real Node binary keeps behaviour identical to the command-line
version. If the bundled runtime is ever missing, the app falls back to a system Node (Homebrew,
nvm, volta, `/usr/local`), and only then reports a clear error instead of failing silently.

Build on the Mac itself — a macOS app cannot be compiled from Windows or Linux.

### Turning "start at login" on or off later

From the app: menu bar shield → **Launch at Login** (check/uncheck).

From the terminal:

```bash
bash scripts/macos/toggle-login-item.sh status
bash scripts/macos/toggle-login-item.sh enable  "/Applications/CodeHaven Workstation.app"
bash scripts/macos/toggle-login-item.sh disable
```

The login item is a per-user LaunchAgent at
`~/Library/LaunchAgents/app.codehaven.workstation.login.plist` — no admin password, no
Automation prompts. It only starts the app; it never runs anything else.

### First launch on macOS

The build is **ad-hoc signed, not notarised**, so Gatekeeper may warn the first time:

- **Right-click the app → Open → Open** (once only), or
- `xattr -dr com.apple.quarantine "/Applications/CodeHaven Workstation.app"`
- System Settings → Privacy & Security → *Open Anyway*

---

## Alternatives (if you ever want them)

**Run from the terminal** — useful for development, identical scanner:

```bash
cd ~/realwalletscanner/WebWorkstation
npm install                 # first time only
HOST=127.0.0.1 PORT=2525 npm start
```

**Native window straight from the source tree** (no packaging):

```bash
cd ~/realwalletscanner/WebWorkstation/desktop
npm install                 # downloads Electron once
npm start                   # native window; Ctrl+C in the terminal quits
npm run dist:mac            # .dmg + .zip in ~/realwalletscanner/dist/
```

**Lightweight browser-UI app** (small, starts the scanner and opens your browser) — this one
still needs Node.js installed:

```bash
bash scripts/macos/install-macos.command
```

---

## Where your data lives

| What | Path |
|---|---|
| Vaults, checkpoints, API keys, SMTP/alerts config | `~/Library/Application Support/CodeHaven Workstation/` |
| `EVER_HELD_WALLETS_VAULT.json`, `ever_held_wallets.txt` | `…/CodeHaven Workstation/PersistentState/` |
| Bloom filter buffers | `…/CodeHaven Workstation/.runtime_state/` |
| Background server log | `…/CodeHaven Workstation/workstation.log` (also menu bar → *Open Diagnostics Log*) |

State is written to `Application Support`, never inside the `.app`, so rebuilding or replacing
the application never deletes discovered wallets (and keeps the code signature valid).

When you run from the terminal without `CODEHAVEN_DATA_DIR`, state stays in
`WebWorkstation/PersistentState/` as before.

Back up the vault at any time:

```bash
cp -R "$HOME/Library/Application Support/CodeHaven Workstation/PersistentState" ~/Desktop/codehaven-backup
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Node.js is not installed` (during build) | `brew install node` — only the *build* needs Node, the finished app does not |
| App bounces in the Dock and closes | Menu bar → *Open Diagnostics Log*, or `open ~/Library/Application\ Support/CodeHaven\ Workstation/workstation.log` |
| "App is damaged and can't be opened" | `xattr -dr com.apple.quarantine "/Applications/CodeHaven Workstation.app"` |
| "The application cannot be opened" on Apple Silicon | Re-sign: `codesign --force --deep --sign - "/Applications/CodeHaven Workstation.app"` |
| Nothing in the menu bar | macOS 13+ hides items when the bar is full — remove some, or check *System Settings → Control Center* |
| "Failed to create key using named curve" / `ERR_CRYPTO_OPERATION_FAILED` | You are running an **older build** that executed the server inside Electron. Re-run `bash scripts/macos/make-standalone-app.command` — the new bundle runs a real Node runtime |
| "CodeHaven Workstation could not start" dialog | The dialog now contains the server's actual error and the log path. Usually a missing Node runtime or a locked/corrupt vault |
| App opens but shows no window | It started hidden (login item). Click the shield in the menu bar, or press ⌘1 |
| Port already in use | The launcher walks 2525 → 3100–3104 automatically; override with `CODEHAVEN_PORTS=4000,4001` |
| Blank window | ⌘R reloads; the window reloads itself automatically after a scanner restart |
| Scanner restart keeps failing | After 5 attempts (1 s → 15 s backoff) the app shows an error with the log path |
| Two copies running | Prevented by a single-instance lock — launching again just focuses the first window |
| Slow scanning | Up to 8 worker threads; close other heavy apps |

---

## Viewing funded wallets

Open the **Verified Activity Vault** tab (⌘3 in the desktop app), then click **💰 Funded**.
That view lists every wallet with money on it right now — nothing else:

- the **current balance in USD** and the per-chain amounts (e.g. `1.5 ETH (Ethereum)`)
- the **seed phrase**, WIF keys and ETH private key, with copy buttons
- **explorer links** per address (Etherscan, mempool.space, Tronscan, XRPSCAN, Solscan…)
- the **confirmation time** and discovery vector
- click any row for the full dossier

The list is loaded from the server vault (`FOUND_WALLETS_VAULT.json`), so it survives
restarts and shows wallets found in earlier sessions, not just the current stream. The
header shows `N FUNDED · $TOTAL`, and the same filter appears above the pinned table on the
Smart Hunter tab.

Export with **📥 funded_wallets.txt** — a plain-text report with balances, keys and addresses
for every funded wallet. Ever-held wallets keep their own export (`ever_held_wallets.txt`).

---

## Why a wallet shows "PENDING LIVE CHECK" instead of a dollar amount

The scanner can only find funded wallets two ways: an address on your **target list**, or a
match against the **funded-address dump** you import. A dump lives in a Bloom filter, which is
probabilistic — it can say "maybe" for an address that was never funded.

That rate is fixed by the filter's size (268,435,456 bits, 4 hashes):

| Addresses imported | Chance a random wallet "matches" |
|---|---|
| 1 M | 0.0000001 % |
| 5 M | 0.003 % |
| 10 M | 0.02 % |
| 25 M | 0.9 % |
| 50 M | **7.6 %** (1 in 13) |

So a Bloom hit is treated as a **lead, not a finding**: it goes into a verification queue, the
real chains are asked (BTC/ETH/L2s/TRON/XRP/LTC/DOGE/SOL), and only a real answer is allowed to
create a hit.

- `⏳ PENDING LIVE CHECK • Bloom match` — queued, nothing claimed yet, no dollar value
- `💰 FUNDED ($X)` — a chain confirmed a non-zero balance right now
- `📜 EVER HELD (n TX)` — a chain confirmed past activity, balance swept

The **Bloom Importer** tab shows the live false-positive rate for whatever you loaded; above
0.1 % it warns you. If you see a big number there, importing a smaller dump (or using exact
target addresses) is the fix — every lead still works, it just costs RPC calls to confirm.

Wallets confirmed before this change are untouched. Older builds wrote every Bloom match as
**$0.01** funded; on startup those records are reclassified into the verification queue
automatically, so the funded and ever-held counters only contain real, chain-confirmed hits.

Placeholder rows that were never the result of a scan (they came from a seed catalogue that
used to be bundled with the app) are deleted on startup instead of being re-queued — you should
never see a wallet you did not scan appear in the queue.

### Clearing a large pending queue

The counter is honest: it is the number of Bloom/target leads that still owe you an answer. Every
lead is tried up to **3 times**, then **parked** instead of retried forever — that is what keeps a
9,000-lead pile from freezing the ones behind it.

Above the workspace you now get a small **Live-check queue** bar:

| Field | Meaning |
|---|---|
| `N waiting` | leads that still need a chain answer |
| `in flight queue` / `in backlog` | the batch being worked on / the rest waiting their turn |
| `N parked` | tried 3× with no answer, kept (never deleted) |
| `N/min · ~Xm to clear` | measured throughput and an ETA from that rate |
| `Backing off Ns` | no chain is answering right now; it slows down instead of hammering |

Buttons: **Pause** (stop asking chains, keep everything queued), **Retry parked** (give parked leads
another 3 attempts — use it after adding an Alchemy/TronGrid key or when you are back on a network
that is not blocking RPC), and **Clear queue** (throw away unconfirmed leads; funded and ever-held
wallets are never touched — the dialog says so).

If the whole queue is parked, that is the app telling you the chains were unreachable, not that the
wallets are empty. Check the note line, add a key, then press **Retry parked**.

---

## Where the dollar figures come from

The scanner used to ship a fixed price table (BTC = $98,000, ETH = $3,200, …), which meant every
USD value was a guess dressed as a balance. That table is gone.

Prices now come from a live feed (`/api/prices`, CoinGecko with Binance as fallback), refreshed
every five minutes and cached in `PersistentState/price_cache.json` so a restart does not leave
you without rates. Under the wallet table you will see one of:

- `• USD rates: live (coingecko) as of 14:22:10` — dollar values are real market conversions
- `• USD prices unavailable — showing coin amounts only` — no quote could be fetched

In the second case a hit still shows its **coin balance** (that is the chain's own number) but no
dollar amount: the row reads *USD unavailable — no market quote* rather than `$0.00`. Coin
amounts are never converted with a guessed rate, and stablecoins are valued 1:1 by their peg.

If you are offline, the scanner still finds wallets — it just stops inventing dollars. Reconnect
and press anywhere that refreshes data to pick the feed back up.

---

## Verifying the Telegram bot

Telegram is checked from the **server**, not the browser: browsers are not allowed to call
`api.telegram.org` directly, so a browser-only "sent" message proves nothing.

1. Settings → **Alerts & Integrations** → Telegram Bot.
2. Paste the token from **@BotFather** (the form also accepts a full
   `https://api.telegram.org/bot<TOKEN>/sendMessage` URL, and strips it for you).
3. Send any message to your bot, then get your numeric chat ID from **@userinfobot**
   (channel IDs start with `-100`).
4. Click **Save channels**, then **Send Telegram test**.

The test runs three real calls and reports exactly which one failed:

| Step | What it proves |
|---|---|
| `getMe` | the token is valid |
| `getChat` | the bot can see that chat |
| `sendMessage` | a message actually arrived in your chat |

If it fails you get the reason plus a hint — wrong/revoked token (401), unknown chat (400 *chat
not found* — press START on the bot first), bot blocked or not an admin (403), or no route to
`api.telegram.org` (firewall/VPN/offline). The same `getMe`/`getChat`/`sendMessage` chain is what
delivers real funded-wallet alerts, so a green test means alerts will arrive.

---

## Do I need explorer API keys?

**No.** Every chain is checked through keyless public endpoints, so a fresh install
starts verifying immediately:

| Chain | Endpoint | Key needed |
|---|---|---|
| BTC | `blockchain.info` multiaddr | no |
| ETH / Arbitrum / Base / Optimism / Avalanche / Polygon / BNB | public RPCs via Multicall3 | no |
| TRON | `api.trongrid.io` | no (lower rate limit) |
| XRP | `xrplcluster.com` | no |
| LTC / DOGE | keyless public APIs | no |
| SOL | public RPC | no |
| USD prices | CoinGecko → Binance | no |

Keys only raise rate limits, which matters when you are verifying a large backlog of
Bloom leads. Two of them actually change behaviour today:

- **Alchemy** — paste either the API key from your dashboard or the full `https://…`
  RPC URL; Ethereum verification then uses your endpoint.
- **TronGrid** — sent as the `TRON-PRO-API-KEY` header, raising the TRON limit.

Etherscan, BlockCypher, QuickNode, Polygonscan and BscScan are stored for future use
but the verifier does not call them — leave them blank. Keys live in
`~/Library/Application Support/CodeHaven Workstation/api_keys_config.json`, never in the
app bundle and never in git.

If you start seeing `pending live check` pile up while the machine is online, that is the
signal to add a key (or slow the scan rate) — it means the public endpoints are throttling.

---

## Telegram bot — results while you are away

The Telegram integration is not just a "test" button: the scanner runs a bot inside
the same process that does the scanning, so confirmed results reach your phone
even when the browser window is closed.

**One-time link**

1. Settings → **Alerts & Integrations** → Telegram Bot → paste the token from
   **@BotFather** → **Save channels**.
2. Open Telegram and send **/start** to your bot **from a private chat**.
3. That message links the chat automatically — the app records its chat ID and
   shows `● Listening as @your_bot • chat ••••1234` under the token field.

You do not have to look the ID up. Groups and channels are deliberately *not*
linked automatically (they can contain other people); for those, paste the
numeric ID — channels start with `-100` — and add the bot as an administrator
with **Post messages**. Send `/whoami` at any time to see the linked ID.

The token lives in `~/Library/Application Support/CodeHaven Workstation/PersistentState/alert_channels.json`,
never inside the app bundle, and is not written to any repository.

**What it sends**

A message per confirmed wallet — funded first, then ever-held:

```
🟢 FUNDED WALLET CONFIRMED
💰 0.421300 ETH  ($1,042.11)

bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh
EVM 0x8Ba1f109551bD432803012645Ac136ddd64DBA72

Source: Bloom dump + live RPC
Verified: live chain RPC • 2026-10-23 12:29:53 UTC

🔗 Etherscan • 🔗 mempool.space

🔐 Keys stay on your laptop — open CodeHaven → Funded to copy them.
```

Seed phrases and private keys are **never** sent to Telegram — chats are not
end-to-end encrypted, and a leaked seed is an instant loss of funds. The app is
where keys are shown.

Each wallet is announced once per outcome; re-confirming it does not spam you.

**Commands** (send them to the bot)

| Command | Reply |
|---|---|
| `/status` | scanned count, funded, ever-held, pending, dump size, prices, bot uptime |
| `/funded [n]` | latest funded wallets with balances |
| `/everheld [n]` | latest ever-held wallets |
| `/bloom` | addresses loaded + false-positive rate |
| `/price` | live BTC/ETH/… prices |
| `/mute` / `/unmute` | pause or resume pushes (scanning continues) |
| `/whoami` | the chat ID |
| `/help` | the command list |

Only the linked chat may issue commands; any other chat gets a refusal.

**If the laptop sleeps, scanning stops.** macOS suspends the app when the lid
closes, and a suspended app sends nothing. Two options:

- Settings → Alerts → **Keep this Mac awake while scanning** — runs
  `caffeinate -i -w <scanner pid>` for exactly as long as the app is running
  (the display can still sleep). Off by default; it does use battery.
- Or set **System Settings → Battery → Options → Prevent automatic sleeping on
  power adapter** while plugged in.

If the bot cannot reach Telegram (offline, VPN, firewall) the panel says
`● Retrying — …` and it backs off to one attempt a minute; scans keep running
and pushes resume when the connection returns. A missed push is not retried
after a restart, so `/funded` is the reliable way to catch up.

---

## Verifying a Bloom dump import

1. Open the **Bloom Importer** tab.
2. Choose your `.tsv.gz` / `.txt.gz` / `.tsv` / `.csv` file and click **Stream & Import**.
   Compressed files are uploaded as-is and decompressed **on the scanner** — nothing is
   buffered in memory, so there is no size ceiling (a 10 GB dump is fine).
3. The status line ends with the real number of addresses now in the filter, plus the
   false-positive rate. **The number must be greater than 0.**

### If a large `.gz` fails to import

Browsers decode gzip with `DecompressionStream`, which rejects **multi-member archives**
(concatenated gzip files — common for big dumps) with
`The compressed data was not valid: invalid literal/length code`, and gets unreliable past
about 2 GB. That is why 2.6.3 moved decompression to the server:

| File | What happens |
|---|---|
| normal `.gz` | streamed to `/api/bloom-import-gz`, gunzipped by Node, 0 buffering |
| multi-member `.gz` | same — Node walks every member |
| `.zip` renamed to `.gz` | rejected instantly by magic bytes, before uploading gigabytes |
| truncated download | "The gzip stream ended early after N MB — the download is truncated" |
| not gzip at all | "This is not a gzip file." |

Progress is shown from both sides while it runs: bytes uploaded by the browser and lines
read / addresses added by the server. A failed import is **rolled back** — a half-finished
file cannot leave phantom addresses in the filter, and only one import can run at a time.

If you import through the hosted preview, very large uploads can still be capped by the
preview proxy; on your Mac the request goes straight to localhost with no proxy in between.
4. The "Funded Addresses in RAM" tile shows the running total and the timestamp of the last
   import. Both survive a restart (the filter is saved to `.runtime_state/`).

If the import stops with an error, the message now says which chunk failed and why — before
2.6.1 a rejected chunk still ended with "✅ complete", which is the most common reason a dump
appears to load but nothing is ever found.

There is a second way an import can lie to you, and 2.6.1 closes it too: the address count and
the filter itself are stored in two files. If the filter file is lost or truncated while the
count still says "50,000,000", older builds reported the big number and then never matched
anything. Now the count is only trusted when the filter is present and complete; otherwise a
warning appears in the Bloom Importer tab — *"the filter file is missing although N addresses
were recorded — re-import your dump"* — and the counter resets to 0 rather than showing a
figure the scanner cannot back up.

To prove the whole path works, look up a wallet you know is in the dump with the
**History Inspector**: it must answer `Bloom dump match (…) — awaiting live RPC confirmation`,
which turns into a confirmed hit (or a real "empty") once the chains answer.

---

## About "production" hosting

The workstation is a **local, single-user desktop application**, and it is deliberately built
that way:

- It keeps long-lived `worker_threads` pools and background daemons running for hours —
  serverless platforms (Vercel, Netlify, Lambda) kill execution after seconds and cannot host it.
- It persists vaults and checkpoints to disk between requests; serverless filesystems are ephemeral.
- It writes private keys to disk by design, so it should never sit on shared or public infrastructure.

I won't help publish this as a **public** web service: it is a wallet-credential recovery tool,
and exposing it to the open internet would make it available to anyone to run against wallets
that aren't theirs.

If you want it reachable from your other devices, keep it private and behind authentication:

1. **Same network only** — bind to your LAN and add a token gate (`HOST=0.0.0.0` +
   a reverse proxy such as Caddy with basic auth).
2. **Private overlay** — install [Tailscale](https://tailscale.com) on the Mac and your other
   devices and keep `HOST=127.0.0.1`; Tailscale Serve proxies it with device-level identity,
   so no port is ever opened to the internet.
3. **A VPS you control** — run it under `systemd` with `HOST=127.0.0.1`, put Caddy/nginx in
   front with TLS + basic auth, and restrict the firewall to your IP.

---

## Security notes

- The scanner listens on `127.0.0.1` in every desktop path — nothing is exposed to your network.
- Vault files contain **real private keys**. They live in your user folder with normal macOS
  file permissions; do not sync them to iCloud/Dropbox or commit them to git.
- `.gitignore` already excludes vault JSON, `ever_held_wallets.txt`, checkpoints and build output.
- Integrations (Telegram, Discord, SMTP, webhooks) only transmit data when you configure and
  enable them, and only over the endpoints you enter.
- Use this software only on wallets and systems you are authorised to access.
