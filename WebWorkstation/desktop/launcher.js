'use strict';

/**
 * CodeHaven Workstation — shared desktop bootstrap.
 *
 * Responsibilities:
 *   1. Locate the web workstation directory (dev checkout or packaged .app).
 *   2. Reuse an already-running workstation instead of spawning a duplicate.
 *   3. Spawn `server.js` with an isolated user-data directory and loopback bind.
 *   4. Wait until the HTTP API actually answers before opening a window.
 *   5. Supervise the child: restart it automatically if it ever exits early.
 *
 * The server is spawned with ELECTRON_RUN_AS_NODE=1 so a packaged build never
 * depends on the user having a system-wide Node.js installation.
 */

const { spawn } = require('child_process');
const fs = require('fs');
const http = require('http');
const net = require('net');
const os = require('os');
const path = require('path');

const DEFAULT_PORTS = [2525, 3100, 3101, 3102, 3103, 3104];

function exists(p) {
  try { fs.accessSync(p); return true; } catch (_) { return false; }
}

/** Directory that contains server.js + index.html. */
function resolveServerDir() {
  const candidates = [
    process.env.CODEHAVEN_SERVER_DIR,
    // Packaged: <App>.app/Contents/Resources/workstation
    process.resourcesPath ? path.join(process.resourcesPath, 'workstation') : null,
    // Dev checkout: desktop/ lives inside WebWorkstation/
    path.join(__dirname, '..'),
    process.cwd()
  ].filter(Boolean);

  for (const dir of candidates) {
    if (exists(path.join(dir, 'server.js')) && exists(path.join(dir, 'index.html'))) return dir;
  }
  throw new Error('Unable to locate the WebWorkstation directory (server.js + index.html).');
}

/** Node-compatible executable: the Electron binary in "run as node" mode, or plain node. */
function resolveNodeExecutable() {
  const isElectron = Boolean(process.versions && process.versions.electron);
  return {
    execPath: process.execPath,
    isElectron,
    env: isElectron ? { ELECTRON_RUN_AS_NODE: '1' } : {}
  };
}

function getJson(port, pathname, timeoutMs = 900) {
  return new Promise((resolve) => {
    const req = http.get(
      { host: '127.0.0.1', port, path: pathname, timeout: timeoutMs },
      (res) => {
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (c) => { if (body.length < 200000) body += c; });
        res.on('end', () => resolve({ status: res.statusCode, body }));
      }
    );
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.on('error', () => resolve(null));
  });
}

async function isWorkstation(port) {
  const res = await getJson(port, '/api/brain-status');
  if (!res || res.status !== 200) return false;
  return res.body.includes('{');
}

/** Live counters for the menu-bar tray (falls back to null when unavailable). */
async function getDesktopStatus(port) {
  const res = await getJson(port, '/api/desktop-status', 700);
  if (!res || res.status !== 200) return null;
  try { return JSON.parse(res.body); } catch (_) { return null; }
}

async function isPortFree(port) {
  return new Promise((resolve) => {
    const srv = net.createServer();
    srv.once('error', () => resolve(false));
    srv.once('listening', () => srv.close(() => resolve(true)));
    srv.listen(port, '127.0.0.1');
  });
}

/** Reuse a workstation that is already serving, otherwise pick a free port. */
async function findExisting(ports = DEFAULT_PORTS) {
  for (const port of ports) {
    // eslint-disable-next-line no-await-in-loop
    if (await isWorkstation(port)) return port;
  }
  return null;
}

async function pickPort(ports = DEFAULT_PORTS) {
  for (const port of ports) {
    // eslint-disable-next-line no-await-in-loop
    if (await isPortFree(port)) return port;
  }
  return new Promise((resolve) => {
    const srv = net.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolve(port));
    });
  });
}

function defaultDataDir() {
  if (process.env.CODEHAVEN_DATA_DIR) return path.resolve(process.env.CODEHAVEN_DATA_DIR);
  const home = os.homedir();
  if (process.platform === 'darwin') {
    return path.join(home, 'Library', 'Application Support', 'CodeHaven Workstation');
  }
  if (process.platform === 'win32') {
    return path.join(process.env.APPDATA || path.join(home, 'AppData', 'Roaming'), 'CodeHaven Workstation');
  }
  return path.join(process.env.XDG_DATA_HOME || path.join(home, '.local', 'share'), 'codehaven-workstation');
}

function waitForWorkstation(port, { timeoutMs = 45000, intervalMs = 250 } = {}) {
  const startedAt = Date.now();
  return new Promise((resolve, reject) => {
    const tick = async () => {
      if (await isWorkstation(port)) return resolve(true);
      if (Date.now() - startedAt > timeoutMs) {
        return reject(new Error(`Workstation did not answer on 127.0.0.1:${port} within ${Math.round(timeoutMs / 1000)}s`));
      }
      setTimeout(tick, intervalMs);
    };
    tick();
  });
}

function readLogTail(file, lines = 25) {
  try {
    const text = fs.readFileSync(file, 'utf8');
    return text.trim().split('\n').slice(-lines).join('\n');
  } catch (_) {
    return '';
  }
}

/**
 * Start (or attach to) the workstation.
 * @returns {Promise<{url:string, port:number, spawned:boolean, stop:Function, logFile?:string}>}
 */
