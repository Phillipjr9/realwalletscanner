const { parentPort, workerData } = require('worker_threads');
const crypto = require('crypto');
const eng = require('./crypto_engine');

const workerIndex = (workerData && workerData.workerIndex) || 0;
const totalWorkers = (workerData && workerData.totalWorkers) || 1;
const job = (workerData && workerData.job) || {};

const ALL_TARGET_FIELDS = [
  'btcLegacy', 'btcNestedSegwit', 'btcSegwit', 'btcTaproot',
  'btcSegwitIndex1', 'btcSegwitIndex2', 'btcChangeSegwit', 'btcChangeLegacy',
  'ethAddress', 'ethAddressIndex1',
  'trxAddress', 'xrpAddress', 'solAddress', 'ltcAddress', 'ltcSegwitAddress', 'dogeAddress'
];

function buildTargetSet(targets) {
  const s = new Set();
  for (const raw of (targets || [])) {
    if (!raw || typeof raw !== 'string') continue;
    const t = raw.trim();
    if (!t) continue;
    s.add(t);
    s.add(t.toLowerCase());
  }
  return s;
}

function matchesTargetSet(wallet, targetSet) {
  if (!wallet) return null;
  for (const field of ALL_TARGET_FIELDS) {
    const val = wallet[field];
    if (!val) continue;
    if (targetSet.has(val) || targetSet.has(val.toLowerCase())) {
      return { matchedField: field, matchedAddress: val };
    }
  }
  if (Array.isArray(wallet.childAddresses)) {
    for (const c of wallet.childAddresses) {
      if (c && c.address && (targetSet.has(c.address) || targetSet.has(c.address.toLowerCase()))) {
        return { matchedField: c.path, matchedAddress: c.address };
      }
    }
  }
  return null;
}

/**
 * Fast SHA-256 BIP39 12-word checksum validator using pre-packed indices array (indices[0..11] in 0..2047).
 * Avoids string concatenation AND filters out 15/16 (93.75%) of candidates BEFORE expensive PBKDF2-SHA512!
 */
const entropyBuf16 = Buffer.allocUnsafe(16);
function isValid12WordIndices(idxArr) {
  let bits = 0n;
  for (let i = 0; i < 12; i++) {
    bits = (bits << 11n) | BigInt(idxArr[i]);
  }
  const checksum = Number(bits & 0x0fn);
  const ent = bits >> 4n;
  let tmp = ent;
  for (let i = 15; i >= 0; i--) {
    entropyBuf16[i] = Number(tmp & 0xffn);
    tmp >>= 8n;
  }
  const shaFirstByte = crypto.createHash('sha256').update(entropyBuf16).digest()[0];
  return (shaFirstByte >>> 4) === checksum;
}

function isValid24WordIndices(idxArr) {
  let bits = 0n;
  for (let i = 0; i < 24; i++) {
    bits = (bits << 11n) | BigInt(idxArr[i]);
  }
  const checksum = Number(bits & 0xffn);
  const ent = bits >> 8n;
  const buf32 = Buffer.allocUnsafe(32);
  let tmp = ent;
  for (let i = 31; i >= 0; i--) {
    buf32[i] = Number(tmp & 0xffn);
    tmp >>= 8n;
  }
  return crypto.createHash('sha256').update(buf32).digest()[0] === checksum;
}

