/* Honesty + core-UI regression suite.
   A lead is only FUNDED when a chain confirmed a balance; every status label
   must come from evidence. Also covers the diagnostics surface and settings. */
const fs = require('fs');
const path = require('path');
const jsdom = require('jsdom');
const { JSDOM } = jsdom;

const ROOT = path.join(__dirname, '..', '..');
const html = fs.readFileSync(path.join(ROOT, 'WebWorkstation', 'index.html'), 'utf8');

const errors = [];
const virtualConsole = new jsdom.VirtualConsole();
virtualConsole.on('jsdomError', (e) => errors.push('jsdomError: ' + e.message));

const BLOOM_FIXTURE = {
  ok: true,
  totalImported: 50000000,
  falsePositiveRate: 0.076,
  byChain: { BTC: 12000000, ETH_EVM: 21000000, TRX: 9000000, XRP: 8000000 },
  sharedAcrossWorkers: 8
};

function boot() {
  return new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    url: 'http://localhost:2525/',
    virtualConsole,
    beforeParse(window) {
      Object.defineProperty(window, 'matchMedia', {
        writable: true,
        value: (q) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} })
      });
      Object.defineProperty(window, 'localStorage', {
        writable: true,
        value: (() => {
          let store = {};
          return {
            getItem: (k) => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); },
            removeItem: (k) => { delete store[k]; },
            clear: () => { store = {}; }
          };
        })()
      });
      window.fetch = () => Promise.resolve({
        ok: true,
        status: 200,
        json: () => Promise.resolve(BLOOM_FIXTURE),
        text: () => Promise.resolve('')
      });
      window.WebSocket = function () { return { addEventListener() {}, removeEventListener() {}, close() {}, send() {}, readyState: 0 }; };
      window.requestAnimationFrame = (cb) => setTimeout(() => cb(Date.now()), 16);
      window.cancelAnimationFrame = (id) => clearTimeout(id);
    }
  });
}

const dom = boot();
const w = dom.window;
const d = w.document;

const checks = [];

// 1–4: status badges — a lead is only FUNDED when a chain confirmed a balance
const pendingBadge = w.formatWalletStatusBadge({ verification: 'pending', bloomOnly: true, address: '0xabc' });
checks.push(['1 pending not FUNDED   ', !/FUNDED/.test(pendingBadge) && /PENDING/.test(pendingBadge)]);
checks.push(['2 pending says checking', /PENDING LIVE CHECK/.test(pendingBadge)]);

const fundedBadge = w.formatWalletStatusBadge({ balanceUsd: 4821.37, verification: 'confirmed', liveVerified: true, address: '0xdef' });
checks.push(['3 confirmed is FUNDED  ', /💰 FUNDED/.test(fundedBadge)]);

const everBadge = w.formatWalletStatusBadge({ everHeldCrypto: true, historicalTx: 41, totalEverReceivedBtc: 0.21, address: 'bc1qz' });
checks.push(['4 ever-held label      ', /📜 EVER HELD/.test(everBadge)]);

// 5: queue badge shows the pending count, visibly
w.updateVerificationBadge(481);
const badge = d.getElementById('VerificationPendingBadge');
checks.push(['5 pending badge        ', badge.textContent.includes('⏳ 481 pending live check') && badge.style.display === 'inline-block']);

// 6: health pill from a healthy snapshot
w.renderHealthPill({ status: 'ok', score: 100 });
const pillText = d.getElementById('HealthPillIcon').textContent + ' ' + d.getElementById('HealthPillText').textContent;
checks.push(['6 health pill          ', pillText === '● Healthy 100']);

// 7: diagnostics all-clear
w.renderDiagnostics({
  status: 'ok', score: 100, version: '2.6.9', uptimeSec: 300, issues: [],
  scan: { daemonActive: true, addressesChecked: 1234567, addressesPerSec: 4021 },
  verify: { pending: 0, ratePerMin: 0, parked: 0 },
  chains: [{ label: 'BTC', attempts: 0, health: 'unknown' }],
  prices: { stale: true, asOf: null },
  telegram: { running: false },
  report: 'demo report'
});
checks.push(['7 diag all-clear       ', d.getElementById('DiagIssues').innerHTML.includes('✅ Every check is passing')]);

