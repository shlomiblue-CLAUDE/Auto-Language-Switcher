/**
 * Generates the extension icons and the Agent's tray icon.
 *
 * Written by hand rather than pulled from an image library because the product ships nothing it
 * cannot explain, and a PNG encoder for flat-colour art is about forty lines. It also means the
 * icons are reproducible from source: change a colour here and every size regenerates.
 *
 * The mark is aleph and A side by side - the two alphabets the product switches between. At 16px
 * two glyphs turn to mush, so that size drops to aleph alone.
 *
 * Usage: node tools/make-icons.mjs
 */

import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');

const BACKGROUND = [79, 70, 229]; // indigo
const INK = [255, 255, 255];

// 5x7 bitmaps. Row strings, '#' is ink.
// Aleph is three strokes, and drawing it as a plain X reads as a Latin N instead. The main
// diagonal runs top-right to bottom-left; a short arm descends from the top-left to meet its
// middle, and another leaves that middle for the bottom-right.
const GLYPHS = {
  aleph: ['....#', '#...#', '.#.#.', '..#..', '..##.', '.#..#', '#...#'],
  A: ['.###.', '#...#', '#...#', '#####', '#...#', '#...#', '#...#'],
};

// --- PNG encoding ---------------------------------------------------------------------------

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

const crc32 = (buffer) => {
  let c = 0xffffffff;
  for (const byte of buffer) c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

const chunk = (type, data) => {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
};

/** RGBA pixel buffer to PNG. */
function encodePng(width, height, pixels) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // truecolour with alpha
  ihdr[10] = 0; // deflate
  ihdr[11] = 0; // adaptive filtering
  ihdr[12] = 0; // no interlace

  // Each scanline is prefixed with its filter type; 0 means none, which compresses fine for flat art.
  const raw = Buffer.alloc(height * (1 + width * 4));
  for (let y = 0; y < height; y++) {
    const rowStart = y * (1 + width * 4);
    raw[rowStart] = 0;
    pixels.copy(raw, rowStart + 1, y * width * 4, (y + 1) * width * 4);
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// --- Drawing --------------------------------------------------------------------------------

function draw(size) {
  const pixels = Buffer.alloc(size * size * 4); // transparent

  const set = (x, y, [r, g, b], a = 255) => {
    if (x < 0 || y < 0 || x >= size || y >= size) return;
    const i = (y * size + x) * 4;
    pixels[i] = r;
    pixels[i + 1] = g;
    pixels[i + 2] = b;
    pixels[i + 3] = a;
  };

  // Rounded square. The corner radius scales with the icon so the shape stays recognisable.
  const radius = Math.max(2, Math.round(size * 0.22));
  const inCorner = (x, y) => {
    const cx = x < radius ? radius : x >= size - radius ? size - radius - 1 : x;
    const cy = y < radius ? radius : y >= size - radius ? size - radius - 1 : y;
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2;
  };

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (inCorner(x, y)) set(x, y, BACKGROUND);
    }
  }

  // Two glyphs at 32px and up; one below that, where two would be unreadable.
  const glyphs = size >= 32 ? ['aleph', 'A'] : ['aleph'];

  const scale = Math.max(1, Math.floor((size * (glyphs.length === 2 ? 0.34 : 0.5)) / 7));
  const glyphW = 5 * scale;
  const glyphH = 7 * scale;
  const gap = glyphs.length === 2 ? Math.max(1, Math.round(size * 0.08)) : 0;

  const totalW = glyphs.length * glyphW + (glyphs.length - 1) * gap;
  let originX = Math.round((size - totalW) / 2);
  const originY = Math.round((size - glyphH) / 2);

  for (const name of glyphs) {
    const rows = GLYPHS[name];
    for (let ry = 0; ry < rows.length; ry++) {
      for (let rx = 0; rx < rows[ry].length; rx++) {
        if (rows[ry][rx] !== '#') continue;
        for (let dy = 0; dy < scale; dy++) {
          for (let dx = 0; dx < scale; dx++) {
            set(originX + rx * scale + dx, originY + ry * scale + dy, INK);
          }
        }
      }
    }
    originX += glyphW + gap;
  }

  return encodePng(size, size, pixels);
}

// --- ICO for the tray -----------------------------------------------------------------------

/** Vista and later accept PNG data inside an ICO, so the same buffers are reused. */
function encodeIco(entries) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(entries.length, 4);

  const directory = Buffer.alloc(16 * entries.length);
  let offset = header.length + directory.length;

  entries.forEach(({ size, png }, index) => {
    const at = index * 16;
    directory[at] = size >= 256 ? 0 : size;
    directory[at + 1] = size >= 256 ? 0 : size;
    directory[at + 2] = 0; // palette
    directory[at + 3] = 0; // reserved
    directory.writeUInt16LE(1, at + 4); // colour planes
    directory.writeUInt16LE(32, at + 6); // bits per pixel
    directory.writeUInt32BE(0, at + 8);
    directory.writeUInt32LE(png.length, at + 8);
    directory.writeUInt32LE(offset, at + 12);
    offset += png.length;
  });

  return Buffer.concat([header, directory, ...entries.map((e) => e.png)]);
}

// --- Output ---------------------------------------------------------------------------------

const iconDir = join(repo, 'extension/public/icons');
mkdirSync(iconDir, { recursive: true });

const sizes = [16, 32, 48, 128];
const rendered = sizes.map((size) => ({ size, png: draw(size) }));

for (const { size, png } of rendered) {
  const path = join(iconDir, `icon${size}.png`);
  writeFileSync(path, png);
  console.log(`${path}  ${png.length} bytes`);
}

const agentDir = join(repo, 'agent/AutoLang.Agent/Resources');
mkdirSync(agentDir, { recursive: true });

const icoPath = join(agentDir, 'tray.ico');
const ico = encodeIco(rendered.filter((r) => r.size <= 48));
writeFileSync(icoPath, ico);
console.log(`${icoPath}  ${ico.length} bytes`);
