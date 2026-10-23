'use strict';

/**
 * CodeHaven Workstation — Electron main process (macOS / Windows / Linux).
 *
 * The UI is the existing web workstation served by server.js; this shell gives
 * it a native window, menu bar, Dock integration, a menu-bar status item and
 * login-item support so the scanner can run on its own in the background.
 */

const { app, BrowserWindow, Menu, Tray, nativeImage, dialog, shell } = require('electron');
const path = require('path');
const { startWorkstation, readLogTail } = require('./launcher');
const { icon1x, icon2x } = require('./trayIcon');

const APP_TITLE = 'CodeHaven Workstation';
const isDev = !app.isPackaged;
let START_HIDDEN = process.argv.includes('--hidden');

/**
 * macOS may launch the app from the login item without forwarding argv (newer
 * macOS uses SMAppService, which drops custom arguments). In that case
 * `wasOpenedAtLogin` is the only signal that we should stay in the menu bar.
 */
function shouldStartHidden() {
  if (START_HIDDEN) return true;
  try {
    const settings = app.getLoginItemSettings({ args: ['--hidden'] });
    if (settings && (settings.wasOpenedAtLogin || settings.wasOpenedAsHidden)) {
      START_HIDDEN = true;
    }
  } catch (_) { /* not supported on this platform */ }
  return START_HIDDEN;
}

let mainWindow = null;
let tray = null;
let workstation = null;
let quitting = false;
let statusTimer = null;
let lastStatus = null;

// A single running instance keeps one scanner alive (and avoids two copies
// fighting over the same vault files).
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    showWindow();
  });
  bootstrap();
}

function bootstrap() {
  app.setName(APP_TITLE);

  app.whenReady().then(start).catch(fatal);

  app.on('window-all-closed', () => {
    // The scanner is a background service: closing the window keeps it running
    // and leaves the menu-bar item in place. Quit from the app menu or tray.
    if (process.platform !== 'darwin') app.quit();
  });

  app.on('activate', () => showWindow());

  app.on('before-quit', async (event) => {
    if (quitting) return;
    event.preventDefault();
    quitting = true;
    stopStatusPolling();
    try { if (tray) tray.destroy(); } catch (_) {}
    try {
      if (workstation && workstation.spawned) await workstation.stop();
    } catch (_) {}
    app.exit(0);
  });
}

async function start() {
  try {
    workstation = await startWorkstation({
      log: (msg) => console.log('[launcher]', msg),
      onRestart: () => {
        // The supervisor restarted the scanner on the same port — just reload.
        if (mainWindow && !mainWindow.isDestroyed()) mainWindow.reload();
        refreshTray();
      }
    });
  } catch (err) {
    fatal(err);
    return;
  }

  Menu.setApplicationMenu(buildMenu());
  createWindow(!shouldStartHidden());
  createTray();
  startStatusPolling();
}

/* ─────────────────────────────── window ─────────────────────────────── */

function createWindow(showWhenReady = !START_HIDDEN) {
  const win = new BrowserWindow({
    width: 1560,
    height: 980,
    minWidth: 1080,
    minHeight: 700,
    show: false,
    title: APP_TITLE,
    backgroundColor: '#04060B',
    titleBarStyle: process.platform === 'darwin' ? 'hiddenInset' : 'default',
    trafficLightPosition: process.platform === 'darwin' ? { x: 16, y: 13 } : undefined,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
      backgroundThrottling: false
    }
  });

  mainWindow = win;

  win.once('ready-to-show', () => {
    // Launched at login → stay in the menu bar until the user asks for it.
    if (showWhenReady) win.show();
    if (isDev) console.log('[window] ready', workstation && workstation.url);
  });

  win.on('closed', () => { mainWindow = null; });

  win.webContents.on('render-process-gone', (_event, details) => {
    console.error('[window] renderer gone:', details && details.reason);
    if (!quitting) {
      try { win.reload(); } catch (_) {}
    }
  });

  // External links (block explorers, docs) open in the user's browser.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//i.test(url) && !url.startsWith(workstation.url.split('?')[0])) {
      shell.openExternal(url).catch(() => {});
      return { action: 'deny' };
    }
    return { action: 'allow', overrideBrowserWindowOptions: { width: 1200, height: 840 } };
  });

  win.webContents.on('will-navigate', (event, url) => {
    const base = workstation ? workstation.url.split('?')[0] : '';
    if (base && url.startsWith(base)) return;
    event.preventDefault();
    if (/^https?:\/\//i.test(url)) shell.openExternal(url).catch(() => {});
  });

  win.loadURL(workstation.url);
}