// 8: unprobed chains are named honestly
const chainsLine = w.diagChainsLine([
  { label: 'BTC', attempts: 0 }, { label: 'ETH', attempts: 0 },
  { label: 'TRX', attempts: 0 }, { label: 'XRP', attempts: 0 }
]);
checks.push(['8 chains unprobed line ', chainsLine === 'Chains ⚪ not probed yet (4 configured)']);
const probedLine = w.diagChainsLine([
  { label: 'BTC', attempts: 5, health: 'ok' }, { label: 'ETH', attempts: 3, health: 'failing' }
]);
checks.push(['8b chains failing line ', probedLine.includes('failing: ETH')]);

// 9: settings modal opens (integrations + Mac power live there)
w.openSettings('general');
checks.push(['9 settings opens       ', d.getElementById('settingsModal').classList.contains('open')]);
checks.push(['9b keep-awake toggle   ', Boolean(d.getElementById('KeepAwakeChk'))]);

(async () => {
  // 10: full dossier opens for a funded wallet
  await w.openWalletDetail({
    id: 'w-1',
    mnemonic: 'abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about',
    isMnemonicSeed: true,
    btcLegacy: '1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa',
    ethAddress: '0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045',
    balanceUsd: 5210.5,
    verification: 'confirmed',
    liveVerified: true
  });
  const modalOpen = d.getElementById('walletDetailModal').classList.contains('open');
  const dossierBadge = d.getElementById('WalletDetailHeaderBadge').textContent;
  checks.push(['10 dossier opened      ', modalOpen && dossierBadge === 'FUNDED NOW']);

  // 11: bloom false-positive rate is shown, not hidden
  await w.refreshBloomStatus();
  const fpText = d.getElementById('BloomFalsePositiveVal').textContent;
  checks.push(['11 bloom FP shown      ', fpText === '7.6%']);

  // 12: version label follows the server (was hardcoded v2.4)
  w.renderDiagnostics({
    status: 'degraded', score: 76, version: '2.6.9', uptimeSec: 600,
    issues: [{ severity: 'warning', title: 'Market quote is stale', detail: 'x', count: 1 }],
    scan: { daemonActive: true, addressesChecked: 24214560, addressesPerSec: 1877 },
    verify: { pending: 52708, ratePerMin: 1512, parked: 12 },
    chains: [{ label: 'BTC', attempts: 9, health: 'ok' }, { label: 'ETH', attempts: 7, health: 'ok' }],
    prices: { stale: true, asOf: null },
    telegram: { running: true },
    report: ''
  });
  checks.push(['12 version is dynamic  ', d.getElementById('AppVersionLabel').textContent === 'v2.6.9']);

  // 13: RPC label follows server evidence, not the browser loop
  const qB = d.getElementById('QuantumUpgradesBadge');
  if (qB && !qB.textContent.includes('Live Mainnet RPC')) {
    qB.textContent = '8 Workers • 🟢 Live Mainnet RPC (CONNECTING...): 0 Addrs Verified • 3 Ever Held';
    w.renderDiagnostics({
      status: 'ok', score: 100, version: '2.6.9', uptimeSec: 60, issues: [],
      scan: {}, verify: {},
      chains: [{ label: 'BTC', attempts: 5, health: 'ok' }],
      prices: {}, telegram: {}, report: ''
    });
  }
  checks.push(['13 rpc label honest    ', qB.textContent.includes('Live Mainnet RPC (ONLINE)')]);

  // 14: health pill tooltip names the issues
  w.renderHealthPill({ status: 'degraded', score: 76, issues: [{ severity: 'warning', title: 'The scan engine stopped producing addresses' }] });
  const pillTitle = d.getElementById('HealthPill').title;
  checks.push(['14 pill tooltip        ', pillTitle.includes('scan engine stopped')]);

  // 15: a locked (multisig/contract) hit must never look spendable
  const lockedBadge = w.formatWalletStatusBadge({ balanceUsd: 41200, verification: 'confirmed', liveVerified: true, fundsLocked: true, lockReason: 'Safe multisig', address: '0xlock' });
  checks.push(['15 locked not spendable', /LOCKED/.test(lockedBadge) && !/spendable/.test(lockedBadge)]);
  const spendBadge = w.formatWalletStatusBadge({ balanceUsd: 41, verification: 'confirmed', liveVerified: true, address: '0xfree' });
  checks.push(['16 spendable labelled  ', /FUNDED — spendable/.test(spendBadge)]);

  // 17: the dossier banner explains locked funds
  const banner = w.walletLockBanner({ fundsLocked: true, lockReason: 'These funds sit in a smart contract' });
  checks.push(['17 lock banner honest  ', banner.includes('NOT spendable') && banner.includes('smart contract')]);

  // 18: manual check page exposes the transferability verdict
  checks.push(['18 manual check verdict',
    Boolean(d.getElementById('InspectorTransferBanner')) && typeof w.renderTransferVerdict === 'function']);

  // 19: a locked manual result must say NOT transferable and never claim vaulted-found
  const lockHtml = w.renderTransferVerdict(
    { fundedNow: true, allTransferable: false, savedTo: 'none' },
    { balances: { ETH: '1.2' }, fundsLocked: true, lockReason: 'Safe multisig' });
  checks.push(['19 manual locked honest',
    /LOCKED/.test(lockHtml) && /NOT all transferable/.test(lockHtml) &&
    /Safe multisig/.test(lockHtml) && !/Saved to <b>Wallets Found/.test(lockHtml)]);

  // 20: a transferable manual result is recorded and says so
  const freeHtml = w.renderTransferVerdict(
    { fundedNow: true, allTransferable: true, savedTo: 'found' },
    { balances: { BTC: '0.5' } });
  checks.push(['20 manual spendable saves',
    /TRANSFERABLE/.test(freeHtml) && /Saved to <b>Wallets Found/.test(freeHtml)]);

  // 21: typo repair helper suggests one-edit BIP39 fixes (the new recovery engine mode)
  checks.push(['21 typo repair helper',
    typeof w.bip39EditFixes === 'function' &&
    w.bip39EditFixes('aboot', ['about', 'abandon']).includes('about') &&
    w.bip39EditFixes('abandno', ['abandon', 'zoo']).includes('abandon')]);

  // 22: brainflayer-grade KDF families documented with real parameters
  checks.push(['22 kdf families armed',
    /scrypt/.test(w.explainKdfFamily('warpwallet')) &&
    /262144/.test(w.explainKdfFamily('warpwallet')) &&
    /keccak/.test(w.explainKdfFamily('camp2')) &&
    /2031/.test(w.explainKdfFamily('camp2')) &&
    /sha256/.test(w.explainKdfFamily('brainwallet.io'))]);

  // 23: funded-dump coverage report renders chains + totals (audit GAP 3)
  const dumpHtml = w.renderFundedDumpReport({
    totalImported: 15,
    byChain: { BTC: 12, ETH_EVM: 3 },
    deadWeight: 0,
    falsePositiveRate: '0.001%'
  });
  checks.push(['23 dump coverage report',
    /BTC/.test(dumpHtml) && /15/.test(dumpHtml) && /12/.test(dumpHtml) && /false-positive/i.test(dumpHtml)]);

  // 24: live-display feed caps its backlog by dropping oldest (never stalls)
  checks.push(['24 stream queue drops oldest',
    typeof w.pushStreamItems === 'function' &&
    w.pushStreamItems(new Array(300).fill(0).map((_, i) => ({ input: 'w' + i }))) <= 96]);

  // 25: the poller must never contain the old display-freezing stall rule
  checks.push(['25 poll never stalls display',
    !/streamQueue\.length\s*>\s*140/.test(String(w.pollScanBatch)) &&
    /drained < 12/.test(String(w.drainNextStreamItem))]);

  console.log(checks.map(([n, v]) => n + ': ' + v).join('\n'));
  const failed = checks.filter(([, v]) => !v).length;
  console.log(failed === 0 ? 'ALL PASS' : 'FAILED: ' + failed);
  console.log('ERRORS: ' + (errors.length ? errors.join(' | ') : 'none'));
  dom.window.close();
  process.exit(failed === 0 ? 0 : 1);
})();
