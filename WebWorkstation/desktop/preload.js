'use strict';

/**
 * Minimal, locked-down preload bridge.
 * The renderer keeps using plain fetch() against the local server; this only
 * exposes a flag so the web UI can switch into native-window layout.
 */

const { contextBridge } = require('electron');

try {
  contextBridge.exposeInMainWorld('codehavenDesktop', {
    isDesktop: true,
    platform: process.platform,
    versions: {
      electron: process.versions.electron,
      chrome: process.versions.chrome,
      node: process.versions.node
    }
  });
} catch (_) {
  // Preload can run before the bridge is ready; the UI falls back to ?desktop=1.
}