function showWindow() {
  if (!mainWindow || mainWindow.isDestroyed()) {
    createWindow(true);
    return;
  }
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
}

/* ──────────────────────────── menu-bar tray ─────────────────────────── */

function trayImage() {
  const img = nativeImage.createEmpty();
  try {
    img.addRepresentation({ scaleFactor: 1, buffer: Buffer.from(icon1x, 'base64') });
    img.addRepresentation({ scaleFactor: 2, buffer: Buffer.from(icon2x, 'base64') });
  } catch (_) {}
  img.setTemplateImage(true);
  return img;
}

function createTray() {
  try {
    tray = new Tray(trayImage());
    tray.setToolTip(`${APP_TITLE} — starting…`);
    tray.on('click', () => showWindow());
    tray.on('double-click', () => showWindow());
    refreshTray();
  } catch (err) {
    console.error('[tray] unavailable:', err.message);
  }
}

function loginItemEnabled() {
  try {
    return Boolean(app.getLoginItemSettings({ args: ['--hidden'] }).openAtLogin);
  } catch (_) {
    return false;
  }
}

function setLoginItem(enabled) {
  try {
    app.setLoginItemSettings({
      openAtLogin: enabled,
      openAsHidden: enabled,
      args: enabled ? ['--hidden'] : []
    });
    return true;
  } catch (err) {
    console.error('[login item]', err.message);
    return false;
  }
}

function refreshTray() {
  if (!tray) return;
  try { if (tray.isDestroyed()) return; } catch (_) { return; }
  const s = lastStatus;
  const scanned = s ? Number(s.scanned || 0).toLocaleString() : '—';
  const rate = s ? Number(s.ratePerSec || 0).toLocaleString() : '0';
  const everHeld = s ? Number(s.everHeldWallets || 0) : 0;
  const funded = s ? Number(s.fundedWallets || 0) : 0;

  const headline = s
    ? (s.scanning ? `Scanning • ${scanned} wallets` : 'Paused')
    : 'Starting…';

  try { tray.setToolTip(`${APP_TITLE}\n${headline} (${rate}/s)\nEver held: ${everHeld} • Funded: ${funded}`); } catch (_) {}

  const menu = Menu.buildFromTemplate([
    { label: headline, enabled: false },
    { label: `${rate}/s • ${everHeld} ever held • ${funded} funded`, enabled: false },
    { type: 'separator' },
    { label: 'Open Workstation', click: () => showWindow() },
    { label: 'Activity Vault', click: () => showTab('Watch') },
    { label: 'Settings…', click: () => openSettings('general') },
    { type: 'separator' },
    {
      label: 'Launch at Login',
      type: 'checkbox',
      checked: loginItemEnabled(),
      click: (item) => {
        const ok = setLoginItem(item.checked);
        if (!ok) item.checked = !item.checked;
        refreshTray();
      }
    },
    {
      label: 'Reveal Data Folder',
      click: () => { if (workstation) shell.openPath(workstation.dataDir).catch(() => {}); }
    },
    {
      label: 'Open Diagnostics Log',
      click: () => { if (workstation && workstation.logFile) shell.openPath(workstation.logFile).catch(() => {}); }
    },
    { type: 'separator' },
    { label: `Quit ${APP_TITLE}`, click: () => app.quit() }
  ]);

  try { tray.setContextMenu(menu); } catch (_) {}
}

function startStatusPolling() {
  stopStatusPolling();
  const tick = async () => {
    if (!workstation || !workstation.getStatus) return;
    try {
      const s = await workstation.getStatus();
      if (s) {
        lastStatus = s;
        refreshTray();
      }
    } catch (_) {}
  };
  tick();
  statusTimer = setInterval(tick, 5000);
  if (statusTimer.unref) statusTimer.unref();
}

function stopStatusPolling() {
  if (statusTimer) {
    clearInterval(statusTimer);
    statusTimer = null;
  }
}

/* ─────────────────────────────── navigation ─────────────────────────── */

function showTab(tab) {
  showWindow();
  if (!mainWindow) return;
  const run = () => mainWindow.webContents.executeJavaScript(
    `window.switchTab && window.switchTab(${JSON.stringify(tab)});`
  ).catch(() => {});
  if (mainWindow.webContents.isLoading()) mainWindow.webContents.once('did-finish-load', run);
  else run();
}

function openSettings(section) {
  showWindow();
  if (!mainWindow) return;
  const run = () => mainWindow.webContents.executeJavaScript(
    `window.openSettings && window.openSettings(${JSON.stringify(section)});`
  ).catch(() => {});
  if (mainWindow.webContents.isLoading()) mainWindow.webContents.once('did-finish-load', run);
  else run();
}

