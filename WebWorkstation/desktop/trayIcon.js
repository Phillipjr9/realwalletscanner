'use strict';

/**
 * Menu-bar template icon for the desktop shell.
 *
 * Generated procedurally as a monochrome (black + alpha) PNG. macOS renders
 * template images automatically for light and dark menu bars, so one asset is
 * enough. Both 1x (18 px) and 2x (36 px) representations are embedded so the
 * icon stays sharp on Retina displays.
 */

const zlib = require('zlib');

function crc32(buf) {
  let c;
  const table = [];
  for (let n = 0; n < 256; n++) {
    c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  let crc = 0xffffffff;
  for (let i = 0; i < buf.length; i++) crc = table[(crc ^ buf[i]) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

function encodePng(width, height, rgba) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0; // filter: none
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }

  return Buffer.concat([
    signature,
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0))
  ]);
}

/** Coverage of the shield-with-keyhole glyph at a point, in 0..1. */
function coverage(x, y, size) {
  const c = size / 2;
  const yTop = size * 0.13;
  const yShoulder = size * 0.50;
  const yBottom = size * 0.88;
  const halfW = size * 0.335;

  if (y < yTop || y > yBottom) return 0;

  let w;
  if (y <= yShoulder) {
    w = halfW;
    // Softly rounded top corners
    const cornerR = size * 0.10;
    if (y < yTop + cornerR) {
      const dx = Math.abs(x - c) - (halfW - cornerR);
      const dy = (yTop + cornerR) - y;
      if (dx > 0 && Math.hypot(dx, dy) > cornerR) return 0;
    }
  } else {
    const t = (y - yShoulder) / (yBottom - yShoulder);
    w = halfW * Math.pow(1 - t, 0.62);
  }

  if (Math.abs(x - c) > w) return 0;

  // Keyhole cut-out (transparent punch-through)
  const headR = size * 0.112;
  const headY = size * 0.395;
  if (Math.hypot(x - c, y - headY) <= headR) return 0;

  const stemTop = headY + headR * 0.30;
  const stemBottom = size * 0.70;
  if (y >= stemTop && y <= stemBottom) {
    const t = (y - stemTop) / (stemBottom - stemTop);
    const hw = (size * 0.082) * (1 - t) + (size * 0.022) * t;
    if (Math.abs(x - c) <= hw) return 0;
  }

  return 1;
}

/** Render at 4x supersampling for smooth anti-aliased edges. */
function render(size) {
  const ss = 4;
  const rgba = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let acc = 0;
      for (let sy = 0; sy < ss; sy++) {
        for (let sx = 0; sx < ss; sx++) {
          acc += coverage(x + (sx + 0.5) / ss, y + (sy + 0.5) / ss, size);
        }
      }
      const alpha = Math.round((acc / (ss * ss)) * 255);
      const i = (y * size + x) * 4;
      rgba[i] = 0;
      rgba[i + 1] = 0;
      rgba[i + 2] = 0;
      rgba[i + 3] = alpha;
    }
  }
  return encodePng(size, size, rgba);
}

const icon1x = render(18).toString('base64');
const icon2x = render(36).toString('base64');

module.exports = { icon1x, icon2x };

// `node trayIcon.js --preview` prints an ASCII proof of the glyph.
if (require.main === module && process.argv.includes('--preview')) {
  const size = 18;
  const rows = [];
  for (let y = 0; y < size; y++) {
    let line = '';
    for (let x = 0; x < size; x++) {
      const v = coverage(x + 0.5, y + 0.5, size);
      line += v ? '#' : '.';
    }
    rows.push(line);
  }
  console.log(rows.join('\n'));
  console.log('\n1x png bytes:', Buffer.from(icon1x, 'base64').length);
  console.log('2x png bytes:', Buffer.from(icon2x, 'base64').length);
}
