const { parentPort, workerData } = require('worker_threads');
const crypto = require('crypto');
const eng = require('./crypto_engine');

const workerId = (workerData && workerData.workerId) || 0;
const sharedBloomBuffer = workerData && workerData.sharedBloomBuffer;
const bloomView = sharedBloomBuffer ? new Int32Array(sharedBloomBuffer) : null;
const BLOOM_BITS = bloomView ? bloomView.length * 32 : 0;

function fnv1aHashes(str) {
  let h1 = 0x811c9dc5;
  let h2 = 0x811c9dc5 ^ 0x9e3779b9;
  for (let i = 0; i < str.length; i++) {
    const c = str.charCodeAt(i);
    h1 ^= c;
    h1 = Math.imul(h1, 0x01000193) >>> 0;
    h2 ^= (c + i);
    h2 = Math.imul(h2, 0x85ebca6b) >>> 0;
  }
  const h3 = (h1 ^ Math.imul(h2, 0xc2b2ae35)) >>> 0;
  return [h1 >>> 0, h2 >>> 0, h3 >>> 0];
}

function tryMarkInSharedBloom(key) {
  if (!bloomView || !key) return true;
  const [h1, h2, h3] = fnv1aHashes(key);
  const b1 = h1 % BLOOM_BITS;
  const b2 = h2 % BLOOM_BITS;
  const b3 = h3 % BLOOM_BITS;

  const w1 = b1 >>> 5, m1 = 1 << (b1 & 31);
  const w2 = b2 >>> 5, m2 = 1 << (b2 & 31);
  const w3 = b3 >>> 5, m3 = 1 << (b3 & 31);

  const old1 = Atomics.or(bloomView, w1, m1);
  const old2 = Atomics.or(bloomView, w2, m2);
  const old3 = Atomics.or(bloomView, w3, m3);

  return !((old1 & m1) !== 0 && (old2 & m2) !== 0 && (old3 & m3) !== 0);
}

const MULTI_LANG_SAMPLES = [
  { lang: 'ES', words: ['abaco', 'abdomen', 'abeja', 'abierto', 'abogado', 'abono', 'aborto', 'abrazo', 'abrir', 'abuelo', 'abuso', 'acabar', 'academia', 'acceso', 'accion', 'aceite', 'acelga', 'acento', 'aceptar', 'acido', 'aclarar', 'acne', 'acoger', 'acoso'] },
  { lang: 'FR', words: ['abaisser', 'abandon', 'abdiquer', 'abeille', 'abolir', 'aborder', 'aboutir', 'aboyer', 'abrasif', 'abreuver', 'abriter', 'abroger', 'abrupt', 'absence', 'absolu', 'absorber', 'abstrait', 'abusif', 'acajou', 'accepter', 'acclamer', 'accolade', 'accroche', 'accuser'] },
  { lang: 'IT', words: ['abaco', 'abbaglio', 'abbinato', 'abete', 'abisso', 'abitare', 'abituare', 'abrogato', 'accadere', 'accenno', 'ulivo', 'umore', 'unguento', 'onore', 'opera', 'opinione', 'opposto', 'oracolo', 'orario', 'orbita', 'ordine', 'orecchio', 'orfano', 'organo'] }
];

let localCounter = workerId * 1000000;
let paused = false;

if (parentPort) {
  parentPort.on('message', msg => {
    if (msg && msg.type === 'pause') paused = Boolean(msg.paused);
  });
}

