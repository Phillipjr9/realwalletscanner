const crypto = require('crypto');
const bip39 = require('bip39');

const WORDLIST = bip39.wordlists.english;
const WORD_TO_INDEX = new Map(WORDLIST.map((w, idx) => [w, idx]));
const SECP256K1_N = BigInt('0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141');

// Precomputed BIP-0340 / BIP-0341 "TapTweak" tagged-hash prefix: SHA256("TapTweak") || SHA256("TapTweak")
const TAPTWEAK_TAG_SINGLE = crypto.createHash('sha256').update('TapTweak').digest();
const TAPTWEAK_TAG_PREFIX = Buffer.concat([TAPTWEAK_TAG_SINGLE, TAPTWEAK_TAG_SINGLE]); // 64 bytes
const tapTweakInputBuf = Buffer.allocUnsafe(96);
TAPTWEAK_TAG_PREFIX.copy(tapTweakInputBuf, 0);

// RFC 8410 ASN.1 PKCS#8 DER prefix for a 32-byte Ed25519 private seed (Solana SLIP-0010)
const ED25519_PKCS8_PREFIX = Buffer.from('302e020100300506032b657004220420', 'hex');

// Shared OpenSSL secp256k1 context (avoids repeated context allocations per wallet)
const sharedEcdh = crypto.createECDH('secp256k1');

// Fast Base58 alphabet
const BASE58_ALPHABET = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
function base58Encode(buf) {
  let zeros = 0;
  while (zeros < buf.length && buf[zeros] === 0) zeros++;
  let num = BigInt('0x' + (buf.toString('hex') || '0'));
  let out = '';
  while (num > 0n) {
    const rem = Number(num % 58n);
    num = num / 58n;
    out = BASE58_ALPHABET[rem] + out;
  }
  return '1'.repeat(zeros) + out;
}

function base58CheckEncode(versionByte, payload) {
  const data = Buffer.allocUnsafe(1 + payload.length + 4);
  data[0] = versionByte;
  payload.copy(data, 1);
  const sub = data.subarray(0, 1 + payload.length);
  const h1 = crypto.createHash('sha256').update(sub).digest();
  const h2 = crypto.createHash('sha256').update(h1).digest();
  h2.copy(data, 1 + payload.length, 0, 4);
  return base58Encode(data);
}

// Generic Bech32 / Bech32m encoder (supports 'bc' & 'ltc' witness v0 & v1)
const BECH32_CHARSET = 'qpzry9x8gf2tvdw0s3jn54khce6mua7l';
const BECH32_GEN = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

function bech32EncodeWitness(hrp, witnessVer, progBuf, isBech32m = false) {
  let chk = 1;
  for (let i = 0; i < hrp.length; i++) {
    const b = chk >>> 25;
    chk = ((chk & 0x1ffffff) << 5) ^ (hrp.charCodeAt(i) >>> 5);
    for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
  }
  {
    const b = chk >>> 25;
    chk = (chk & 0x1ffffff) << 5;
    for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
  }
  for (let i = 0; i < hrp.length; i++) {
    const b = chk >>> 25;
    chk = ((chk & 0x1ffffff) << 5) ^ (hrp.charCodeAt(i) & 31);
    for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
  }
  {
    const b = chk >>> 25;
    chk = ((chk & 0x1ffffff) << 5) ^ witnessVer;
    for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
  }

  let out = hrp + '1' + BECH32_CHARSET[witnessVer];
  let acc = 0, bits = 0;
  for (let i = 0; i < progBuf.length; i++) {
    acc = (acc << 8) | progBuf[i];
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      const v = (acc >>> bits) & 31;
      out += BECH32_CHARSET[v];
      const b = chk >>> 25;
      chk = ((chk & 0x1ffffff) << 5) ^ v;
      for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
    }
  }
  if (bits > 0) {
    const v = (acc << (5 - bits)) & 31;
    out += BECH32_CHARSET[v];
    const b = chk >>> 25;
    chk = ((chk & 0x1ffffff) << 5) ^ v;
    for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
  }

  for (let i = 0; i < 6; i++) {
    const b = chk >>> 25;
    chk = (chk & 0x1ffffff) << 5;
    for (let j = 0; j < 5; j++) if ((b >>> j) & 1) chk ^= BECH32_GEN[j];
  }
  chk ^= isBech32m ? 0x2bc830a3 : 1;
  for (let i = 0; i < 6; i++) {
    out += BECH32_CHARSET[(chk >>> (5 * (5 - i))) & 31];
  }
  return out;
}

function bech32EncodeBc0(h160) {
  return bech32EncodeWitness('bc', 0, h160, false);
}

function bech32mEncodeBc1p(xOnly32) {
  return bech32EncodeWitness('bc', 1, xOnly32, true);
}

