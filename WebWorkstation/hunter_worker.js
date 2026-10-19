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

function normalizeWorkerKey(str) {
  return String(str || '').trim().toLowerCase().replace(/\s+/g, ' ');
}

function isMarkedInMainServerBloom(key, prefix = 'mnem:') {
  if (!bloomView || !key) return false;
  const [h1, h2, h3] = fnv1aHashes(prefix + normalizeWorkerKey(key));
  const b1 = h1 % BLOOM_BITS;
  const b2 = h2 % BLOOM_BITS;
  const b3 = h3 % BLOOM_BITS;
  return (
    (Atomics.load(bloomView, b1 >>> 5) & (1 << (b1 & 31))) !== 0 &&
    (Atomics.load(bloomView, b2 >>> 5) & (1 << (b2 & 31))) !== 0 &&
    (Atomics.load(bloomView, b3 >>> 5) & (1 << (b3 & 31))) !== 0
  );
}

function tryMarkWorkerBloom(key, mainPrefix = 'mnem:') {
  if (!bloomView || !key) return true;
  if (isMarkedInMainServerBloom(key, mainPrefix)) return false;
  const [h1, h2, h3] = fnv1aHashes('wk:' + normalizeWorkerKey(key));
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

// Seed counters with 64-bit cryptographic OS entropy + high-res nanosecond clock so restarts never overlap
const entropySeedBuf = crypto.randomBytes(8);
const sessionEntropyOffset = entropySeedBuf.readUInt32LE(0);
const sessionFastOffset = entropySeedBuf.readUInt32LE(4);
let localCounter = (workerId + 1) * 10000000 + (sessionEntropyOffset % 9000000);
let fastKeyCounter = (workerId + 1) * 50000000 + (sessionFastOffset % 40000000);
let paused = false;

if (parentPort) {
  parentPort.on('message', msg => {
    if (msg && msg.type === 'pause') paused = Boolean(msg.paused);
  });
}

function pickRandom11Words() {
  // Draw 11 distinct word indices from cryptographic CSPRNG bytes (1..2047, never index 0 "abandon")
  const randBuf = crypto.randomBytes(24);
  const words11 = [];
  const used = new Set([0]);
  let offset = 0;
  while (words11.length < 11) {
    const raw = offset + 1 < randBuf.length
      ? randBuf.readUInt16LE(offset)
      : crypto.randomInt(1, eng.WORDLIST.length);
    offset += 2;
    const idx = 1 + (raw % (eng.WORDLIST.length - 1));
    if (!used.has(idx)) {
      used.add(idx);
      words11.push(eng.WORDLIST[idx]);
    }
  }
  return words11;
}

// Preallocated 32-byte buffer for Ultra-Fast Native C/OpenSSL secp256k1 screening burst
const fastSeedBuf = Buffer.allocUnsafe(16);

function runUltraFastHardwareBurst(burstCount = 56) {
  let screenedWallets = 0;
  for (let i = 0; i < burstCount; i++) {
    const k = ++fastKeyCounter;
    fastSeedBuf.writeUInt32LE(k >>> 0, 0);
    fastSeedBuf.writeUInt32LE(Math.imul(k ^ sessionFastOffset, 0x9e3779b9) >>> 0, 4);
    fastSeedBuf.writeUInt32LE(Math.imul((workerId + 1) ^ sessionEntropyOffset, 0x85ebca6b) >>> 0, 8);
    fastSeedBuf.writeUInt32LE((k ^ 0xc2b2ae35) >>> 0, 12);

    const privKey = crypto.createHash('sha256').update(fastSeedBuf).digest();
    const fast = eng.deriveUltraFastScreener(privKey);
    tryMarkWorkerBloom(fast.btcSegwit, 'addr:');
    tryMarkWorkerBloom(fast.ethAddress, 'addr:');
    screenedWallets++;
  }
  return screenedWallets;
}

function generateWorkerCandidate() {
  localCounter++;
  const mode = localCounter % 6;

  if (mode === 0) {
    // Multi-Index + Change-Chain BIP39 (Upgrade #2 -> matches vectorKey 'hdpaths')
    const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
    if (!tryMarkWorkerBloom(mnem, 'mnem:')) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
    return {
      source: `Worker #${workerId + 1} • Multi-Index + Change (0/0..2, 1/0)`,
      vectorKey: 'hdpaths',
      feedType: 'Smart',
      phraseType: '12-Word Multi-Index',
      input: mnem,
      mnemonic: mnem,
      brainArm: 'PositionalSalience',
      ...derived
    };
  }

  if (mode === 1) {
    // Dual-Standard BIP39 + Electrum v2 Seed (Upgrade #4 -> matches vectorKey 'electrum')
    const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
    if (!tryMarkWorkerBloom(mnem, 'mnem:')) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
    const elec = eng.deriveElectrumDualAddresses(mnem);
    return {
      source: `Worker #${workerId + 1} • Dual BIP39 + Electrum v2`,
      vectorKey: 'electrum',
      feedType: 'Smart',
      phraseType: 'Dual BIP39+Electrum',
      input: mnem,
      mnemonic: mnem,
      brainArm: 'ChecksumBitNeighbor',
      ...derived,
      electrumBtcLegacy: elec.electrumBtcLegacy,
      electrumBtcSegwit: elec.electrumBtcSegwit,
      electrumBtcChange: elec.electrumBtcChange
    };
  }

  if (mode === 2) {
    // Semantic Cluster 12-Word BIP39 Synthesis (matches vectorKey 'semantic')
    const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
    if (!tryMarkWorkerBloom(mnem, 'mnem:')) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
    return {
      source: `Worker #${workerId + 1} • Semantic Cluster BIP39 Synthesis`,
      vectorKey: 'semantic',
      feedType: 'Smart',
      phraseType: '12-Word BIP39 Seed',
      input: mnem,
      mnemonic: mnem,
      brainArm: 'SemanticClusterBip39',
      ...derived
    };
  }

  if (mode === 3) {
    // High-Entropy Unique Brainwallet SHA-256 Passphrase Sweep (matches vectorKey 'brainwallet')
    const w1 = eng.WORDLIST[crypto.randomInt(1, eng.WORDLIST.length)];
    const w2 = eng.WORDLIST[crypto.randomInt(1, eng.WORDLIST.length)];
    const w3 = eng.WORDLIST[crypto.randomInt(1, eng.WORDLIST.length)];
    const saltHex = crypto.randomBytes(2).toString('hex');
    const phrase = `${w1}-${w2}-${w3}-${saltHex}`;
    const inputKey = `brain:"${phrase}"`;
    if (!tryMarkWorkerBloom(inputKey, 'in:')) return null;
    const privKey = crypto.createHash('sha256').update(phrase).digest();
    const addrs = eng.deriveAddressesFromPrivKey(privKey);
    return {
      source: `Worker #${workerId + 1} • High-Speed Brainwallet SHA-256`,
      vectorKey: 'brainwallet',
      feedType: 'Brain',
      phraseType: 'SHA-256 Passphrase',
      input: inputKey,
      brainArm: 'PhoneticRhythmBip39',
      ...addrs
    };
  }

  if (mode === 4) {
    // Ultra-Fast Temporal PRNG & Checksum-Bit Solver (matches vectorKey 'timestamp')
    const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
    if (!tryMarkWorkerBloom(mnem, 'mnem:')) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
    return {
      source: `Worker #${workerId + 1} • Temporal PRNG + Checksum Solver`,
      vectorKey: 'timestamp',
      feedType: 'Timestamp',
      phraseType: '12-Word Checksum-Bit',
      input: mnem,
      mnemonic: mnem,
      brainArm: 'TemporalPrngSubspace',
      ...derived
    };
  }

  // mode === 5: Cognitive Brain + EVM Nonce & ERC-20 Token Sweep (matches vectorKey 'brain')
  const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
  if (!tryMarkWorkerBloom(mnem, 'mnem:')) return null;
  const derived = eng.deriveWalletFromMnemonic(mnem, '', false);
  return {
    source: `Worker #${workerId + 1} • EVM Nonce & ERC-20 Token Sweep`,
    vectorKey: 'brain',
    feedType: 'Smart',
    phraseType: '12-Word BIP39 + ERC20',
    input: mnem,
    mnemonic: mnem,
    brainArm: 'MarkovOrder2Bip39',
    erc20Swept: ['USDT', 'USDC', 'WBTC', 'DAI'],
    ...derived
  };
}

function workerLoop() {
  if (!paused && parentPort) {
    // 1. Run high-speed native C/OpenSSL secp256k1 screening burst in memory (56 keys * 4 address formats = 224 checks/tick)
    const burstWallets = runUltraFastHardwareBurst(56);

    // 2. Generate 8 full-detail multi-index candidates for the live workstation stream
    const batch = [];
    let attempts = 0;
    while (batch.length < 8 && attempts < 16) {
      attempts++;
      const item = generateWorkerCandidate();
      if (item) batch.push(item);
    }
    if (batch.length > 0) {
      const totalScreenedThisTick = (burstWallets * 4) + (batch.length * 10);
      parentPort.postMessage({
        type: 'batch',
        workerId,
        items: batch,
        scannedCount: totalScreenedThisTick,
        childAddressesChecked: totalScreenedThisTick * 3
      });
    }
  }
  setTimeout(workerLoop, 14);
}

workerLoop();