function runMissingWordsSolver() {
  // Tokens like: "abandon ? abandon ? abandon abandon abandon abandon abandon abandon abandon about"
  const rawTokens = String(job.pattern || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (rawTokens.length !== 12 && rawTokens.length !== 24) {
    parentPort.postMessage({ type: 'error', error: `Expected 12 or 24 tokens (use ? or * for missing positions), got ${rawTokens.length}` });
    return;
  }
  const targetSet = buildTargetSet(job.targets);
  const passphrase = String(job.passphrase || '');
  const missingPositions = [];
  const idxArr = new Int32Array(rawTokens.length);

  for (let i = 0; i < rawTokens.length; i++) {
    const tok = rawTokens[i];
    if (tok === '?' || tok === '*' || tok === '_') {
      missingPositions.push(i);
      idxArr[i] = 0;
    } else {
      const wIdx = eng.WORD_TO_INDEX.get(tok);
      if (wIdx === undefined) {
        parentPort.postMessage({ type: 'error', error: `Word #${i + 1} ("${tok}") is not a valid BIP39 word` });
        return;
      }
      idxArr[i] = wIdx;
    }
  }

  if (missingPositions.length === 0 || missingPositions.length > 3) {
    parentPort.postMessage({ type: 'error', error: `Missing word solver supports 1, 2, or 3 unknown (?) positions (found ${missingPositions.length})` });
    return;
  }

  const is12 = rawTokens.length === 12;
  const checkFn = is12 ? isValid12WordIndices : isValid24WordIndices;
  let checkedCombos = 0;
  let validChecksums = 0;
  const foundCandidates = [];
  let lastReport = Date.now();

  const reportProgress = (force = false) => {
    const now = Date.now();
    if (force || now - lastReport >= 250) {
      lastReport = now;
      parentPort.postMessage({
        type: 'progress',
        workerIndex,
        checkedCombos,
        validChecksums
      });
    }
  };

  // Partition outer missing word across [workerIndex, workerIndex + totalWorkers, ...]
  if (missingPositions.length === 1) {
    const p0 = missingPositions[0];
    for (let w0 = workerIndex; w0 < 2048; w0 += totalWorkers) {
      idxArr[p0] = w0;
      checkedCombos++;
      if (checkFn(idxArr)) {
        validChecksums++;
        const words = Array.from(idxArr, k => eng.WORDLIST[k]);
        const mnem = words.join(' ');
        const wallet = eng.deriveWalletFromMnemonic(mnem, passphrase, true);
        if (targetSet.size > 0) {
          const hit = matchesTargetSet(wallet, targetSet);
          if (hit) {
            parentPort.postMessage({ type: 'hit', workerIndex, mnemonic: mnem, passphrase, wallet, ...hit });
            return;
          }
        } else {
          foundCandidates.push({ mnemonic: mnem, wallet });
        }
      }
    }
  } else if (missingPositions.length === 2) {
    const [p0, p1] = missingPositions;
    for (let w0 = workerIndex; w0 < 2048; w0 += totalWorkers) {
      idxArr[p0] = w0;
      for (let w1 = 0; w1 < 2048; w1++) {
        idxArr[p1] = w1;
        checkedCombos++;
        if (checkFn(idxArr)) {
          validChecksums++;
          const mnem = Array.from(idxArr, k => eng.WORDLIST[k]).join(' ');
          const wallet = eng.deriveWalletFromMnemonic(mnem, passphrase, true);
          if (targetSet.size > 0) {
            const hit = matchesTargetSet(wallet, targetSet);
            if (hit) {
              parentPort.postMessage({ type: 'hit', workerIndex, mnemonic: mnem, passphrase, wallet, ...hit });
              return;
            }
          } else if (foundCandidates.length < 25) {
            foundCandidates.push({ mnemonic: mnem, wallet });
          }
        }
      }
      reportProgress(false);
    }
  } else {
    // 3 missing words: support optional restricted hint lists per slot or full 2048^3 sweep
    const [p0, p1, p2] = missingPositions;
    const domain = Array.isArray(job.wordPoolIndices) && job.wordPoolIndices.length > 0
      ? job.wordPoolIndices
      : Array.from({ length: 2048 }, (_, k) => k);
    for (let i0 = workerIndex; i0 < domain.length; i0 += totalWorkers) {
      idxArr[p0] = domain[i0];
      for (let i1 = 0; i1 < domain.length; i1++) {
        idxArr[p1] = domain[i1];
        for (let i2 = 0; i2 < domain.length; i2++) {
          idxArr[p2] = domain[i2];
          checkedCombos++;
          if (checkFn(idxArr)) {
            validChecksums++;
            const mnem = Array.from(idxArr, k => eng.WORDLIST[k]).join(' ');
            const wallet = eng.deriveWalletFromMnemonic(mnem, passphrase, true);
            if (targetSet.size > 0) {
              const hit = matchesTargetSet(wallet, targetSet);
              if (hit) {
                parentPort.postMessage({ type: 'hit', workerIndex, mnemonic: mnem, passphrase, wallet, ...hit });
                return;
              }
            }
          }
          if ((checkedCombos & 8191) === 0) reportProgress(false);
        }
      }
      reportProgress(false);
    }
  }

  reportProgress(true);
  parentPort.postMessage({
    type: 'done',
    workerIndex,
    checkedCombos,
    validChecksums,
    candidates: foundCandidates
  });
}

function editDistance1Candidates(word) {
  const LETTERS = 'abcdefghijklmnopqrstuvwxyz';
  const w = String(word || '').toLowerCase();
  const out = new Set();
  const has = (x) => eng.WORD_TO_INDEX.has(x);
  // deletions
  for (let i = 0; i < w.length; i++) {
    const t = w.slice(0, i) + w.slice(i + 1);
    if (has(t)) out.add(t);
  }
  // adjacent transpositions
  for (let i = 0; i + 1 < w.length; i++) {
    const a = w.split('');
    [a[i], a[i + 1]] = [a[i + 1], a[i]];
    const t = a.join('');
    if (has(t)) out.add(t);
  }
  // substitutions
  for (let i = 0; i < w.length; i++) {
    for (const c of LETTERS) {
      const t = w.slice(0, i) + c + w.slice(i + 1);
      if (has(t)) out.add(t);
    }
  }
  // insertions
  for (let i = 0; i <= w.length; i++) {
    for (const c of LETTERS) {
      const t = w.slice(0, i) + c + w.slice(i);
      if (has(t)) out.add(t);
    }
  }
  out.delete(w);
  return Array.from(out);
}

function runTypoFixSolver() {
  const rawTokens = String(job.pattern || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (rawTokens.length !== 12 && rawTokens.length !== 24) {
    parentPort.postMessage({ type: 'error', error: `Expected 12 or 24 tokens (mistyped words allowed), got ${rawTokens.length}` });
    return;
  }
  const targetSet = buildTargetSet(job.targets);
  const passphrase = String(job.passphrase || '');
  const idxArr = new Int32Array(rawTokens.length);
  const suspect = []; // { pos, candidates[] }
  let comboCount = 1;

  for (let i = 0; i < rawTokens.length; i++) {
    const tok = rawTokens[i];
    const wIdx = eng.WORD_TO_INDEX.get(tok);
    if (wIdx !== undefined) {
      idxArr[i] = wIdx;
    } else {
      const candidates = editDistance1Candidates(tok);
      if (candidates.length === 0) {
        parentPort.postMessage({ type: 'error', error: `Word #${i + 1} ("${tok}") is not a BIP39 word and has no one-edit fix` });
        return;
      }
      suspect.push({ pos: i, candidates });
      comboCount *= candidates.length;
    }
  }

  if (suspect.length === 0) {
    parentPort.postMessage({ type: 'error', error: 'All words are valid BIP39 words - nothing to fix. Use the missing-word solver for ? positions.' });
    return;
  }
  if (suspect.length > 2 || comboCount > 250000) {
    parentPort.postMessage({ type: 'error', error: `Typo solver supports up to 2 mistyped words (and <= 250k fix combos); this input needs ${comboCount} combos across ${suspect.length} words` });
    return;
  }

  const is12 = rawTokens.length === 12;
  const checkFn = is12 ? isValid12WordIndices : isValid24WordIndices;
  let checkedCombos = 0;
  let validChecksums = 0;
  const foundCandidates = [];
  let lastReport = Date.now();

  const reportProgress = (force = false) => {
    const now = Date.now();
    if (force || now - lastReport >= 250) {
      lastReport = now;
      parentPort.postMessage({ type: 'progress', workerIndex, checkedCombos, validChecksums });
    }
  };

  // Cartesian product of fix candidates, partitioned across workers on the flat index
  const product = [];
  const sizes = suspect.map(x => x.candidates.length);
  const walk = (depth, acc) => {
    if (product.length >= 300000) return;
    if (depth === suspect.length) { product.push(acc.slice()); return; }
    for (let c = 0; c < sizes[depth]; c++) walk(depth + 1, [...acc, c]);
  };
  walk(0, []);

  for (let ci = workerIndex; ci < product.length; ci += totalWorkers) {
    const combo = product[ci];
    for (let k = 0; k < suspect.length; k++) {
      const sp = suspect[k];
      idxArr[sp.pos] = eng.WORD_TO_INDEX.get(sp.candidates[combo[k]]);
    }
    checkedCombos++;
    if (checkFn(idxArr)) {
      validChecksums++;
      const mnem = Array.from(idxArr, k => eng.WORDLIST[k]).join(' ');
      const wallet = eng.deriveWalletFromMnemonic(mnem, passphrase, true);
      if (targetSet.size > 0) {
        const hit = matchesTargetSet(wallet, targetSet);
        if (hit) {
          parentPort.postMessage({ type: 'hit', workerIndex, mnemonic: mnem, passphrase, wallet, ...hit });
          reportProgress(true);
          return;
        }
      } else if (foundCandidates.length < 25) {
        foundCandidates.push({ mnemonic: mnem, wallet });
      }
    }
    if ((ci % 64) === 0) reportProgress();
  }

  reportProgress(true);
  parentPort.postMessage({
    type: 'done', workerIndex, checkedCombos, validChecksums,
    candidates: foundCandidates,
    fixes: suspect.map((sp, i) => ({
      position: sp.pos + 1,
      typed: rawTokens[sp.pos],
      tried: sp.candidates.length
    }))
  });
}

function runPassphrase25thSolver() {
  const mnem = String(job.mnemonic || '').trim().toLowerCase().replace(/\s+/g, ' ');
  const candidates = Array.isArray(job.passwords) ? job.passwords : [];
  const targetSet = buildTargetSet(job.targets);
  let checkedCombos = 0;
  let lastReport = Date.now();
  const sampleDerived = [];

  for (let i = workerIndex; i < candidates.length; i += totalWorkers) {
    const pass = candidates[i];
    checkedCombos++;
    const wallet = eng.deriveWalletFromMnemonic(mnem, pass, true);
    if (targetSet.size > 0) {
      const hit = matchesTargetSet(wallet, targetSet);
      if (hit) {
        parentPort.postMessage({
          type: 'hit',
          workerIndex,
          mnemonic: mnem,
          passphrase: pass,
          wallet,
          ...hit
        });
        return;
      }
    } else if (sampleDerived.length < 15) {
      sampleDerived.push({ passphrase: pass, wallet });
    }
    if (Date.now() - lastReport >= 250) {
      lastReport = Date.now();
      parentPort.postMessage({ type: 'progress', workerIndex, checkedCombos, validChecksums: checkedCombos });
    }
  }

  parentPort.postMessage({
    type: 'done',
    workerIndex,
    checkedCombos,
    validChecksums: checkedCombos,
    candidates: sampleDerived
  });
}

function runScrambledWordsSolver() {
  const words = String(job.mnemonic || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length !== 12 && words.length !== 24) {
    parentPort.postMessage({ type: 'error', error: `Scrambled solver expects 12 or 24 BIP39 words, got ${words.length}` });
    return;
  }
  const baseIndices = [];
  for (const w of words) {
    const idx = eng.WORD_TO_INDEX.get(w);
    if (idx === undefined) {
      parentPort.postMessage({ type: 'error', error: `"${w}" is not a valid BIP39 word` });
      return;
    }
    baseIndices.push(idx);
  }

  const targetSet = buildTargetSet(job.targets);
  const passphrase = String(job.passphrase || '');
  const is12 = words.length === 12;
  const checkFn = is12 ? isValid12WordIndices : isValid24WordIndices;
  let checkedCombos = 0;
  let validChecksums = 0;
  const foundCandidates = [];
  let lastReport = Date.now();

  // Build deterministic list of common human paper-backup scramble transforms FIRST (instant milliseconds):
  //   - identity, reverse, 2x6 / 6x2 / 3x4 / 4x3 column-vs-row transpositions, cyclic shifts, all 1-swaps & 2-swaps
  const transforms = [];
  const n = baseIndices.length;
  transforms.push(baseIndices.slice());
  transforms.push(baseIndices.slice().reverse());

  // Paper grid transpositions (reading down columns instead of across rows)
  const grids = n === 12 ? [[2, 6], [6, 2], [3, 4], [4, 3]] : [[2, 12], [12, 2], [3, 8], [8, 3], [4, 6], [6, 4]];
  for (const [rows, cols] of grids) {
    const t = [];
    for (let c = 0; c < cols; c++) {
      for (let r = 0; r < rows; r++) t.push(baseIndices[r * cols + c]);
    }
    transforms.push(t);
    transforms.push(t.slice().reverse());
  }
  // Cyclic rotations
  for (let shift = 1; shift < n; shift++) {
    transforms.push(baseIndices.slice(shift).concat(baseIndices.slice(0, shift)));
  }
  // All single-pair transpositions (C(12,2) = 66) & double-pair transpositions (2,145)
  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) {
      const a = baseIndices.slice();
      [a[i], a[j]] = [a[j], a[i]];
      transforms.push(a);
      for (let p = i + 1; p < n; p++) {
        for (let q = p + 1; q < n; q++) {
          if (p === j || q === j) continue;
          const b = a.slice();
          [b[p], b[q]] = [b[q], b[p]];
          transforms.push(b);
        }
      }
    }
  }

  for (let tIdx = workerIndex; tIdx < transforms.length; tIdx += totalWorkers) {
    const arr = transforms[tIdx];
    checkedCombos++;
    if (checkFn(arr)) {
      validChecksums++;
      const mnem = arr.map(k => eng.WORDLIST[k]).join(' ');
      const wallet = eng.deriveWalletFromMnemonic(mnem, passphrase, true);
      if (targetSet.size > 0) {
        const hit = matchesTargetSet(wallet, targetSet);
        if (hit) {
          parentPort.postMessage({ type: 'hit', workerIndex, mnemonic: mnem, passphrase, wallet, ...hit });
          return;
        }
      } else if (foundCandidates.length < 30) {
        foundCandidates.push({ mnemonic: mnem, wallet });
      }
    }
  }

  // Next, if still not found and n === 12 and deepPermute is enabled, permute last 8 positions while keeping first 4 fixed (8! = 40,320)
  // OR full Heap's permutation of last K positions (default K = 8 => 40,320 * 1/16 = ~2,520 PBKDF2s across all workers!)
  if (is12 && job.deepPermute !== false) {
    const kPerm = Math.min(9, Math.max(6, Number(job.permuteTailCount || 8)));
    const prefix = baseIndices.slice(0, 12 - kPerm);
    const tail = baseIndices.slice(12 - kPerm);
    // Partition by choice of tail[0] across workers!
    for (let firstChoice = workerIndex; firstChoice < tail.length; firstChoice += totalWorkers) {
      const remaining = tail.filter((_, idx) => idx !== firstChoice);
      const work = new Int32Array(12);
      for (let x = 0; x < prefix.length; x++) work[x] = prefix[x];
      work[prefix.length] = tail[firstChoice];

      // Iterative Heap's algorithm over `remaining`
      const m = remaining.length;
      const c = new Int32Array(m);
      const perm = remaining.slice();

      const testPerm = () => {
        for (let x = 0; x < m; x++) work[prefix.length + 1 + x] = perm[x];
        checkedCombos++;
        if (checkFn(work)) {
          validChecksums++;
          const mnem = Array.from(work, idx => eng.WORDLIST[idx]).join(' ');
          const wallet = eng.deriveWalletFromMnemonic(mnem, passphrase, true);
          if (targetSet.size > 0) {
            const hit = matchesTargetSet(wallet, targetSet);
            if (hit) {
              parentPort.postMessage({ type: 'hit', workerIndex, mnemonic: mnem, passphrase, wallet, ...hit });
              return true;
            }
          } else if (foundCandidates.length < 30) {
            foundCandidates.push({ mnemonic: mnem, wallet });
          }
        }
        if (Date.now() - lastReport >= 250) {
          lastReport = Date.now();
          parentPort.postMessage({ type: 'progress', workerIndex, checkedCombos, validChecksums });
        }
        return false;
      };

      if (testPerm()) return;
      let i = 0;
      while (i < m) {
        if (c[i] < i) {
          if (i % 2 === 0) {
            [perm[0], perm[i]] = [perm[i], perm[0]];
          } else {
            [perm[c[i]], perm[i]] = [perm[i], perm[c[i]]];
          }
          if (testPerm()) return;
          c[i]++;
          i = 0;
        } else {
          c[i] = 0;
          i++;
        }
      }
    }
  }

  parentPort.postMessage({
    type: 'done',
    workerIndex,
    checkedCombos,
    validChecksums,
    candidates: foundCandidates
  });
}

