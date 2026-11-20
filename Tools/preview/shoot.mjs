// Serves the preview page (with the game's Resources folder mounted at /res/) and screenshots it
// with headless Chromium.   node shoot.mjs <out.png> [yaw] [pitch] [dist] [dpr]
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const here = path.dirname(fileURLToPath(import.meta.url));
const resDir = path.resolve(here, '../../Assets/Sanguine/Resources');
const [out = 'preview.png', yaw = 'auto', pitch = '14', dist = '8', dpr = '1'] = process.argv.slice(2);
fs.writeFileSync(path.join(here, 'params.js'), `window.PARAMS={debugTop:${process.env.DEBUGTOP ? 'true' : 'false'},yaw:${yaw === 'auto' ? "'auto'" : +yaw},pitch:${+pitch},dist:${+dist},dpr:${+dpr}};`);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.glb': 'model/gltf-binary', '.jpg': 'image/jpeg', '.png': 'image/png' };
const server = http.createServer((req, res) => {
  const url = decodeURIComponent(req.url.split('?')[0]);
  const file = url.startsWith('/res/') ? path.join(resDir, url.slice(5)) : path.join(here, url);
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
    res.end(data);
  });
}).listen(0, '127.0.0.1', async () => {
  const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 }, deviceScaleFactor: +dpr });
  page.on('console', m => { if (!/GPU stall|deprecated|THREE\.WebGLRenderer/.test(m.text())) console.log('console:', m.text()); });
  page.on('pageerror', e => console.log('pageerror:', e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/${process.env.PAGE || 'render.html'}`);
  await page.waitForFunction(() => window.DONE === true, null, { timeout: 900000 });
  await page.screenshot({ path: out });
  await browser.close();
  server.close();
  console.log('saved', out);
});
