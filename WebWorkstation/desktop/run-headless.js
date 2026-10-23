'use strict';

/**
 * Headless desktop runner — starts the workstation and opens the system browser.
 * Used by the lightweight `.app` / `.command` launcher when the full Electron
 * shell is not installed.
 *
 * Usage: node run-headless.js [--port 2525] [--no-open]
 */

const { spawn } = require('child_process');
const { startWorkstation } = require('./launcher');

function openBrowser(url) {
  const platform = process.platform;
  const cmd = platform === 'darwin' ? 'open' : platform === 'win32' ? 'cmd' : 'xdg-open';
  const args = platform === 'win32' ? ['/c', 'start', '', url] : [url];
  try {
    spawn(cmd, args, { detached: true, stdio: 'ignore' }).unref();
  } catch (_) {
    console.log(`Open ${url} in your browser.`);
  }
}

(async () => {
  const argv = process.argv.slice(2);
  const portArg = argv.indexOf('--port');
  const requestedPort = portArg >= 0 ? Number(argv[portArg + 1]) : 2525;
  const ports = [requestedPort, 3100, 3101, 3102, 3103];

  try {
    const ws = await startWorkstation({
      ports,
      log: (m) => console.log(m)
    });

    const url = ws.url.replace('?desktop=1', '');
    console.log(`\nCodeHaven Workstation ready: ${url}`);
    console.log(`Data directory: ${ws.dataDir}`);
    if (ws.logFile) console.log(`Log file: ${ws.logFile}`);

    if (!argv.includes('--no-open')) openBrowser(url);

    if (!ws.spawned) {
      console.log('Attached to an already-running scanner — leaving it untouched.');
      process.exit(0);
    }

    const shutdown = async (signal) => {
      console.log(`\n[shutdown] ${signal}`);
      await ws.stop();
      process.exit(0);
    };
    process.on('SIGINT', () => shutdown('SIGINT'));
    process.on('SIGTERM', () => shutdown('SIGTERM'));

    // Keep this supervisor alive: the launcher owns the child's lifecycle and
    // will restart it automatically if it crashes. Exiting here would kill the
    // supervisor and defeat the auto-restart.
    setInterval(() => {}, 60 * 60 * 1000);
  } catch (err) {
    console.error('\nFailed to start CodeHaven Workstation:\n');
    console.error(err.message);
    process.exit(1);
  }
})();
