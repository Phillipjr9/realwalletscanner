# REA Setup on Your Mac — the "X-ray machine" for software

REA (from [github.com/morluto/rea](https://github.com/morluto/rea)) lets an AI
agent **take apart any app or program and show the evidence** — machine code,
smart-contract bytecode, live running processes — instead of guessing.

This is the simple install for your Mac. Everything here is optional tooling;
the wallet scanner does not depend on any of it.

> **One rule:** only point it at software **you own or have permission to
> analyze**. That is also where it is most useful to you.

---

## 1. What you need first

- **Node.js 22.19+** (you already have it — the scanner needs it too)
- A terminal

## 2. Install REA (2 minutes)

```bash
npm install -g rea-agents frida
```

That installs REA plus **Frida** (the "watch a live app while it runs" tool) in
one go. If you already installed rea-agents earlier, just run
`npm install -g frida` afterwards.

**To connect it to your AI agent** (Claude Code, Codex, Cursor, …):

```bash
npx rea-agents setup
```

Pick your agents, review the changes it proposes, approve, then **restart your
agent**. REA's tools now appear inside your agent.

For plain terminal use, the `rea` command is ready immediately:

```bash
rea --help
rea doctor        # health check — see step 6
```

## 3. Add the decompiler (the big X-ray)

Without this, REA still analyzes app code, contracts and live processes. With
it, you also get **"turn machine code back into readable code"**.

**Option A — Ghidra (free, made by the NSA):**

1. Download **Ghidra 12.1.x** from the official site: <https://ghidra-sre.org/>
   (the ready-made release zip, e.g. `ghidra_12.1.4_PUBLIC_20260921.zip` — NOT
   the GitHub "source code" download)
2. Unzip it, e.g. into `~/Downloads/`
3. Tell REA where it lives (add to `~/.zshrc` so it sticks):

```bash
export GHIDRA_INSTALL_DIR=~/Downloads/ghidra_12.1.4_PUBLIC   # folder name may differ
```

Ghidra needs **two extra things** on macOS that `rea doctor` will nag about:

- **A full JDK 21 or newer** (macOS often has an old JDK 17 left behind by other
  apps, and Ghidra refuses it):

```bash
brew install openjdk
echo 'export JAVA_HOME=/opt/homebrew/opt/openjdk/libexec/openjdk.jdk/Contents/Home' >> ~/.zshrc
echo 'export PATH="$JAVA_HOME/bin:$PATH"' >> ~/.zshrc
source ~/.zshrc
java -version    # should print 21 or newer
```

- **The native decompiler binary must be executable** (zip extraction on macOS
  sometimes drops the exec bit — doctor says "native decompiler was not found"):

```bash
chmod +x ~/Downloads/ghidra_12.1.4_PUBLIC/Ghidra/Features/Decompiler/os/mac_arm_64/decompile
```

If that path doesn't exist at all, re-extract the release zip — the file should
be inside it.

**Option B — Hopper (paid, friendlier app):** `npx rea-agents setup` can offer
to install it for you with your approval.

## 4. Optional extras (only if you want them)

**ELF layout analysis** (sections, symbols, security flags of native binaries):

```bash
python3 -m venv ~/rea-py
~/rea-py/bin/pip install pwntools==4.15.0 pyelftools==0.33 unicorn==2.1.2
export REA_PWNTOOLS_PYTHON=~/rea-py/bin/python     # also in ~/.zshrc
```

**rizin** (extra disassembly engine — very easy with Homebrew):

```bash
brew install rizin
```

## 5. Keep it on ~/.zshrc

By the end your `~/.zshrc` may have up to three lines like:

```bash
export GHIDRA_INSTALL_DIR=~/ghidra/ghidra_12.1_PUBLIC
export REA_PWNTOOLS_PYTHON=~/rea-py/bin/python
# (frida and rizin need no env vars)
```

Open a new terminal (or `source ~/.zshrc`) afterwards.

## 6. Check everything

```bash
rea doctor
```

Green = ready. Anything you skipped (step 4) simply stays unavailable — that's
fine.

## 7. Things to ask your agent once it's connected

- "Analyze this app and show me, with evidence, how feature X works."
- "Check that the Mac app I'm about to ship matches the code in the repo."
  (`compare-source-to-bundle` — supply-chain check for every release)
- "Here is smart-contract bytecode — what functions does it expose?"
  (great for telling **spendable vs contract-locked** funds)
- "Open my scanner with the inspector on and watch what it actually loads."

Every answer comes back with **hashes, file locations and stated limitations** —
if REA doesn't know something, it says so instead of inventing it.

---

*Installed and proven working in the Arena sandbox on 2026-10-25: app-graph
analysis of this very scanner, native disassembly, EVM selector recovery, live
V8 capture of the running scanner, and static+runtime evidence reconciliation.*