/* ───────────────────────────────── menu ─────────────────────────────── */

function buildMenu() {
  const isMac = process.platform === 'darwin';

  const template = [
    ...(isMac ? [{
      label: APP_TITLE,
      submenu: [
        { role: 'about' },
        { type: 'separator' },
        { label: 'Settings…', accelerator: 'Cmd+,', click: () => openSettings('general') },
        { type: 'separator' },
        { role: 'services' },
        { type: 'separator' },
        { role: 'hide' },
        { role: 'hideOthers' },
        { role: 'unhide' },
        { type: 'separator' },
        { role: 'quit' }
      ]
    }] : []),
    {
      label: 'File',
      submenu: [
        { label: 'Open Activity Vault', accelerator: 'CmdOrCtrl+1', click: () => showTab('Watch') },
        { label: 'Open Smart Hunter', accelerator: 'CmdOrCtrl+2', click: () => showTab('Smart') },
        { type: 'separator' },
        {
          label: 'Download ever_held_wallets.txt',
          click: () => { if (workstation) shell.openExternal(`${workstation.url.split('?')[0]}api/export-ever-held-txt`).catch(() => {}); }
        },
        {
          label: 'Export Scan CSV',
          click: () => { if (mainWindow) mainWindow.webContents.executeJavaScript('window.exportCsvResults && window.exportCsvResults();').catch(() => {}); }
        },
        { type: 'separator' },
        isMac ? { role: 'close' } : { role: 'quit' }
      ]
    },
    {
      label: 'Edit',
      submenu: [
        { role: 'undo' }, { role: 'redo' }, { type: 'separator' },
        { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }
      ]
    },
    {
      label: 'View',
      submenu: [
        { role: 'reload' },
        { role: 'forceReload' },
        { type: 'separator' },
        { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' },
        { type: 'separator' },
        { role: 'toggleDevTools' }
      ]
    },
    {
      label: 'Tools',
      submenu: [
        { label: 'Settings…', accelerator: 'CmdOrCtrl+,', click: () => openSettings('general') },
        { label: 'Alerts & Integrations', click: () => openSettings('alerts') },
        { label: 'Email / SMTP', click: () => openSettings('email') },
        { label: 'API Keys & RPC', click: () => openSettings('api') },
        { label: 'Data & Storage', click: () => openSettings('data') },
        { type: 'separator' },
        {
          label: 'Run Core Self-Test',
          click: () => {
            showTab('BrainEngine');
            if (mainWindow) mainWindow.webContents.executeJavaScript('window.runSelfTestSuite && window.runSelfTestSuite();').catch(() => {});
          }
        },
        {
          label: 'Open Diagnostics Log',
          click: () => { if (workstation && workstation.logFile) shell.openPath(workstation.logFile).catch(() => {}); }
        },
        {
          label: 'Reveal Data Folder',
          click: () => { if (workstation && workstation.dataDir) shell.openPath(workstation.dataDir).catch(() => {}); }
        },
        { type: 'separator' },
        {
          label: 'Launch at Login',
          type: 'checkbox',
          checked: loginItemEnabled(),
          click: (item) => {
            setLoginItem(item.checked);
            refreshTray();
          }
        }
      ]
    },
    { role: 'windowMenu' },
    {
      role: 'help',
      submenu: [
        {
          label: 'Open Web Version',
          click: () => { if (workstation) shell.openExternal(workstation.url.replace('?desktop=1', '')).catch(() => {}); }
        },
        { type: 'separator' },
        {
          label: 'About / Disclaimer',
          click: () => {
            dialog.showMessageBox({
              type: 'info',
              title: `About ${APP_TITLE}`,
              message: `${APP_TITLE} ${app.getVersion()}`,
              detail: [
                'Runs entirely on this machine. Vaults, keys and settings never leave your computer',
                'except through integrations you explicitly enable (Telegram, Discord, SMTP, webhooks).',
                '',
                `Data folder: ${workstation ? workstation.dataDir : '—'}`,
                '',
                'Use only on wallets and infrastructure you are authorised to access.'
              ].join('\n')
            });
          }
        }
      ]
    }
  ];

  return Menu.buildFromTemplate(template);
}

function fatal(err) {
  const message = err && err.message ? err.message : String(err);
  console.error('[fatal]', err);
  const logHint = workstation && workstation.logFile ? `\n\nLog: ${workstation.logFile}` : '';
  try {
    dialog.showErrorBox(`${APP_TITLE} could not start`, `${message}${logHint}`);
  } catch (_) {}
  app.exit(1);
}

module.exports = { readLogTail };
