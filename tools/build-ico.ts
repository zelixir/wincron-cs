// 打包 tools/png/icon-<size>.png → assets/app.ico（手写 ICO 容器，PNG 裸块条目，SPEC §11）
// 运行：bun tools/build-ico.ts
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const toolDir = import.meta.dir;
const rootDir = path.resolve(toolDir, '..');
const SIZES = [256, 64, 48, 32, 16];

function pngEntry(size) {
  const png = readFileSync(path.join(toolDir, 'png', `icon-${size}.png`));
  if (png.length < 24 || png.readUInt32BE(0) !== 0x89504e47) {
    throw new Error(`icon-${size}.png is not a PNG`);
  }
  const w = png.readUInt32BE(16); // IHDR width
  const h = png.readUInt32BE(20); // IHDR height
  if (w !== size || h !== size) {
    throw new Error(`icon-${size}.png IHDR ${w}x${h} != ${size}x${size}`);
  }
  return { size, png };
}

const entries = SIZES.map(pngEntry);

// ICONDIR + ICRENTRY ×N + PNG 裸块
const headerSize = 6 + 16 * entries.length;
let offset = headerSize;
const dir = Buffer.alloc(headerSize);
dir.writeUInt16LE(0, 0); // reserved
dir.writeUInt16LE(1, 2); // type = icon
dir.writeUInt16LE(entries.length, 4);

entries.forEach((entry, i) => {
  const o = 6 + i * 16;
  dir.writeUInt8(entry.size >= 256 ? 0 : entry.size, o); // width（256 记 0）
  dir.writeUInt8(entry.size >= 256 ? 0 : entry.size, o + 1); // height（256 记 0）
  dir.writeUInt8(0, o + 2); // 调色板色数（PNG 条目不用）
  dir.writeUInt8(0, o + 3); // reserved
  dir.writeUInt16LE(1, o + 4); // planes
  dir.writeUInt16LE(32, o + 6); // bit count
  dir.writeUInt32LE(entry.png.length, o + 8); // bytesInRes
  dir.writeUInt32LE(offset, o + 12); // imageOffset
  offset += entry.png.length;
});

const icoPath = path.join(rootDir, 'assets', 'app.ico');
writeFileSync(icoPath, Buffer.concat([dir, ...entries.map((e) => e.png)]));

// ---- 写后自检：重新解析容器 ----
const b = readFileSync(icoPath);
const count = b.readUInt16LE(4);
if (b.readUInt16LE(2) !== 1) throw new Error('ico type != 1');
if (count !== SIZES.length) throw new Error(`entry count ${count} != ${SIZES.length}`);
console.log(`assets/app.ico: ${b.length} bytes, entries=${count}`);
for (let i = 0; i < count; i++) {
  const o = 6 + i * 16;
  const widthByte = b.readUInt8(o);
  const heightByte = b.readUInt8(o + 1);
  const len = b.readUInt32LE(o + 8);
  const off = b.readUInt32LE(o + 12);
  const size = widthByte === 0 ? 256 : widthByte;
  if ((heightByte === 0 ? 256 : heightByte) !== size) throw new Error(`entry ${i} width/height mismatch`);
  if (b.readUInt32BE(off) !== 0x89504e47) throw new Error(`entry ${i} data is not PNG`);
  const ihdrW = b.readUInt32BE(off + 16);
  const ihdrH = b.readUInt32BE(off + 20);
  if (ihdrW !== size || ihdrH !== size) throw new Error(`entry ${i} IHDR ${ihdrW}x${ihdrH} != ${size}`);
  const nextOff = i + 1 < count ? b.readUInt32LE(6 + (i + 1) * 16 + 12) : b.length;
  if (len !== nextOff - off) throw new Error(`entry ${i} bytesInRes ${len} != actual ${nextOff - off}`);
  console.log(`  entry ${i}: ${size}x${size}, bytesInRes=${len}, offset=${off}, png ${ihdrW}x${ihdrH} ok`);
}
console.log('ico build ok -> assets/app.ico');