// Fast 32-bit Lane Keccak-256 (zero BigInt allocations inside the 24-round permutation)
const RC_HI = new Uint32Array([
  0x00000000, 0x00000000, 0x80000000, 0x80000000, 0x00000000, 0x00000000, 0x80000000, 0x80000000,
  0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x80000000, 0x80000000, 0x80000000,
  0x80000000, 0x80000000, 0x00000000, 0x80000000, 0x80000000, 0x80000000, 0x00000000, 0x80000000
]);
const RC_LO = new Uint32Array([
  0x00000001, 0x00008082, 0x0000808a, 0x80008000, 0x0000808b, 0x80000001, 0x80008081, 0x00008009,
  0x0000008a, 0x00000088, 0x80008009, 0x8000000a, 0x8000808b, 0x0000008b, 0x00008089, 0x00008003,
  0x00008002, 0x00000080, 0x0000800a, 0x8000000a, 0x80008081, 0x00008080, 0x80000001, 0x80008008
]);
const ROTC = [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];
const PILN = [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

const sHi = new Uint32Array(25);
const sLo = new Uint32Array(25);
const bcHi = new Uint32Array(5);
const bcLo = new Uint32Array(5);
const keccakPadBuf = Buffer.alloc(136);

function permuteKeccakf1600() {
  for (let round = 0; round < 24; round++) {
    for (let i = 0; i < 5; i++) {
      bcLo[i] = sLo[i] ^ sLo[i + 5] ^ sLo[i + 10] ^ sLo[i + 15] ^ sLo[i + 20];
      bcHi[i] = sHi[i] ^ sHi[i + 5] ^ sHi[i + 10] ^ sHi[i + 15] ^ sHi[i + 20];
    }
    for (let i = 0; i < 5; i++) {
      const i4 = (i + 4) % 5, i1 = (i + 1) % 5;
      const rLo = (bcLo[i1] << 1) | (bcHi[i1] >>> 31);
      const rHi = (bcHi[i1] << 1) | (bcLo[i1] >>> 31);
      const tLo = bcLo[i4] ^ rLo;
      const tHi = bcHi[i4] ^ rHi;
      for (let j = 0; j < 25; j += 5) {
        sLo[j + i] ^= tLo;
        sHi[j + i] ^= tHi;
      }
    }
    let tLo = sLo[1], tHi = sHi[1];
    for (let i = 0; i < 24; i++) {
      const j = PILN[i];
      const n = ROTC[i];
      const tmpLo = sLo[j], tmpHi = sHi[j];
      if (n < 32) {
        sLo[j] = (tLo << n) | (tHi >>> (32 - n));
        sHi[j] = (tHi << n) | (tLo >>> (32 - n));
      } else {
        const s = n - 32;
        sLo[j] = (tHi << s) | (tLo >>> (32 - s));
        sHi[j] = (tLo << s) | (tHi >>> (32 - s));
      }
      tLo = tmpLo;
      tHi = tmpHi;
    }
    for (let j = 0; j < 25; j += 5) {
      for (let i = 0; i < 5; i++) {
        bcLo[i] = sLo[j + i];
        bcHi[i] = sHi[j + i];
      }
      for (let i = 0; i < 5; i++) {
        const i1 = (i + 1) % 5, i2 = (i + 2) % 5;
        sLo[j + i] ^= (~bcLo[i1]) & bcLo[i2];
        sHi[j + i] ^= (~bcHi[i1]) & bcHi[i2];
      }
    }
    sLo[0] ^= RC_LO[round];
    sHi[0] ^= RC_HI[round];
  }
}

function keccak256ShortBlock(inputBuf, byteLen) {
  sHi.fill(0);
  sLo.fill(0);
  keccakPadBuf.fill(0);
  inputBuf.copy(keccakPadBuf, 0, 0, byteLen);
  keccakPadBuf[byteLen] = 0x01;
  keccakPadBuf[135] |= 0x80;

  for (let i = 0; i < 17; i++) {
    sLo[i] ^= keccakPadBuf.readUInt32LE(i * 8);
    sHi[i] ^= keccakPadBuf.readUInt32LE(i * 8 + 4);
  }

  permuteKeccakf1600();

  const out = Buffer.allocUnsafe(32);
  for (let i = 0; i < 4; i++) {
    out.writeUInt32LE(sLo[i] >>> 0, i * 8);
    out.writeUInt32LE(sHi[i] >>> 0, i * 8 + 4);
  }
  return out;
}

/**
 * Full multi-block Keccak-256 sponge (rate = 136 bytes, domain byte = 0x01)
 * Used for ABI function selectors and Ethereum Keystore V3 MAC verification.
 */
function keccak256(input) {
  const buf = Buffer.isBuffer(input) ? input : Buffer.from(input);
  if (buf.length < 136) {
    return keccak256ShortBlock(buf, buf.length);
  }
  sHi.fill(0);
  sLo.fill(0);
  let offset = 0;
  while (buf.length - offset >= 136) {
    for (let i = 0; i < 17; i++) {
      sLo[i] ^= buf.readUInt32LE(offset + i * 8);
      sHi[i] ^= buf.readUInt32LE(offset + i * 8 + 4);
    }
    permuteKeccakf1600();
    offset += 136;
  }
  const rem = buf.length - offset;
  keccakPadBuf.fill(0);
  if (rem > 0) buf.copy(keccakPadBuf, 0, offset, buf.length);
  keccakPadBuf[rem] = 0x01;
  keccakPadBuf[135] |= 0x80;
  for (let i = 0; i < 17; i++) {
    sLo[i] ^= keccakPadBuf.readUInt32LE(i * 8);
    sHi[i] ^= keccakPadBuf.readUInt32LE(i * 8 + 4);
  }
  permuteKeccakf1600();

  const out = Buffer.allocUnsafe(32);
  for (let i = 0; i < 4; i++) {
    out.writeUInt32LE(sLo[i] >>> 0, i * 8);
    out.writeUInt32LE(sHi[i] >>> 0, i * 8 + 4);
  }
  return out;
}

function keccak256PubKey64(pub64) {
  return keccak256ShortBlock(pub64, 64);
}

const asciiHex40Buf = Buffer.allocUnsafe(40);
function toEip55ChecksumAddress(lowerHex40) {
  asciiHex40Buf.write(lowerHex40, 0, 40, 'ascii');
  const hash = keccak256ShortBlock(asciiHex40Buf, 40);
  let out = '0x';
  for (let i = 0; i < 40; i++) {
    const ch = lowerHex40[i];
    if (ch >= 'a' && ch <= 'f') {
      const nibble = (i & 1) === 0 ? (hash[i >>> 1] >>> 4) : (hash[i >>> 1] & 0x0f);
      out += nibble >= 8 ? ch.toUpperCase() : ch;
    } else {
      out += ch;
    }
  }
  return out;
}

// Official Ripple (XRPL) Base58 Alphabet
const XRP_ALPHABET = 'rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz';
const BASE58_MAP = new Int16Array(128).fill(-1);
for (let i = 0; i < 58; i++) {
  BASE58_MAP['123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz'.charCodeAt(i)] = i;
}

function base58Decode(str) {
  if (!str || typeof str !== 'string') return Buffer.alloc(0);
  let zeros = 0;
  while (zeros < str.length && str[zeros] === '1') zeros++;
  const size = Math.ceil((str.length - zeros) * 733 / 1000) + 1;
  const b256 = new Uint8Array(size);
  let length = 0;
  for (let i = zeros; i < str.length; i++) {
    const code = str.charCodeAt(i);
    const val = code < 128 ? BASE58_MAP[code] : -1;
    if (val < 0) return Buffer.alloc(0);
    let carry = val;
    let j = 0;
    for (let k = size - 1; (carry !== 0 || j < length) && k >= 0; k--, j++) {
      carry += 58 * b256[k];
      b256[k] = carry % 256;
      carry = (carry / 256) | 0;
    }
    length = j;
  }
  let it = size - length;
  while (it < size && b256[it] === 0) it++;
  const out = Buffer.allocUnsafe(zeros + (size - it));
  out.fill(0, 0, zeros);
  let pos = zeros;
  while (it < size) out[pos++] = b256[it++];
  return out;
}

/**
 * Converts a TRON Base58Check address ('T...') to its 20-byte EVM hex form ('0x...')
 * for ultra-fast JSON-RPC / Multicall batching on TronGrid (/jsonrpc).
 */
function tronAddressToHex20(tAddr) {
  const raw = base58Decode(tAddr);
  if (raw.length === 25 && raw[0] === 0x41) {
    return '0x' + raw.subarray(1, 21).toString('hex');
  }
  return null;
}

/**
 * Official TRON Address Derivation from 32-byte secp256k1 private key:
 * Keccak-256(uncompressedPubKey64)[12..32] prefixed with 0x41 -> Base58Check ('T...')
 */
function privKeyToTronAddress(privKeyBuf) {
  sharedEcdh.setPrivateKey(privKeyBuf);
  const pubUncomp = sharedEcdh.getPublicKey(null, 'uncompressed');
  const addr20 = keccak256PubKey64(pubUncomp.subarray(1)).subarray(12, 32);
  return {
    trxAddress: base58CheckEncode(0x41, addr20),
    trxHexAddress: '0x' + addr20.toString('hex')
  };
}

/**
 * Official XRP Ledger Classic Address ('r...') from 32-byte secp256k1 private key:
 * Payload = 0x00 || RIPEMD160(SHA256(compressedPubKey33)), Checksum = SHA256(SHA256(Payload))[0..4],
 * encoded with the Ripple Base58 dictionary.
 */
function privKeyToXrpAddress(privKeyBuf) {
  sharedEcdh.setPrivateKey(privKeyBuf);
  const acctId20 = hash160(sharedEcdh.getPublicKey(null, 'compressed'));
  const payload = Buffer.allocUnsafe(25);
  payload[0] = 0x00;
  acctId20.copy(payload, 1);
  const c1 = crypto.createHash('sha256').update(payload.subarray(0, 21)).digest();
  const c2 = crypto.createHash('sha256').update(c1).digest();
  c2.copy(payload, 21, 0, 4);

  let zeros = 0;
  while (zeros < payload.length && payload[zeros] === 0) zeros++;
  const digits = [0];
  for (let i = zeros; i < payload.length; i++) {
    let carry = payload[i];
    for (let j = 0; j < digits.length; j++) {
      carry += digits[j] << 8;
      digits[j] = carry % 58;
      carry = (carry / 58) | 0;
    }
    while (carry > 0) {
      digits.push(carry % 58);
      carry = (carry / 58) | 0;
    }
  }
  let out = XRP_ALPHABET[0].repeat(zeros);
  for (let i = digits.length - 1; i >= 0; i--) {
    out += XRP_ALPHABET[digits[i]];
  }
  return out;
}

function hash160(buf) {
  const sha = crypto.createHash('sha256').update(buf).digest();
  return crypto.createHash('ripemd160').update(sha).digest();
}

function bigIntToBuf32(num) {
  const out = Buffer.allocUnsafe(32);
  let tmp = num;
  for (let i = 31; i >= 0; i--) {
    out[i] = Number(tmp & 0xffn);
    tmp >>= 8n;
  }
  return out;
}

function privKeyToCompressedWif(privKeyBuf) {
  return base58CheckEncode(0x80, Buffer.concat([privKeyBuf, Buffer.from([0x01])]));
}

/**
 * BIP-0341 / BIP-0086 Standard Taproot Key-Path Output (Q = lift_x(P) + int(TaggedHash("TapTweak", P_x)) * G):
 * Since P_even = d_even * G, Q = ((d_even + tweak) mod n) * G in 1 native OpenSSL point multiplication.
 */
function deriveTaprootAddressFromPrivKey(privKeyBuf) {
  sharedEcdh.setPrivateKey(privKeyBuf);
  const pubComp = sharedEcdh.getPublicKey(null, 'compressed');
  const dInt = BigInt('0x' + privKeyBuf.toString('hex'));
  const dEven = pubComp[0] === 0x02 ? dInt : (SECP256K1_N - dInt);

  pubComp.copy(tapTweakInputBuf, 64, 1, 33);
  const tweakHash = crypto.createHash('sha256').update(tapTweakInputBuf).digest();
  const tweakInt = BigInt('0x' + tweakHash.toString('hex'));
  const tweakedScalar = (dEven + tweakInt) % SECP256K1_N;
  const tweakedPrivBuf = bigIntToBuf32(tweakedScalar);

  sharedEcdh.setPrivateKey(tweakedPrivBuf);
  const tweakedPubComp = sharedEcdh.getPublicKey(null, 'compressed');
  return bech32mEncodeBc1p(tweakedPubComp.subarray(1, 33));
}

/**
 * SLIP-0010 Ed25519 Solana Key & Address Derivation (m/44'/501'/0'/0' — Phantom / Solflare / Ledger standard)
 */
const slip10Buf = Buffer.allocUnsafe(37);
function deriveSolanaFromSeed(seedBuf, account = 0) {
  let I = crypto.createHmac('sha512', 'ed25519 seed').update(seedBuf).digest();
  let key = I.subarray(0, 32);
  let chainCode = I.subarray(32, 64);

  const path = [0x8000002c, 0x800001f5, (0x80000000 | (account >>> 0)) >>> 0, 0x80000000];
  for (let i = 0; i < path.length; i++) {
    slip10Buf[0] = 0x00;
    key.copy(slip10Buf, 1);
    slip10Buf.writeUInt32BE(path[i] >>> 0, 33);
    I = crypto.createHmac('sha512', chainCode).update(slip10Buf).digest();
    key = I.subarray(0, 32);
    chainCode = I.subarray(32, 64);
  }
  return deriveSolanaFromEd25519Seed32(key);
}

function deriveSolanaFromEd25519Seed32(edSeed32) {
  const pkcs8Der = Buffer.concat([ED25519_PKCS8_PREFIX, edSeed32]);
  const privObj = crypto.createPrivateKey({ key: pkcs8Der, format: 'der', type: 'pkcs8' });
  const spkiDer = crypto.createPublicKey(privObj).export({ format: 'der', type: 'spki' });
  const pubKey32 = spkiDer.subarray(spkiDer.length - 32);
  const solAddress = base58Encode(pubKey32);
  const solPrivateKeyBase58 = base58Encode(Buffer.concat([edSeed32, pubKey32]));
  return { solAddress, solPrivateKeyBase58 };
}

function deriveAddressesFromPrivKey(privKeyBuf, fullAltcoins = true) {
  sharedEcdh.setPrivateKey(privKeyBuf);
  const pubCompressed = sharedEcdh.getPublicKey(null, 'compressed');
  const pubUncompressed = sharedEcdh.getPublicKey(null, 'uncompressed');

  const h160 = hash160(pubCompressed);
  const btcLegacy = base58CheckEncode(0x00, h160);
  // 2009-2015 bitaddress.org / brainwallet.org / Bitcoin-Qt Uncompressed P2PKH ('1...' matching WIF '5...')
  const btcLegacyUncompressed = base58CheckEncode(0x00, hash160(pubUncompressed));
  const redeemScript = Buffer.concat([Buffer.from([0x00, 0x14]), h160]);
  const btcNestedSegwit = base58CheckEncode(0x05, hash160(redeemScript));
  const btcSegwit = bech32EncodeBc0(h160);

  const k = keccak256PubKey64(pubUncompressed.subarray(1));
  const ethAddress = toEip55ChecksumAddress(k.subarray(12).toString('hex'));

  // Compute BIP-0341 TapTweak address after pubCompressed/pubUncompressed have been read
  const btcTaproot = deriveTaprootAddressFromPrivKey(privKeyBuf);

  const privHex = privKeyBuf.toString('hex');
  const btcWif = privKeyToCompressedWif(privKeyBuf);
  const btcWifUncompressed = base58CheckEncode(0x80, privKeyBuf);
  const electrumImportBlock = `p2pkh:${btcWif}\np2pkh:${btcWifUncompressed}\np2wpkh-p2sh:${btcWif}\np2wpkh:${btcWif}`;

  if (!fullAltcoins) {
    return {
      privateKeyHex: privHex,
      ethPrivateKeyHex: '0x' + privHex,
      btcPrivateKeyHex: privHex,
      privateKey: privHex,
      btcWif,
      btcLegacyWif: btcWif,
      btcNestedWif: btcWif,
      btcSegwitWif: btcWif,
      btcTaprootWif: btcWif,
      btcWifUncompressed,
      electrumImportBlock,
      btcLegacy,
      btcLegacyUncompressed,
      btcNestedSegwit,
      btcP2sh: btcNestedSegwit,
      btcSegwit,
      btcTaproot,
      ethAddress
    };
  }

  const ltcAddress = base58CheckEncode(0x30, h160);
  const ltcSegwitAddress = bech32EncodeWitness('ltc', 0, h160, false);
  const dogeAddress = base58CheckEncode(0x1E, h160);
  const sol = deriveSolanaFromEd25519Seed32(privKeyBuf);
  const trx = privKeyToTronAddress(privKeyBuf);
  const xrpAddress = privKeyToXrpAddress(privKeyBuf);

  return {
    privateKeyHex: privHex,
    ethPrivateKeyHex: '0x' + privHex,
    btcPrivateKeyHex: privHex,
    trxPrivateKeyHex: privHex,
    xrpPrivateKeyHex: privHex,
    privateKey: privHex,
    btcWif,
    btcLegacyWif: btcWif,
    btcNestedWif: btcWif,
    btcSegwitWif: btcWif,
    btcTaprootWif: btcWif,
    solPrivateKeyBase58: sol.solPrivateKeyBase58,
    btcWifUncompressed,
    electrumImportBlock,
    btcLegacy,
    btcLegacyUncompressed,
    btcNestedSegwit,
    btcP2sh: btcNestedSegwit,
    btcSegwit,
    btcTaproot,
    ethAddress,
    trxAddress: trx.trxAddress,
    trxHexAddress: trx.trxHexAddress,
    xrpAddress,
    solAddress: sol.solAddress,
    ltcAddress,
    ltcSegwitAddress,
    dogeAddress
  };
}

// BIP32 child derivation with preallocated 37-byte buffer
const bip32DataBuf = Buffer.allocUnsafe(37);
function bip32MasterFromSeed(seedBuf) {
  const I = crypto.createHmac('sha512', 'Bitcoin seed').update(seedBuf).digest();
  return { key: I.subarray(0, 32), chainCode: I.subarray(32) };
}

function bip32DeriveChild(parent, index) {
  if (index >= 0x80000000) {
    bip32DataBuf[0] = 0x00;
    parent.key.copy(bip32DataBuf, 1);
  } else {
    sharedEcdh.setPrivateKey(parent.key);
    sharedEcdh.getPublicKey(null, 'compressed').copy(bip32DataBuf, 0);
  }
  bip32DataBuf.writeUInt32BE(index >>> 0, 33);
  const I = crypto.createHmac('sha512', parent.chainCode).update(bip32DataBuf).digest();
  const il = BigInt('0x' + I.subarray(0, 32).toString('hex'));
  const kpar = BigInt('0x' + parent.key.toString('hex'));
  const childKeyInt = (il + kpar) % SECP256K1_N;
  const childKeyBuf = bigIntToBuf32(childKeyInt);
  return { key: childKeyBuf, chainCode: I.subarray(32) };
}

function derivePathNode(master, indices) {
  let node = master;
  for (const idx of indices) node = bip32DeriveChild(node, idx);
  return node;
}

function derivePath(master, indices) {
  return derivePathNode(master, indices).key;
}

const H = 0x80000000;

/**
 * Direct O(1) SHA-256 Checksum-Bit 12th-Word Solver:
 * Computes the exact 4-bit SHA-256 checksum for 11 words + 7-bit prefix in 1 single SHA-256 call.
 */
function buildChecksumValid12WordDirect(elevenWords, top7Bits = 0) {
  const indices = new Uint16Array(12);
  for (let i = 0; i < 11; i++) {
    const idx = WORD_TO_INDEX.get(elevenWords[i]);
    indices[i] = idx !== undefined ? idx : 1;
  }
  let top7 = top7Bits & 0x7F;
  const entropy = Buffer.alloc(16);

  for (let attempt = 0; attempt < 16; attempt++) {
    entropy.fill(0);
    let bitPos = 0;
    for (let w = 0; w < 11; w++) {
      const val = indices[w];
      for (let b = 10; b >= 0; b--) {
        if ((val >>> b) & 1) {
          entropy[bitPos >>> 3] |= (1 << (7 - (bitPos & 7)));
        }
        bitPos++;
      }
    }
    for (let b = 6; b >= 0; b--) {
      if ((top7 >>> b) & 1) {
        entropy[bitPos >>> 3] |= (1 << (7 - (bitPos & 7)));
      }
      bitPos++;
    }
    const sha = crypto.createHash('sha256').update(entropy).digest();
    const checksum4 = (sha[0] >> 4) & 0x0F;
    const word12Idx = (top7 << 4) | checksum4;
    if (word12Idx !== 0 && !indices.subarray(0, 11).includes(word12Idx)) {
      return elevenWords.concat([WORDLIST[word12Idx]]).join(' ');
    }
    top7 = (top7 + 1) & 0x7F;
  }
  return bip39.generateMnemonic(128);
}

/**
 * Generates a real valid Electrum v2 12-word seed whose HMAC-SHA512("Seed version", phrase)
 * genuinely begins with '100' (Native SegWit) or '01' (Standard Legacy).
 */
function generateValidElectrumSeed(preferSegwit = true) {
  for (let round = 0; round < 32; round++) {
    const words11 = [];
    const used = new Set([0]);
    while (words11.length < 11) {
      const idx = crypto.randomInt(1, WORDLIST.length);
      if (!used.has(idx)) {
        used.add(idx);
        words11.push(WORDLIST[idx]);
      }
    }
    const prefix = words11.join(' ') + ' ';
    const startIdx = crypto.randomInt(1, WORDLIST.length);
    for (let offset = 0; offset < WORDLIST.length - 1; offset++) {
      const w12 = WORDLIST[1 + ((startIdx + offset) % (WORDLIST.length - 1))];
      const cand = prefix + w12;
      const digest = crypto.createHmac('sha512', 'Seed version').update(cand).digest();
      if (preferSegwit) {
        if (digest[0] === 0x10 && (digest[1] & 0xf0) === 0x00) {
          return { mnemonic: cand, electrumVersion: '100 (Native SegWit)' };
        }
      } else if (digest[0] === 0x01) {
        return { mnemonic: cand, electrumVersion: '01 (Standard Legacy)' };
      }
    }
  }
  return generateValidElectrumSeed(false);
}

function getElectrumSeedVersion(mnemonic) {
  if (!mnemonic) return null;
  const norm = mnemonic.trim().toLowerCase().replace(/\s+/g, ' ').normalize('NFKD');
  const verHex = crypto.createHmac('sha512', 'Seed version').update(norm).digest('hex');
  if (verHex.startsWith('100')) return '100';
  if (verHex.startsWith('101')) return '101';
  if (verHex.startsWith('102')) return '102';
  if (verHex.startsWith('01')) return '01';
  return null;
}

function pubKeyToBtcSegwit(privKey) {
  sharedEcdh.setPrivateKey(privKey);
  const pubComp = sharedEcdh.getPublicKey(null, 'compressed');
  return bech32EncodeBc0(hash160(pubComp));
}

function pubKeyToEthAddress(privKey) {
  sharedEcdh.setPrivateKey(privKey);
  const pubUncomp = sharedEcdh.getPublicKey(null, 'uncompressed');
  return toEip55ChecksumAddress(keccak256PubKey64(pubUncomp.subarray(1)).subarray(12).toString('hex'));
}

/**
 * True Electrum v2 Dual Derivation:
 * - Electrum Standard Legacy (version 01): unhardened m/0/0 (receiving) & m/1/0 (change)
 * - Electrum Native SegWit (version 100): hardened m/0'/0/0 (receiving) & m/0'/1/0 (change)
 */
function deriveElectrumDualAddresses(mnemonic, passphrase = '') {
  const electrumSeed = crypto.pbkdf2Sync(
    mnemonic.normalize('NFKD'),
    'electrum' + passphrase.normalize('NFKD'),
    2048,
    64,
    'sha512'
  );
  const master = bip32MasterFromSeed(electrumSeed);

  // 1. Electrum Standard Legacy (m/0/0 & m/1/0)
  const stdExt = bip32DeriveChild(master, 0);
  const stdChg = bip32DeriveChild(master, 1);
  const stdKey0 = bip32DeriveChild(stdExt, 0).key;
  const stdChgKey0 = bip32DeriveChild(stdChg, 0).key;

  sharedEcdh.setPrivateKey(stdKey0);
  const stdH160 = hash160(sharedEcdh.getPublicKey(null, 'compressed'));
  const electrumBtcLegacy = base58CheckEncode(0x00, stdH160);

  sharedEcdh.setPrivateKey(stdChgKey0);
  const electrumBtcLegacyChange = base58CheckEncode(0x00, hash160(sharedEcdh.getPublicKey(null, 'compressed')));

  // 2. Electrum Native SegWit (m/0'/0/0 & m/0'/1/0)
  const swAcct0 = bip32DeriveChild(master, 0 + H);
  const swExt0 = bip32DeriveChild(swAcct0, 0);
  const swChg1 = bip32DeriveChild(swAcct0, 1);
  const swKey0 = bip32DeriveChild(swExt0, 0).key;
  const swChgKey0 = bip32DeriveChild(swChg1, 0).key;

  const electrumBtcSegwit = pubKeyToBtcSegwit(swKey0);
  const electrumBtcChange = pubKeyToBtcSegwit(swChgKey0);
  const electrumLegacyWif = privKeyToCompressedWif(stdKey0);
  const electrumSegwitWif = privKeyToCompressedWif(swKey0);

  return {
    electrumVersion: getElectrumSeedVersion(mnemonic),
    electrumBtcLegacy,
    electrumBtcLegacyChange,
    electrumBtcSegwit,
    electrumBtcChange,
    electrumLegacyWif,
    electrumSegwitWif
  };
}

// Multi-Language BIP39 Wordlist Support
const MULTI_LANG_WORDLISTS = {
  english: bip39.wordlists.english,
  spanish: bip39.wordlists.spanish || [],
  french: bip39.wordlists.french || [],
  italian: bip39.wordlists.italian || [],
  japanese: bip39.wordlists.japanese || [],
  korean: bip39.wordlists.korean || [],
  chinese_simplified: bip39.wordlists.chinese_simplified || []
};

/**
 * Standards-Compliant Multi-Chain BIP39 Derivation:
 * - BTC Legacy:        m/44'/0'/0'/0/0 (+ 1/0 change)
 * - BTC Nested SegWit: m/49'/0'/0'/0/0 (P2SH-P2WPKH '3...')
 * - BTC Native SegWit: m/84'/0'/0'/0/0..2 (+ 1/0 change, Bech32 'bc1q...')
 * - BTC Taproot:       m/86'/0'/0'/0/0 + BIP341 TapTweak (Bech32m 'bc1p...')
 * - Ethereum / EVM:    m/44'/60'/0'/0/0..1 (EIP-55 '0x...' + exact ETH privateKeyHex)
 * - Litecoin:          m/44'/2'/0'/0/0 ('L...') + m/84'/2'/0'/0/0 ('ltc1q...')
 * - Dogecoin:          m/44'/3'/0'/0/0 ('D...')
 * - Solana:            SLIP-0010 Ed25519 m/44'/501'/0'/0' + 64-byte Base58 Phantom key
 */
function deriveWalletFromMnemonic(mnemonic, passphrase = '', fullAltcoins = true) {
  const cleanMnemonic = mnemonic.trim().replace(/\s+/g, ' ');
  const actualWordCount = cleanMnemonic ? cleanMnemonic.split(' ').length : 12;

  const seed = crypto.pbkdf2Sync(
    cleanMnemonic.normalize('NFKD'),
    'mnemonic' + passphrase.normalize('NFKD'),
    2048,
    64,
    'sha512'
  );
  const master = bip32MasterFromSeed(seed);

  // Share m/44' hardened node across BTC (0'), LTC (2'), DOGE (3'), ETH (60')
  const m44 = bip32DeriveChild(master, 44 + H);
  const btcAcct44 = derivePathNode(m44, [0 + H, 0 + H]);
  const btcExt44 = bip32DeriveChild(btcAcct44, 0);
  const btcChg44 = bip32DeriveChild(btcAcct44, 1);
  const btcKey = bip32DeriveChild(btcExt44, 0).key;
  const btcChangeLegacyKey = bip32DeriveChild(btcChg44, 0).key;

  // BIP49 Nested SegWit: m/49'/0'/0'/0/0
  const btcNestedKey = derivePath(master, [49 + H, 0 + H, 0 + H, 0, 0]);

  // BIP84 Native SegWit: m/84'/0'/0'/0/0..2 + 1/0 change
  const btcAcct84 = derivePathNode(master, [84 + H, 0 + H, 0 + H]);
  const btcExt84 = bip32DeriveChild(btcAcct84, 0);
  const btcChg84 = bip32DeriveChild(btcAcct84, 1);
  const btcSegwitKey = bip32DeriveChild(btcExt84, 0).key;
  const btcSegwitKey1 = bip32DeriveChild(btcExt84, 1).key;
  const btcSegwitKey2 = bip32DeriveChild(btcExt84, 2).key;
  const btcChangeSegwitKey = bip32DeriveChild(btcChg84, 0).key;

  // BIP86 Taproot: m/86'/0'/0'/0/0 + BIP341 TapTweak
  const btcTaprootKey = derivePath(master, [86 + H, 0 + H, 0 + H, 0, 0]);

  // BIP44 Ethereum: m/44'/60'/0'/0/0 & 0/1
  const ethAcct44 = derivePathNode(m44, [60 + H, 0 + H]);
  const ethExt44 = bip32DeriveChild(ethAcct44, 0);
  const ethKey = bip32DeriveChild(ethExt44, 0).key;
  const ethKeyIndex1 = bip32DeriveChild(ethExt44, 1).key;

  // 1. BTC Legacy (m/44'/0'/0'/0/0 -> 1...) + Legacy Change (m/44'/0'/0'/1/0 -> 1...)
  sharedEcdh.setPrivateKey(btcKey);
  const btcH160 = hash160(sharedEcdh.getPublicKey(null, 'compressed'));
  const btcLegacy = base58CheckEncode(0x00, btcH160);

  sharedEcdh.setPrivateKey(btcChangeLegacyKey);
  const btcChangeLegacy = base58CheckEncode(0x00, hash160(sharedEcdh.getPublicKey(null, 'compressed')));

  // 2. BTC Nested SegWit (m/49'/0'/0'/0/0 -> 3...)
  sharedEcdh.setPrivateKey(btcNestedKey);
  const nestedH160 = hash160(sharedEcdh.getPublicKey(null, 'compressed'));
  const redeemScript = Buffer.concat([Buffer.from([0x00, 0x14]), nestedH160]);
  const btcNestedSegwit = base58CheckEncode(0x05, hash160(redeemScript));

  // 3. BTC Native SegWit (m/84'/0'/0'/0/0..2 + 1/0 -> bc1q...)
  const btcSegwit = pubKeyToBtcSegwit(btcSegwitKey);
  const btcSegwitIndex1 = pubKeyToBtcSegwit(btcSegwitKey1);
  const btcSegwitIndex2 = pubKeyToBtcSegwit(btcSegwitKey2);
  const btcChangeSegwit = pubKeyToBtcSegwit(btcChangeSegwitKey);

  // 4. BTC Taproot (m/86'/0'/0'/0/0 + BIP341 TapTweak -> bc1p...)
  const btcTaproot = deriveTaprootAddressFromPrivKey(btcTaprootKey);

  // 5. ETH Address (m/44'/60'/0'/0/0 -> EIP-55 0x...)
  const ethAddress = pubKeyToEthAddress(ethKey);
  const ethAddressIndex1 = pubKeyToEthAddress(ethKeyIndex1);

  // 6. Litecoin (m/44'/2'/0'/0/0 -> L...)
  const ltcKey = derivePath(m44, [2 + H, 0 + H, 0, 0]);
  sharedEcdh.setPrivateKey(ltcKey);
  const ltcH160 = hash160(sharedEcdh.getPublicKey(null, 'compressed'));
  const ltcAddress = base58CheckEncode(0x30, ltcH160);

  // Chain-accurate export keys!
  const ethPrivHex = ethKey.toString('hex');
  const btcPrivHex = btcKey.toString('hex');
  const btcLegacyWif = privKeyToCompressedWif(btcKey);
  const btcNestedWif = privKeyToCompressedWif(btcNestedKey);
  const btcSegwitWif = privKeyToCompressedWif(btcSegwitKey);
  const btcTaprootWif = privKeyToCompressedWif(btcTaprootKey);
  const btcWifUncompressed = base58CheckEncode(0x80, btcKey);
  // Each Electrum import descriptor uses the exact WIF for its own BIP44/BIP49/BIP84 address!
  const electrumImportBlock = `p2pkh:${btcLegacyWif}\np2wpkh-p2sh:${btcNestedWif}\np2wpkh:${btcSegwitWif}`;

  const childAddresses = [
    btcSegwitIndex1,
    btcSegwitIndex2,
    btcChangeSegwit,
    btcChangeLegacy,
    ethAddressIndex1
  ];

  if (!fullAltcoins) {
    return {
      mnemonic: cleanMnemonic,
      passphrase,
      wordCount: actualWordCount,
      // Keep privateKeyHex equal to the m/44'/60'/0'/0/0 EVM key so MetaMask/Rabby imports unlock ethAddress directly!
      privateKeyHex: ethPrivHex,
      ethPrivateKeyHex: '0x' + ethPrivHex,
      btcPrivateKeyHex: btcPrivHex,
      privateKey: ethPrivHex,
      btcWif: btcSegwitWif,
      btcLegacyWif,
      btcNestedWif,
      btcSegwitWif,
      btcTaprootWif,
      btcWifUncompressed,
      electrumImportBlock,
      btcLegacy,
      btcNestedSegwit,
      btcP2sh: btcNestedSegwit,
      btcSegwit,
      btcTaproot,
      btcSegwitIndex1,
      btcSegwitIndex2,
      btcChangeSegwit,
      btcChangeLegacy,
      ethAddress,
      ethAddressIndex1,
      childAddresses,
      ltcAddress
    };
  }

  const ltcSegwitKey = derivePath(master, [84 + H, 2 + H, 0 + H, 0, 0]);
  sharedEcdh.setPrivateKey(ltcSegwitKey);
  const ltcSegwitAddress = bech32EncodeWitness('ltc', 0, hash160(sharedEcdh.getPublicKey(null, 'compressed')), false);

  const dogeKey = derivePath(m44, [3 + H, 0 + H, 0, 0]);
  sharedEcdh.setPrivateKey(dogeKey);
  const dogeAddress = base58CheckEncode(0x1E, hash160(sharedEcdh.getPublicKey(null, 'compressed')));

  // Official BIP44 TRON: m/44'/195'/0'/0/0 -> Base58Check 'T...' + 20-byte hex '0x...'
  const trxKey = derivePath(m44, [195 + H, 0 + H, 0, 0]);
  const trx = privKeyToTronAddress(trxKey);
  const trxPrivateKeyHex = trxKey.toString('hex');

  // Official BIP44 XRP Ledger: m/44'/144'/0'/0/0 -> Ripple Base58Check 'r...'
  const xrpKey = derivePath(m44, [144 + H, 0 + H, 0, 0]);
  const xrpAddress = privKeyToXrpAddress(xrpKey);
  const xrpPrivateKeyHex = xrpKey.toString('hex');

  const sol = deriveSolanaFromSeed(seed, 0);

  return {
    mnemonic: cleanMnemonic,
    passphrase,
    wordCount: actualWordCount,
    privateKeyHex: ethPrivHex,
    ethPrivateKeyHex: '0x' + ethPrivHex,
    btcPrivateKeyHex: btcPrivHex,
    trxPrivateKeyHex,
    xrpPrivateKeyHex,
    privateKey: ethPrivHex,
    btcWif: btcSegwitWif,
    btcLegacyWif,
    btcNestedWif,
    btcSegwitWif,
    btcTaprootWif,
    solPrivateKeyBase58: sol.solPrivateKeyBase58,
    btcWifUncompressed,
    electrumImportBlock,
    btcLegacy,
    btcNestedSegwit,
    btcP2sh: btcNestedSegwit,
    btcSegwit,
    btcTaproot,
    btcSegwitIndex1,
    btcSegwitIndex2,
    btcChangeSegwit,
    btcChangeLegacy,
    ethAddress,
    ethAddressIndex1,
    childAddresses,
    trxAddress: trx.trxAddress,
    trxHexAddress: trx.trxHexAddress,
    xrpAddress,
    solAddress: sol.solAddress,
    ltcAddress,
    ltcSegwitAddress,
    dogeAddress
  };
}

/**
 * Real Multi-Client Custom HD Path Derivation (Ledger Live, Legacy MEW, Multi-Account BIP84, Bitcoin Core)
 * Actually derives keys & addresses on the exact displayed path instead of discarding the path label.
 */
function deriveCustomPathWallet(mnemonic, variantIndex = 0) {
  const base = deriveWalletFromMnemonic(mnemonic, '', true);
  const seed = crypto.pbkdf2Sync(mnemonic.trim().normalize('NFKD'), 'mnemonic', 2048, 64, 'sha512');
  const master = bip32MasterFromSeed(seed);
  const profile = variantIndex % 4;
  const acct = (Math.floor(variantIndex / 4) % 4) + 1;
  const addrIdx = variantIndex % 5;

  if (profile === 0) {
    // Multi-Account BIP84 Native SegWit: m/84'/0'/acct'/0/addrIdx
    const pathTag = `m/84'/0'/${acct}'/0/${addrIdx}`;
    const k = derivePath(master, [84 + H, 0 + H, acct + H, 0, addrIdx]);
    const sw = pubKeyToBtcSegwit(k);
    const wif = privKeyToCompressedWif(k);
    return {
      ...base,
      customPath: pathTag,
      btcSegwit: sw,
      btcWif: wif,
      btcSegwitWif: wif,
      electrumImportBlock: `p2pkh:${base.btcLegacyWif}\np2wpkh-p2sh:${base.btcNestedWif}\np2wpkh:${wif}`
    };
  }
  if (profile === 1) {
    // Ledger Live ETH Account: m/44'/60'/acct'/0/0
    const pathTag = `m/44'/60'/${acct}'/0/0`;
    const k = derivePath(master, [44 + H, 60 + H, acct + H, 0, 0]);
    const eth = pubKeyToEthAddress(k);
    const hex = k.toString('hex');
    return {
      ...base,
      customPath: pathTag,
      ethAddress: eth,
      privateKeyHex: hex,
      ethPrivateKeyHex: '0x' + hex,
      privateKey: hex
    };
  }
  if (profile === 2) {
    // Legacy MEW / MyCrypto / Jaxx Path: m/44'/60'/0'/addrIdx
    const idx = addrIdx + 1;
    const pathTag = `m/44'/60'/0'/${idx}`;
    const k = derivePath(master, [44 + H, 60 + H, 0 + H, idx]);
    const eth = pubKeyToEthAddress(k);
    const hex = k.toString('hex');
    return {
      ...base,
      customPath: pathTag,
      ethAddress: eth,
      privateKeyHex: hex,
      ethPrivateKeyHex: '0x' + hex,
      privateKey: hex
    };
  }
  // Bitcoin Core HD Hardened Path: m/0'/0'/addrIdx'
  const pathTag = `m/0'/0'/${addrIdx}'`;
  const k = derivePath(master, [0 + H, 0 + H, addrIdx + H]);
  sharedEcdh.setPrivateKey(k);
  const leg = base58CheckEncode(0x00, hash160(sharedEcdh.getPublicKey(null, 'compressed')));
  const sw = pubKeyToBtcSegwit(k);
  const wif = privKeyToCompressedWif(k);
  return {
    ...base,
    customPath: pathTag,
    btcLegacy: leg,
    btcSegwit: sw,
    btcLegacyWif: wif,
    btcSegwitWif: wif,
    btcWif: wif,
    electrumImportBlock: `p2pkh:${wif}\np2wpkh-p2sh:${base.btcNestedWif}\np2wpkh:${wif}`
  };
}

/**
 * Well-known public addresses (BIP39/BIP84/BIP86 test vectors, Bitcoin puzzle
 * keys, famous brainwallets, early dormant whales).
 *
 * IMPORTANT: these entries intentionally carry **no balances, no transaction
 * counts and no dollar values**. The addresses are real and can be checked in
 * any block explorer, but any amount shown by this tool must come from a live
 * chain query — never from a table baked into the source. Older builds shipped
 * invented figures here ("Ever Held 5.18 BTC", "246 TX") which showed up as
 * findings; that data has been removed.
 */
const KNOWN_ACTIVITY_MAP = new Map([
  // BIP39 12-word test vectors
  ['1HQ3rb7nyLPrjnuW85MUknPekwkn7poAUm', { label: '12-Word BIP39 test vector ("army van defense…")' }],
  ['bc1qg0azlj4w2lrq8jssrrz6eprt2fe7f7edm4vpd5', { label: '12-Word BIP39 test vector ("army van defense…") — SegWit' }],
  ['1MKja6EgoqQ2BbQ8NqB8fqGCcD2gwoEeFu', { label: '12-Word BIP39 test vector ("gravity machine north…")' }],
  ['bc1qx276rzgd89qruxusqhhzncq32c9z6xt6fxckrc', { label: '12-Word BIP39 test vector ("gravity machine north…") — SegWit' }],
  ['1MeoRMhpR25zd5TCiw86t7TtwbfA4GwfR5', { label: '12-Word BIP39 test vector ("void come effort…")' }],
  ['bc1qhf097zrj2strlfuwyzy4uflzeqne4stymx500d', { label: '12-Word BIP39 test vector ("void come effort…") — SegWit' }],
  ['1E1Nz7Ps3spNzkAgUtCGfzN1xbRFAWusDU', { label: '12-Word BIP39 test vector ("planet crystal ocean…")' }],
  ['bc1qan690255yqv09y0xjrw57nytmtktevq260hvy0', { label: '12-Word BIP39 test vector ("planet crystal ocean…") — SegWit' }],
  // BIP spec vectors
  ['1LqBGSKuX5yYUonjxT5qGfpUsXKYYWeabA', { label: 'BIP39 standard test vector (m/44\'/0\'/0\'/0/0)' }],
  ['bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu', { label: 'BIP84 native SegWit vector (m/84\'/0\'/0\'/0/0)' }],
  ['bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr', { label: 'BIP86 Taproot vector (m/86\'/0\'/0\'/0/0)' }],
  // Bitcoin puzzle keys
  ['1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMH', { label: 'Bitcoin puzzle #1 (private key 0x01, compressed)' }],
  ['1EHNa6Q4Jz2uvNExL497mE43ikXhwF6kZm', { label: 'Bitcoin puzzle #1 (private key 0x01, uncompressed)' }],
  // Famous brainwallets
  ['1JWnE6p6UN7ZJBN7TtcbNDoRcjFtuDWoNL', { label: 'Brainwallet SHA256("correct horse battery staple")' }],
  ['1C7zdTfnkzmr13HfA2vNm5SJYRK6nEKyq8', { label: 'Brainwallet SHA256("correct horse battery staple") — alt' }],
  ['bc1q08alc0e5ua69scxhvyma568nvguqccrv4cc9n4', { label: 'Brainwallet SHA256("correct horse battery staple") — SegWit' }],
  ['16ga2uqnF1NqpAuQeeg7sTCAdtX6dyjkMx', { label: 'Brainwallet SHA256("password")' }],
  ['16qVRutZ7rZuPx7NMtapvZorWYjyaME2Ue', { label: 'Brainwallet SHA256("password") — compressed' }],
  ['1xm4vFerV3pSgvBFkyzLgT1Ew3HQYrS1V', { label: 'Brainwallet SHA256("satoshi")' }],
  ['18VkRiDhFu2Z17AvtpU3vL2LbTXDzCvDVo', { label: 'Brainwallet SHA256("bitcoin")' }],
  ['1MzNY1oA3kfgYi75zquj3SRUPYztzXHzK9', { label: 'Brainwallet SHA256("123456")' }],
  ['19tbJ5SdTaw3bNeB4Zz67J46LPYbXq9382', { label: 'Brainwallet SHA256("nakamoto")' }],
  ['1PrBiw8DssERdXT2Q2pNo4z21d7m7uYxyj', { label: 'Brainwallet SHA256("hal finney")' }],
  ['1Bv8QzG61KrzKGjc2gCHuyc2p4rbnjxNPN', { label: 'Brainwallet SHA256("blockchain")' }]
]);

function levenshtein(a, b) {
  const dp = Array.from({ length: a.length + 1 }, () => new Array(b.length + 1).fill(0));
  for (let i = 0; i <= a.length; i++) dp[i][0] = i;
  for (let j = 0; j <= b.length; j++) dp[0][j] = j;
  for (let i = 1; i <= a.length; i++) {
    for (let j = 1; j <= b.length; j++) {
      dp[i][j] = a[i - 1] === b[j - 1]
        ? dp[i - 1][j - 1]
        : 1 + Math.min(dp[i - 1][j], dp[i][j - 1], dp[i - 1][j - 1]);
    }
  }
  return dp[a.length][b.length];
}

/**
 * Ultra-Fast Native OpenSSL Screener:
 * Computes all 4 real standard address formats (P2PKH Legacy 1..., P2SH-SegWit 3...,
 * Native SegWit bc1q..., and lowercase EVM 0x...) for real target & watchlist matching.
 */
const screenerRedeemBuf = Buffer.concat([Buffer.from([0x00, 0x14]), Buffer.alloc(20)]);
function deriveUltraFastScreener(privKeyBuf) {
  sharedEcdh.setPrivateKey(privKeyBuf);
  const pubComp = sharedEcdh.getPublicKey(null, 'compressed');
  const pubUncomp = sharedEcdh.getPublicKey(null, 'uncompressed');
  const h160 = hash160(pubComp);
  const btcLegacy = base58CheckEncode(0x00, h160);
  const btcLegacyUncompressed = base58CheckEncode(0x00, hash160(pubUncomp));
  h160.copy(screenerRedeemBuf, 2);
  const btcNestedSegwit = base58CheckEncode(0x05, hash160(screenerRedeemBuf));
  const btcSegwit = bech32EncodeBc0(h160);
  const addr20 = keccak256PubKey64(pubUncomp.subarray(1)).subarray(12, 32);
  const ethAddressLower = '0x' + addr20.toString('hex');
  const trxAddress = base58CheckEncode(0x41, addr20);
  return { btcLegacy, btcLegacyUncompressed, btcNestedSegwit, btcSegwit, ethAddressLower, trxAddress, h160 };
}

// =========================================================================
// CANONICAL MULTICALL3 BATCH ENGINE (ETH, ARBITRUM, BASE, OPTIMISM, AVAX, POLYGON, BNB)
// Deterministic CREATE2 deployment on 100+ EVM networks:
//   0xcA11bde05977b3631167028862bE2a173976CA11
// =========================================================================
const MULTICALL3_ADDRESS = '0xcA11bde05977b3631167028862bE2a173976CA11';
const SEL_AGGREGATE3 = '82ad56cb';      // aggregate3((address,bool,bytes)[])
const SEL_GET_ETH_BALANCE = '4d2301cc'; // getEthBalance(address)
const SEL_BALANCE_OF = '70a08231';      // balanceOf(address)

const EVM_MULTICALL_CHAINS = {
  eth: {
    id: 'eth', name: 'Ethereum', symbol: 'ETH', priceKey: 'eth',
    rpcs: ['https://ethereum-rpc.publicnode.com', 'https://cloudflare-eth.com', 'https://rpc.ankr.com/eth'],
    tokens: [
      { symbol: 'USDT', decimals: 6, usdPeg: 1, address: '0xdAC17F958D2ee523a2206206994597C13D831ec7' },
      { symbol: 'USDC', decimals: 6, usdPeg: 1, address: '0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48' },
      { symbol: 'DAI',  decimals: 18, usdPeg: 1, address: '0x6B175474E89094C44Da98b954EedeAC495271d0F' },
      { symbol: 'WBTC', decimals: 8, priceKey: 'btc', address: '0x2260FAC5E5542a773Aa44fBCfeDf7C193bc2C599' }
    ]
  },
  arb: {
    id: 'arb', name: 'Arbitrum One', symbol: 'ETH', priceKey: 'eth',
    rpcs: ['https://arbitrum-one-rpc.publicnode.com', 'https://arb1.arbitrum.io/rpc'],
    tokens: [
      { symbol: 'USDT', decimals: 6, usdPeg: 1, address: '0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9' },
      { symbol: 'USDC', decimals: 6, usdPeg: 1, address: '0xaf88d065e77c8cC2239327C5EDb3A432268e5831' },
      { symbol: 'WBTC', decimals: 8, priceKey: 'btc', address: '0x2f2a2543B76A4166549F7aaB2e75Bef0aefC5B0f' }
    ]
  },
  base: {
    id: 'base', name: 'Base Mainnet', symbol: 'ETH', priceKey: 'eth',
    rpcs: ['https://base-rpc.publicnode.com', 'https://mainnet.base.org'],
    tokens: [
      { symbol: 'USDC', decimals: 6, usdPeg: 1, address: '0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913' }
    ]
  },
  op: {
    id: 'op', name: 'Optimism', symbol: 'ETH', priceKey: 'eth',
    rpcs: ['https://optimism-rpc.publicnode.com', 'https://mainnet.optimism.io'],
    tokens: [
      { symbol: 'USDT', decimals: 6, usdPeg: 1, address: '0x94b008aA00579c1307B0EF2c499aD98a8ce58e58' },
      { symbol: 'USDC', decimals: 6, usdPeg: 1, address: '0x0b2C639c533813f4Aa9D7837CAf62653d097Ff85' }
    ]
  },
  avax: {
    id: 'avax', name: 'Avalanche C-Chain', symbol: 'AVAX', priceKey: 'avax',
    rpcs: ['https://avalanche-c-chain-rpc.publicnode.com', 'https://api.avax.network/ext/bc/C/rpc'],
    tokens: [
      { symbol: 'USDT', decimals: 6, usdPeg: 1, address: '0x9702230A8Ea53601f5cD2dc00fDBc13d4dF4A8c7' },
      { symbol: 'USDC', decimals: 6, usdPeg: 1, address: '0xB97EF9Ef8734C71904D8002F8b6Bc66Dd9c48a6E' }
    ]
  },
  poly: {
    id: 'poly', name: 'Polygon POS', symbol: 'POL', priceKey: 'pol',
    rpcs: ['https://polygon-bor-rpc.publicnode.com', 'https://polygon-rpc.com'],
    tokens: [
      { symbol: 'USDT', decimals: 6, usdPeg: 1, address: '0xc2132D05D31c914a87C6611C10748AEb04B58e8F' },
      { symbol: 'USDC', decimals: 6, usdPeg: 1, address: '0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359' }
    ]
  },
  bsc: {
    id: 'bsc', name: 'BNB Smart Chain', symbol: 'BNB', priceKey: 'bnb',
    rpcs: ['https://bsc-rpc.publicnode.com', 'https://bsc-dataseed.binance.org'],
    tokens: [
      { symbol: 'USDT', decimals: 18, usdPeg: 1, address: '0x55d398326f99059fF775485246999027B3197955' },
      { symbol: 'USDC', decimals: 18, usdPeg: 1, address: '0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d' }
    ]
  }
};

function padHex32(hexClean) {
  return String(hexClean).replace(/^0x/i, '').toLowerCase().padStart(64, '0');
}
function uintToHex32(n) {
  return BigInt(n).toString(16).padStart(64, '0');
}

/**
 * Encodes `Multicall3.aggregate3((address target, bool allowFailure, bytes callData)[])`
 * into a single canonical ABI hex payload (`0x82ad56cb...`).
 */
function encodeMulticall3Aggregate3(calls) {
  const n = calls.length;
  const tupleBlobs = [];
  for (let i = 0; i < n; i++) {
    const c = calls[i];
    const targetWord = padHex32(c.target);
    const allowWord = c.allowFailure === false ? uintToHex32(0) : uintToHex32(1);
    const dataHex = String(c.callData || '').replace(/^0x/i, '').toLowerCase();
    const byteLen = dataHex.length / 2;
    const paddedHexLen = Math.ceil(dataHex.length / 64) * 64;
    const paddedData = dataHex.padEnd(paddedHexLen, '0');
    const tupleHex =
      targetWord +
      allowWord +
      uintToHex32(96) +
      uintToHex32(byteLen) +
      paddedData;
    tupleBlobs.push(tupleHex);
  }
  let currentOffset = n * 32;
  let offsetsHex = '';
  for (let i = 0; i < n; i++) {
    offsetsHex += uintToHex32(currentOffset);
    currentOffset += tupleBlobs[i].length / 2;
  }
  return '0x' + SEL_AGGREGATE3 + uintToHex32(32) + uintToHex32(n) + offsetsHex + tupleBlobs.join('');
}

/**
 * Decodes `Multicall3.aggregate3` ABI return payload `(bool success, bytes returnData)[]`.
 */
function decodeMulticall3Aggregate3(rawHex) {
  if (!rawHex || typeof rawHex !== 'string' || rawHex === '0x' || rawHex.length < 130) return [];
  const hex = rawHex.startsWith('0x') ? rawHex.slice(2) : rawHex;
  const readWordBigInt = (bytePos) => {
    const slice = hex.slice(bytePos * 2, bytePos * 2 + 64);
    return slice.length === 64 ? BigInt('0x' + slice) : 0n;
  };
  const arrOffset = Number(readWordBigInt(0));
  const arrLen = Number(readWordBigInt(arrOffset));
  const results = [];
  const basePos = arrOffset + 32;
  for (let i = 0; i < arrLen; i++) {
    const elemRelOffset = Number(readWordBigInt(basePos + i * 32));
    const elemPos = basePos + elemRelOffset;
    const success = readWordBigInt(elemPos) !== 0n;
    const bytesRelOffset = Number(readWordBigInt(elemPos + 32));
    const bytesPos = elemPos + bytesRelOffset;
    const bytesLen = Number(readWordBigInt(bytesPos));
    const dataStartHex = (bytesPos + 32) * 2;
    const returnDataHex = hex.slice(dataStartHex, dataStartHex + bytesLen * 2);
    const uint256 = returnDataHex.length >= 64 ? BigInt('0x' + returnDataHex.slice(0, 64)) : 0n;
    results.push({ success, returnData: '0x' + returnDataHex, uint256 });
  }
  return results;
}

/**
 * Builds a complete `Multicall3` call descriptor batch for an array of EVM addresses on a given chain:
 *   - Native coin balance via `Multicall3.getEthBalance(addr)`
 *   - All configured ERC-20 token balances (`USDT`, `USDC`, `WBTC`, `DAI`) via `token.balanceOf(addr)`
 */
function buildMulticall3WalletSweep(addresses, chainConfig) {
  const calls = [];
  const meta = [];
  const tokens = (chainConfig && Array.isArray(chainConfig.tokens)) ? chainConfig.tokens : [];
  for (const addr of addresses) {
    if (!addr || !addr.startsWith('0x') || addr.length !== 42) continue;
    const paddedAddr = padHex32(addr);
    calls.push({
      target: MULTICALL3_ADDRESS,
      allowFailure: true,
      callData: '0x' + SEL_GET_ETH_BALANCE + paddedAddr
    });
    meta.push({ address: addr, kind: 'native', symbol: chainConfig.symbol, decimals: 18, priceKey: chainConfig.priceKey });

    for (const tok of tokens) {
      calls.push({
        target: tok.address,
        allowFailure: true,
        callData: '0x' + SEL_BALANCE_OF + paddedAddr
      });
      meta.push({
        address: addr,
        kind: 'erc20',
        symbol: tok.symbol,
        decimals: tok.decimals,
        usdPeg: tok.usdPeg || 0,
        priceKey: tok.priceKey || null
      });
    }
  }
  return {
    to: MULTICALL3_ADDRESS,
    data: calls.length > 0 ? encodeMulticall3Aggregate3(calls) : null,
    meta,
    callCount: calls.length
  };
}

// =========================================================================
// KEYSTORE V3 (SCRYPT / PBKDF2) & METAMASK VAULT DECRYPTION + MUTATIONS
// =========================================================================

/**
 * Verifies & decrypts an Ethereum Web3 Secret Storage V3 Keystore JSON (`scrypt` or `pbkdf2`).
 * Returns `{ valid: true, wallet }` on exact MAC verification or `{ valid: false }`.
 */
function decryptEthereumKeystoreV3(keystoreObj, password) {
  const ks = typeof keystoreObj === 'string' ? JSON.parse(keystoreObj) : keystoreObj;
  const cryptoSection = ks && (ks.crypto || ks.Crypto);
  if (!cryptoSection || !cryptoSection.kdf || !cryptoSection.ciphertext || !cryptoSection.mac) {
    throw new Error('Invalid Ethereum Keystore V3 JSON structure');
  }
  const kdf = String(cryptoSection.kdf).toLowerCase();
  const kdfparams = cryptoSection.kdfparams || {};
  const salt = Buffer.from(String(kdfparams.salt || '').replace(/^0x/i, ''), 'hex');
  const dklen = Number(kdfparams.dklen || 32);
  let derivedKey;

  if (kdf === 'scrypt') {
    const N = Number(kdfparams.n || 262144);
    const r = Number(kdfparams.r || 8);
    const p = Number(kdfparams.p || 1);
    derivedKey = crypto.scryptSync(Buffer.from(String(password), 'utf8'), salt, dklen, {
      N,
      r,
      p,
      maxmem: Math.max(64 * 1024 * 1024, 256 * N * r)
    });
  } else if (kdf === 'pbkdf2') {
    const c = Number(kdfparams.c || 262144);
    const prf = String(kdfparams.prf || 'hmac-sha256').toLowerCase();
    const digest = prf.includes('512') ? 'sha512' : 'sha256';
    derivedKey = crypto.pbkdf2Sync(Buffer.from(String(password), 'utf8'), salt, c, dklen, digest);
  } else {
    throw new Error(`Unsupported KDF: ${kdf}`);
  }

  const ciphertext = Buffer.from(String(cryptoSection.ciphertext).replace(/^0x/i, ''), 'hex');
  const expectedMac = String(cryptoSection.mac).replace(/^0x/i, '').toLowerCase();
  // Official Web3 V3 MAC = Keccak256(derivedKey[16..32] || ciphertext)
  const macInput = Buffer.concat([derivedKey.subarray(16, 32), ciphertext]);
  const actualMac = keccak256(macInput).toString('hex').toLowerCase();

  if (actualMac !== expectedMac) {
    return { valid: false };
  }

  const cipherAlg = String(cryptoSection.cipher || 'aes-128-ctr').toLowerCase();
  const iv = Buffer.from(String((cryptoSection.cipherparams || {}).iv || '').replace(/^0x/i, ''), 'hex');
  const decipher = crypto.createDecipheriv(cipherAlg, derivedKey.subarray(0, 16), iv);
  const privKeyBuf = Buffer.concat([decipher.update(ciphertext), decipher.final()]);

  return {
    valid: true,
    vaultType: `Ethereum Keystore V3 (${kdf.toUpperCase()})`,
    password,
    privateKeyHex: privKeyBuf.toString('hex'),
    wallet: deriveAddressesFromPrivKey(privKeyBuf)
  };
}

/**
 * Decrypts a MetaMask Browser Extension Vault JSON (`{ data, iv, salt, keyMetadata? }`)
 * using PBKDF2-SHA256 (supports MetaMask legacy 10,000 AND modern 600,000 iterations) + AES-256-GCM.
 */
function decryptMetaMaskVault(vaultObj, password) {
  const v = typeof vaultObj === 'string' ? JSON.parse(vaultObj) : vaultObj;
  if (!v || !v.data || !v.iv || !v.salt) {
    throw new Error('Invalid MetaMask Vault JSON (requires data, iv, salt)');
  }
  const salt = Buffer.from(v.salt, 'base64');
  const iv = Buffer.from(v.iv, 'base64');
  const rawPayload = Buffer.from(v.data, 'base64');
  if (rawPayload.length < 17) return { valid: false };

  const ciphertext = rawPayload.subarray(0, rawPayload.length - 16);
  const authTag = rawPayload.subarray(rawPayload.length - 16);

  const configuredIter = v.keyMetadata && v.keyMetadata.params && v.keyMetadata.params.iterations
    ? [Number(v.keyMetadata.params.iterations)]
    : [600000, 10000];

  for (const iterations of configuredIter) {
    try {
      const key = crypto.pbkdf2Sync(Buffer.from(String(password), 'utf8'), salt, iterations, 32, 'sha256');
      const decipher = crypto.createDecipheriv('aes-256-gcm', key, iv);
      decipher.setAuthTag(authTag);
      const plainBuf = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
      const decryptedJson = JSON.parse(plainBuf.toString('utf8'));

      // Extract mnemonic or imported private keys automatically
      let extractedMnemonic = null;
      const extractedPrivateKeys = [];
      const walk = (node) => {
        if (!node) return;
        if (Array.isArray(node)) {
          for (const item of node) walk(item);
          return;
        }
        if (typeof node === 'object') {
          if (node.mnemonic) {
            if (Array.isArray(node.mnemonic)) {
              extractedMnemonic = Buffer.from(node.mnemonic).toString('utf8').trim();
            } else if (typeof node.mnemonic === 'string') {
              extractedMnemonic = node.mnemonic.trim();
            }
          }
          if (Array.isArray(node.privateKeys)) {
            for (const pk of node.privateKeys) {
              if (typeof pk === 'string') extractedPrivateKeys.push(pk.replace(/^0x/i, ''));
            }
          }
          for (const val of Object.values(node)) walk(val);
        }
      };
      walk(decryptedJson);

      let wallet = null;
      if (extractedMnemonic && bip39.validateMnemonic(extractedMnemonic)) {
        wallet = deriveWalletFromMnemonic(extractedMnemonic);
      } else if (extractedPrivateKeys.length > 0 && /^[0-9a-fA-F]{64}$/.test(extractedPrivateKeys[0])) {
        wallet = deriveAddressesFromPrivKey(Buffer.from(extractedPrivateKeys[0], 'hex'));
      }

      return {
        valid: true,
        vaultType: `MetaMask Vault (PBKDF2 ${iterations.toLocaleString()} iter + AES-256-GCM)`,
        password,
        mnemonic: extractedMnemonic,
        privateKeys: extractedPrivateKeys,
        wallet,
        rawKeyrings: decryptedJson
      };
    } catch (_) {
      // Wrong password or wrong iteration count -> continue
    }
  }
  return { valid: false };
}

/**
 * Auto-detects whether JSON is an Ethereum Keystore V3 or MetaMask Vault and attempts decryption.
 */
function decryptVaultOrKeystore(jsonInput, password) {
  const obj = typeof jsonInput === 'string' ? JSON.parse(jsonInput) : jsonInput;
  if (obj && (obj.crypto || obj.Crypto)) {
    return decryptEthereumKeystoreV3(obj, password);
  }
  if (obj && obj.data && obj.iv && obj.salt) {
    return decryptMetaMaskVault(obj, password);
  }
  throw new Error('Unrecognized vault JSON: paste either an Ethereum Keystore V3 JSON or MetaMask Vault JSON ({data, iv, salt})');
}

/**
 * Generates high-probability human password / BIP39 25th-passphrase mutations from base hints:
 * Case transforms, leetspeak, common crypto suffixes/years/symbols, reversals, and combinations.
 */
function generatePasswordMutations(hints = [], maxCandidates = 25000) {
  const seen = new Set(['']);
  const add = (s) => {
    if (seen.size >= maxCandidates) return;
    if (typeof s === 'string') seen.add(s);
  };

  const baseWords = hints
    .map(w => String(w || '').trim())
    .filter(Boolean);

  const suffixes = [
    '', '!', '!!', '@', '#', '$', '1', '12', '123', '123!', '1234', '123456',
    '01', '07', '69', '88', '99', '007',
    '2017', '2018', '2019', '2020', '2021', '2022', '2023', '2024', '2025', '2026',
    '!2021', '!2024', '@123', '_123'
  ];
  const prefixes = ['', '!', '@', '123', 'my', 'My', 'crypto', 'Crypto', 'btc', 'eth'];

  const leetMap = { a: '@', A: '4', e: '3', E: '3', i: '1', I: '1', o: '0', O: '0', s: '$', S: '5', t: '7' };
  const toLeet = (s) => s.replace(/[aAeEiIoOsSt]/g, ch => leetMap[ch] || ch);

  const variantsPerWord = new Map();
  for (const w of baseWords) {
    const vars = new Set([
      w,
      w.toLowerCase(),
      w.toUpperCase(),
      w.charAt(0).toUpperCase() + w.slice(1).toLowerCase(),
      w.charAt(0).toLowerCase() + w.slice(1).toUpperCase(),
      toLeet(w),
      toLeet(w.charAt(0).toUpperCase() + w.slice(1).toLowerCase()),
      w.split('').reverse().join('')
    ]);
    variantsPerWord.set(w, Array.from(vars));
    for (const v of vars) {
      add(v);
      for (const suf of suffixes) add(v + suf);
      for (const pre of prefixes) if (pre) add(pre + v);
    }
  }

  // Pairwise combinations between distinct hint words (e.g., first word + birth year / pet + symbol)
  const keys = Array.from(variantsPerWord.keys());
  const joiners = ['', '_', '-', '.', '@', '!', ' '];
  for (let i = 0; i < keys.length && seen.size < maxCandidates; i++) {
    for (let j = 0; j < keys.length && seen.size < maxCandidates; j++) {
      if (i === j) continue;
      const vA = variantsPerWord.get(keys[i]).slice(0, 4);
      const vB = variantsPerWord.get(keys[j]).slice(0, 4);
      for (const a of vA) {
        for (const b of vB) {
          for (const sep of joiners) {
            const combo = a + sep + b;
            add(combo);
            add(combo + '!');
            add(combo + '123');
          }
        }
      }
    }
  }

  return Array.from(seen);
}

module.exports = {
  WORDLIST,
  WORD_TO_INDEX,
  MULTI_LANG_WORDLISTS,
  bip39,
  keccak256,
  base58Encode,
  base58Decode,
  base58CheckEncode,
  tronAddressToHex20,
  privKeyToTronAddress,
  privKeyToXrpAddress,
  toEip55ChecksumAddress,
  deriveTaprootAddressFromPrivKey,
  deriveSolanaFromSeed,
  deriveSolanaFromEd25519Seed32,
  deriveAddressesFromPrivKey,
  deriveUltraFastScreener,
  deriveWalletFromMnemonic,
  deriveCustomPathWallet,
  deriveElectrumDualAddresses,
  generateValidElectrumSeed,
  getElectrumSeedVersion,
  buildChecksumValid12WordDirect,
  derivePath,
  derivePathNode,
  bip32MasterFromSeed,
  pubKeyToBtcSegwit,
  pubKeyToEthAddress,
  MULTICALL3_ADDRESS,
  EVM_MULTICALL_CHAINS,
  encodeMulticall3Aggregate3,
  decodeMulticall3Aggregate3,
  buildMulticall3WalletSweep,
  decryptEthereumKeystoreV3,
  decryptMetaMaskVault,
  decryptVaultOrKeystore,
  generatePasswordMutations,
  H,
  KNOWN_ACTIVITY_MAP,
  levenshtein
};
