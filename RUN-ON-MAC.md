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
| **App** | `/Applications/CodeHaven Workstation.app` (~250 MB, no system Node.js needed afterwards) |
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
crypto dependencies, makes an `.icns` from `wallet.ico`, builds with `electron-builder`,
ad-hoc signs the bundle, copies it to `/Applications`, clears the quarantine flag, and
optionally registers the login item.

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
| Port already in use | The launcher walks 2525 → 3100–3104 automatically; override with `CODEHAVEN_PORTS=4000,4001` |
| Blank window | ⌘R reloads; the window reloads itself automatically after a scanner restart |
| Scanner restart keeps failing | After 5 attempts (1 s → 15 s backoff) the app shows an error with the log path |
| Two copies running | Prevented by a single-instance lock — launching again just focuses the first window |
| Slow scanning | Up to 8 worker threads; close other heavy apps |

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