function runKeystoreVaultSolver() {
  const vaultObj = typeof job.vaultJson === 'string' ? JSON.parse(job.vaultJson) : job.vaultJson;
  const passwords = Array.isArray(job.passwords) ? job.passwords : [];
  let checkedCombos = 0;
  let lastReport = Date.now();

  for (let i = workerIndex; i < passwords.length; i += totalWorkers) {
    const pwd = passwords[i];
    checkedCombos++;
    try {
      const res = eng.decryptVaultOrKeystore(vaultObj, pwd);
      if (res && res.valid) {
        parentPort.postMessage({
          type: 'hit',
          workerIndex,
          password: pwd,
          vaultType: res.vaultType,
          mnemonic: res.mnemonic || null,
          privateKeyHex: res.privateKeyHex || (res.wallet && res.wallet.privateKeyHex) || null,
          wallet: res.wallet || null,
          matchedField: 'Cryptographic MAC / GCM AuthTag Verified',
          matchedAddress: (res.wallet && (res.wallet.ethAddress || res.wallet.btcSegwit)) || 'Vault Decrypted'
        });
        return;
      }
    } catch (err) {
      parentPort.postMessage({ type: 'error', error: err.message });
      return;
    }

    if (Date.now() - lastReport >= 250) {
      lastReport = Date.now();
      parentPort.postMessage({ type: 'progress', workerIndex, checkedCombos, validChecksums: 0 });
    }
  }

  parentPort.postMessage({
    type: 'done',
    workerIndex,
    checkedCombos,
    validChecksums: 0,
    candidates: []
  });
}

try {
  if (job.mode === 'fix_typos') {
    runTypoFixSolver();
  } else if (job.mode === 'missing_words') {
    runMissingWordsSolver();
  } else if (job.mode === 'passphrase_25th') {
    runPassphrase25thSolver();
  } else if (job.mode === 'scrambled_words') {
    runScrambledWordsSolver();
  } else if (job.mode === 'keystore_vault') {
    runKeystoreVaultSolver();
  } else {
    parentPort.postMessage({ type: 'error', error: `Unknown forensic mode: ${job.mode}` });
  }
} catch (err) {
  parentPort.postMessage({ type: 'error', error: err.message || String(err) });
}
