// 渲染 assets/icon.svg → tools/png/icon-<size>.png（透明背景，SPEC §11）
// 运行：node tools/render-icon.mjs（依赖 playwright npm 包，channel: 'msedge'，禁止 playwright install）
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const toolDir = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(toolDir, '..');
const svgPath = path.join(rootDir, 'assets', 'icon.svg');
const outDir = path.join(toolDir, 'png');
mkdirSync(outDir, { recursive: true });

const SIZES = [256, 64, 48, 32, 16];
const svg = readFileSync(svgPath, 'utf8');

const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 512, height: 512 }, deviceScaleFactor: 1 });

  for (const size of SIZES) {
    // SVG 有 viewBox="0 0 256 256"，设置 width/height 即可等比缩放到目标尺寸
    const sized = svg.replace('<svg ', `<svg width="${size}" height="${size}" `);
    await page.setContent(
      `<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent}</style></head>` +
      `<body><div id="wrap" style="width:${size}px;height:${size}px;line-height:0">${sized}</div></body></html>`,
      { waitUntil: 'load' },
    );
    const buf = await page.locator('#wrap').screenshot({ omitBackground: true });
    const pngPath = path.join(outDir, `icon-${size}.png`);
    writeFileSync(pngPath, buf);

    // 程序化像素自检：把 PNG 解码回画布，统计非零 alpha 像素数与去重颜色数
    const stats = await page.evaluate(async (b64) => {
      const img = new Image();
      img.src = 'data:image/png;base64,' + b64;
      await new Promise((resolve, reject) => { img.onload = resolve; img.onerror = () => reject(new Error('png decode failed')); });
      const canvas = document.createElement('canvas');
      canvas.width = img.naturalWidth;
      canvas.height = img.naturalHeight;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      let nonZeroAlpha = 0;
      const colors = new Set();
      for (let i = 0; i < data.length; i += 4) {
        if (data[i + 3] !== 0) {
          nonZeroAlpha++;
          colors.add(data[i] + ',' + data[i + 1] + ',' + data[i + 2] + ',' + data[i + 3]);
        }
      }
      return { w: canvas.width, h: canvas.height, nonZeroAlpha, distinctColors: colors.size, total: data.length / 4 };
    }, buf.toString('base64'));

    if (stats.w !== size || stats.h !== size) throw new Error(`size mismatch: want ${size}, got ${stats.w}x${stats.h}`);
    if (stats.nonZeroAlpha === 0) throw new Error(`all-transparent png: icon-${size}.png`);
    if (stats.distinctColors <= 1) throw new Error(`monochrome png: icon-${size}.png (${stats.distinctColors} color)`);
    console.log(`icon-${size}.png ok: ${stats.w}x${stats.h}, nonZeroAlpha=${stats.nonZeroAlpha}/${stats.total}, distinctColors=${stats.distinctColors}`);
  }
} finally {
  await browser.close();
}
console.log('render done -> tools/png/');
