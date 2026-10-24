/* Theme + brand suite: favicon, 3D logo, light mode vars, 3D toggle. */
const fs = require('fs');
const path = require('path');
const jsdom = require('jsdom');
const { JSDOM } = jsdom;

const ROOT = path.join(__dirname, '..', '..');
const html = fs.readFileSync(path.join(ROOT, 'WebWorkstation', 'index.html'), 'utf8');

const errors = [];
const virtualConsole = new jsdom.VirtualConsole();
virtualConsole.on('jsdomError', (e) => errors.push('jsdomError: ' + e.message));

function boot(themePref) {
  return new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    url: 'http://localhost:2525/',
    virtualConsole,
    beforeParse(window) {
      Object.defineProperty(window, 'matchMedia', {
        writable: true,
        value: (q) => ({ matches: q.includes('light') ? themePref === 'light' : false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} })
      });
      Object.defineProperty(window, 'localStorage', {
        writable: true,
        value: (() => {
          let store = {};
          return {
            getItem: (k) => (k in store ? store[k] : null),
            setItem: (k, v) => { store[k] = String(v); },
            removeItem: (k) => { delete store[k]; },
            clear: () => { store = {}; }
          };
        })()
      });
      window.fetch = () => new Promise(() => {});
      window.WebSocket = function () { return { addEventListener() {}, removeEventListener() {}, close() {}, send() {}, readyState: 0 }; };
      window.requestAnimationFrame = (cb) => setTimeout(() => cb(Date.now()), 16);
      window.cancelAnimationFrame = (id) => clearTimeout(id);
    }
  });
}

const checks = [];

checks.push(['1 favicon link          ', /<link rel="icon" type="image\/png" href="\/favicon.png"/.test(html)]);
checks.push(['2 brand logo img        ', /class="brand-logo"[\s\S]{0,200}?<img src="\/assets\/logo\.png"/.test(html)]);
checks.push(['3 light vars in css     ', /\[data-theme="light"\]\s*\{[^}]*--bg-canvas:\s*#EAEFF7/.test(html)]);
checks.push(['4 toggle faces          ', /id="ThemeToggle"[\s\S]{0,600}?theme-face-dark[\s\S]{0,200}?theme-face-light/.test(html)]);

{
  const dom = boot(null);
  checks.push(['5 bootstrap dark        ', dom.window.document.documentElement.getAttribute('data-theme') === 'dark']);
  dom.window.close();
}

{
  const dom = boot('light');
  checks.push(['6 system prefers light  ', dom.window.document.documentElement.getAttribute('data-theme') === 'light']);
  dom.window.close();
}

{
  const dom = boot(null);
  const w = dom.window;
  w.toggleTheme();
  const t1 = w.document.documentElement.getAttribute('data-theme');
  const stored = w.localStorage.getItem('ch-theme');
  checks.push(['7 toggle to light       ', t1 === 'light' && stored === 'light']);
  w.toggleTheme();
  const t2 = w.document.documentElement.getAttribute('data-theme');
  checks.push(['8 toggle back to dark   ', t2 === 'dark' && w.localStorage.getItem('ch-theme') === 'dark']);
  const meta = w.document.querySelector('meta[name="theme-color"]').getAttribute('content');
  checks.push(['9 theme-color meta      ', meta === '#0A0E16']);
  dom.window.close();
}

checks.push(['10 knob flips in 3d     ', /\[data-theme="light"\] \.theme-knob-inner \{ transform: rotateY\(180deg\); \}/.test(html) &&
  /\[data-theme="light"\] \.theme-knob \{ transform: translateX\(22px\); \}/.test(html)]);

checks.push(['11 logo on disk         ', fs.existsSync(path.join(ROOT, 'WebWorkstation', 'assets', 'logo.png')) &&
  fs.existsSync(path.join(ROOT, 'WebWorkstation', 'favicon.png'))]);

console.log(checks.map(([n, v]) => n + ': ' + v).join('\n'));
const failed = checks.filter(([, v]) => !v).length;
console.log(failed === 0 ? 'ALL PASS' : 'FAILED: ' + failed);
console.log('ERRORS: ' + (errors.length ? errors.join(' | ') : 'none'));
process.exit(failed === 0 ? 0 : 1);