async function startWorkstation(options = {}) {
  const envPorts = String(process.env.CODEHAVEN_PORTS || '')
    .split(',')
    .map((p) => Number(p.trim()))
    .filter((p) => Number.isInteger(p) && p > 0 && p < 65536);

  const {
    ports = envPorts.length ? envPorts : DEFAULT_PORTS,
    dataDir = defaultDataDir(),
    log = () => {},
    onOutput = null,
    autoRestart = true,
    maxRestarts = 5,
    onRestart = null,
    onGiveUp = null
  } = options;

  fs.mkdirSync(dataDir, { recursive: true });

  const existing = await findExisting(ports);
  if (existing) {
    log(`Attached to workstation already running on 127.0.0.1:${existing}`);
    return {
      port: existing,
      url: `http://127.0.0.1:${existing}/?desktop=1`,
      spawned: false,
      dataDir,
      getStatus: () => getDesktopStatus(existing),
      stop: async () => {}
    };
  }

  const serverDir = resolveServerDir();
  // A stable port keeps the window URL valid across automatic restarts.
  const port = await pickPort(ports);
  const { execPath, env: execEnv } = resolveNodeExecutable();
  const logFile = path.join(dataDir, 'workstation.log');
  const logStream = fs.createWriteStream(logFile, { flags: 'a' });

  let child = null;
  let stopping = false;
  let restarts = 0;
  let lastExit = null;

  const spawnChild = () => {
    const c = spawn(execPath, [path.join(serverDir, 'server.js')], {
      cwd: serverDir,
      env: {
        ...process.env,
        ...execEnv,
        PORT: String(port),
        HOST: '127.0.0.1',
        CODEHAVEN_DATA_DIR: dataDir,
        NODE_ENV: process.env.NODE_ENV || 'production',
        // Keep the spool off the HTTP port in every desktop flavour.
        LOCAL_SMTP_PORT: process.env.LOCAL_SMTP_PORT || '2526'
      },
      stdio: ['ignore', 'pipe', 'pipe']
    });

    const relay = (chunk, stream) => {
      const text = chunk.toString();
      try { logStream.write(text); } catch (_) {}
      if (onOutput) onOutput(text, stream);
    };
    c.stdout.on('data', (d) => relay(d, 'stdout'));
    c.stderr.on('data', (d) => relay(d, 'stderr'));

    c.on('exit', (code, signal) => {
      lastExit = { code, signal, at: Date.now() };
      if (stopping) {
        log(`Workstation stopped (${signal || `exit ${code}`})`);
        return;
      }
      log(`Workstation exited unexpectedly (${signal || `exit ${code}`})`);
      if (!autoRestart || restarts >= maxRestarts) {
        log(`Giving up after ${restarts} restart attempts`);
        if (onGiveUp) onGiveUp({ code, signal });
        return;
      }
      restarts += 1;
      const delay = Math.min(1000 * 2 ** (restarts - 1), 15000);
      log(`Restarting in ${delay}ms (attempt ${restarts}/${maxRestarts})`);
      setTimeout(async () => {
        if (stopping) return;
        try {
          child = spawnChild();
          await waitForWorkstation(port, { timeoutMs: 45000 });
          restarts = 0;
          log('Workstation restarted');
          if (onRestart) onRestart({ port, url: `http://127.0.0.1:${port}/?desktop=1` });
        } catch (err) {
          log(`Restart failed: ${err.message}`);
        }
      }, delay);
    });

    return c;
  };

  child = spawnChild();

  try {
    await waitForWorkstation(port);
  } catch (err) {
    stopping = true;
    try { child.kill('SIGTERM'); } catch (_) {}
    const tail = readLogTail(logFile);
    throw new Error(`${err.message}${tail ? `\n\n${tail}` : ''}`);
  }

  log(`Workstation started on 127.0.0.1:${port} (data: ${dataDir})`);

  const flushLogs = () => new Promise((resolve) => {
    try { logStream.end(() => resolve()); } catch (_) { resolve(); }
  });

  const stop = () => new Promise((resolve) => {
    stopping = true;
    if (!child || child.exitCode !== null || child.signalCode !== null) {
      return flushLogs().then(resolve);
    }
    let settled = false;
    const done = async () => {
      if (settled) return;
      settled = true;
      await new Promise((r) => setTimeout(r, 120));
      await flushLogs();
      resolve();
    };
    child.once('exit', done);
    try { child.kill('SIGTERM'); } catch (_) {}
    setTimeout(() => {
      try { if (child.exitCode === null) child.kill('SIGKILL'); } catch (_) {}
      done();
    }, 6000);
  });

  return {
    port,
    url: `http://127.0.0.1:${port}/?desktop=1`,
    spawned: true,
    dataDir,
    logFile,
    get child() { return child; },
    get pid() { return child ? child.pid : null; },
    getStatus: () => getDesktopStatus(port),
    lastExit: () => lastExit,
    stop
  };
}

module.exports = {
  DEFAULT_PORTS,
  resolveServerDir,
  startWorkstation,
  findExisting,
  defaultDataDir,
  isWorkstation,
  getDesktopStatus,
  readLogTail
};