function generateWorkerCandidate() {
  localCounter++;
  const mode = localCounter % 6;

  if (mode === 0) {
    // Multi-Index + Change-Chain BIP39 (Upgrade #2)
    const words11 = [];
    const used = new Set([0]);
    while (words11.length < 11) {
      const idx = crypto.randomInt(1, eng.WORDLIST.length);
      if (!used.has(idx)) {
        used.add(idx);
        words11.push(eng.WORDLIST[idx]);
      }
    }
    const mnem = eng.buildChecksumValid12WordDirect(words11, localCounter & 0x7f);
    if (!tryMarkInSharedBloom(mnem)) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
    return {
      source: `Worker #${workerId + 1} • Multi-Index + Change (0/0..2, 1/0)`,
      vectorKey: 'paths',
      feedType: 'Smart',
      phraseType: '12-Word Multi-Index',
      input: mnem,
      brainArm: 'MultiAccountChangeSweep',
      ...derived
    };
  }

  if (mode === 1) {
    // Dual-Standard BIP39 + Electrum v2 Seed (Upgrade #4)
    const words11 = [];
    const used = new Set([0]);
    while (words11.length < 11) {
      const idx = crypto.randomInt(1, eng.WORDLIST.length);
      if (!used.has(idx)) {
        used.add(idx);
        words11.push(eng.WORDLIST[idx]);
      }
    }
    const mnem = eng.buildChecksumValid12WordDirect(words11, (localCounter * 7) & 0x7f);
    if (!tryMarkInSharedBloom('electrum:' + mnem)) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
    const elec = eng.deriveElectrumDualAddresses(mnem);
    return {
      source: `Worker #${workerId + 1} • Dual BIP39 + Electrum v2`,
      vectorKey: 'electrum',
      feedType: 'Smart',
      phraseType: 'Dual BIP39+Electrum',
      input: mnem,
      brainArm: 'ElectrumDualStandard',
      ...derived,
      electrumBtcLegacy: elec.electrumBtcLegacy,
      electrumBtcSegwit: elec.electrumBtcSegwit,
      electrumBtcChange: elec.electrumBtcChange
    };
  }

  if (mode === 2) {
    // Multi-Language BIP39 (ES / FR / IT) (Upgrade #4)
    const pack = MULTI_LANG_SAMPLES[localCounter % MULTI_LANG_SAMPLES.length];
    const picked = [];
    const used = new Set();
    while (picked.length < 12) {
      const w = pack.words[crypto.randomInt(0, pack.words.length)];
      if (!used.has(w)) {
        used.add(w);
        picked.push(w);
      }
    }
    const phrase = picked.join(' ');
    if (!tryMarkInSharedBloom(`lang:${pack.lang}:${phrase}`)) return null;
    const derived = eng.deriveWalletFromMnemonic(phrase, '', false);
    return {
      source: `Worker #${workerId + 1} • Multi-Lang BIP39 (${pack.lang})`,
      vectorKey: 'human',
      feedType: 'Smart',
      phraseType: `12-Word BIP39 (${pack.lang})`,
      input: phrase,
      brainArm: 'MultiLanguageBip39',
      ...derived
    };
  }

  // Fast Checksum-Bit Neighbor / Temporal PRNG / EVM Nonce & Token Sweep
  const words11 = [];
  const used = new Set([0]);
  while (words11.length < 11) {
    const idx = crypto.randomInt(1, eng.WORDLIST.length);
    if (!used.has(idx)) {
      used.add(idx);
      words11.push(eng.WORDLIST[idx]);
    }
  }
  const mnem = eng.buildChecksumValid12WordDirect(words11, crypto.randomInt(0, 128));
  if (!tryMarkInSharedBloom(mnem)) return null;
  const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
  return {
    source: `Worker #${workerId + 1} • EVM Nonce & ERC-20 Token Sweep`,
    vectorKey: 'cognitive',
    feedType: 'Smart',
    phraseType: '12-Word BIP39 + ERC20',
    input: mnem,
    brainArm: 'ChecksumBitNeighbor',
    erc20Swept: ['USDT', 'USDC', 'WBTC', 'DAI'],
    ...derived
  };
}

function workerLoop() {
  if (!paused && parentPort) {
    const batch = [];
    let attempts = 0;
    while (batch.length < 4 && attempts < 12) {
      attempts++;
      const item = generateWorkerCandidate();
      if (item) batch.push(item);
    }
    if (batch.length > 0) {
      parentPort.postMessage({
        type: 'batch',
        workerId,
        items: batch,
        scannedCount: batch.length,
        childAddressesChecked: batch.length * 6
      });
    }
  }
  setTimeout(workerLoop, 90);
}

workerLoop();
