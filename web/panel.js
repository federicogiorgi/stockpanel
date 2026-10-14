'use strict';

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const CUR = { EUR: '€', USD: '$', GBP: '£', JPY: '¥', CHF: 'CHF ' };
const MINUS = '−';

const $ = (sel, root = document) => root.querySelector(sel);
const isNum = v => typeof v === 'number' && Number.isFinite(v);
const pad2 = n => String(n).padStart(2, '0');
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

let last = null;          // latest snapshot, redrawn on resize
let tileKey = null;       // names of the tiles currently built
let sentRendered = false;

/* ---- formatting: two decimals for percentages, whole units of currency ---- */
function sign(v) { return v > 0 ? '+' : v < 0 ? MINUS : ''; }
function pct(v) {
  if (!isNum(v)) return '–';
  const a = Math.abs(v).toFixed(2);
  return (a === '0.00' ? '' : sign(v)) + a + '%';
}
function money(v, cur) {
  if (!isNum(v)) return '–';
  const s = CUR[cur] ?? cur + ' ';
  const a = Math.round(Math.abs(v));
  return (a === 0 ? '' : sign(v)) + s + a.toLocaleString('en-US');
}
// direction of a value as shown (a change that rounds to 0.00 counts as unchanged)
function dir(v, digits = 2) {
  if (!isNum(v) || Math.abs(v) < 0.5 * Math.pow(10, -digits)) return 0;
  return v > 0 ? 1 : -1;
}
const tone = d => (d > 0 ? 'pos' : d < 0 ? 'neg' : 'zero');
const tint = d => (d > 0 ? 'up' : d < 0 ? 'down' : 'flat');

function setText(el, text) { if (el.textContent !== text) el.textContent = text; }
function setClass(el, base, extra) {
  const c = (base + ' ' + extra).trim();
  if (el.className.baseVal !== undefined) el.setAttribute('class', c);
  else if (el.className !== c) el.className = c;
}

/* ---- fit the 400 x 1280 panel into the window (windowed mode) ---- */
function fit() {
  const z = Math.min(innerWidth / 400, innerHeight / 1280);
  document.body.style.zoom = z > 0 ? String(z) : '1';
}
addEventListener('resize', () => { fit(); if (last) render(last); });
fit();

/* ---- clock: ticks on its own, independent of the data ---- */
function renderClock() {
  const t = new Date();
  setText($('#clock'), `${pad2(t.getHours())}:${pad2(t.getMinutes())}`);
  setText($('#date'), `${DAYS[t.getDay()]} ${pad2(t.getDate())}-${MONTHS[t.getMonth()]}-${String(t.getFullYear()).slice(2)}`);
}
renderClock();
setInterval(renderClock, 1000);

/* ---- weather icons (WMO codes from Open-Meteo) ---- */
const SUN = '<circle cx="16" cy="16" r="6" fill="#ffc940"/>' +
  [0, 45, 90, 135, 180, 225, 270, 315].map(a => {
    const r = a * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);
    return `<line x1="${16 + 9 * c}" y1="${16 + 9 * s}" x2="${16 + 13 * c}" y2="${16 + 13 * s}" stroke="#ffc940" stroke-width="2.2" stroke-linecap="round"/>`;
  }).join('');
const MOON = '<path d="M20 5a11 11 0 1 0 8 17A9 9 0 0 1 20 5z" fill="#e9e4c9"/>';
const cloud = (dy = 0, fill = '#c9ced6') =>
  `<path transform="translate(0 ${dy})" d="M9 26h15a6 6 0 0 0 .6-11.97A8 8 0 0 0 9.3 16.1 5 5 0 0 0 9 26z" fill="${fill}"/>`;
const small = (svg, s = .62, x = -1, y = -2) => `<g transform="translate(${x} ${y}) scale(${s})">${svg}</g>`;
const drops = (color, n = 3) => Array.from({ length: n }, (_, i) =>
  `<line x1="${11 + i * 5}" y1="25" x2="${9.5 + i * 5}" y2="30" stroke="${color}" stroke-width="2" stroke-linecap="round"/>`).join('');

function weatherIcon(code, isDay) {
  const sky = isDay ? SUN : MOON;
  if (code === 0) return sky;
  if (code === 1 || code === 2) return small(sky) + cloud(2);
  if (code === 3) return cloud(-1);
  if (code === 45 || code === 48) return [10, 15, 20, 25].map((y, i) =>
    `<line x1="${5 + (i % 2) * 3}" y1="${y}" x2="${27 - (i % 2) * 3}" y2="${y}" stroke="#aab0b8" stroke-width="2.2" stroke-linecap="round"/>`).join('');
  if (code >= 51 && code <= 57) return cloud(-5) + drops('#6db7ff', 2);
  if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return cloud(-5) + drops('#4ea6ff');
  if ((code >= 71 && code <= 77) || code === 85 || code === 86)
    return cloud(-5) + [11, 16, 21].map(x => `<circle cx="${x}" cy="27.5" r="1.6" fill="#fff"/>`).join('');
  if (code >= 95) return cloud(-5, '#9ba1aa') + '<path d="M17 20l-4 6h3l-2 5 6-7h-3l2-4z" fill="#ffd23f"/>';
  return cloud(0);
}

function renderWeather(w) {
  if (!w) return;
  $('#wxIcon').innerHTML = weatherIcon(w.code, w.isDay);
  setText($('#wxTemp'), isNum(w.temp) ? Math.round(w.temp) + '°' : '–');
  setText($('#wxPlace'), w.place || '');
  setText($('#wxRange'), isNum(w.min) && isNum(w.max) ? `${Math.round(w.min)}° / ${Math.round(w.max)}°` : '');
}

/* ---- charts ----
   A price line over its time span, a dashed reference level (previous close or purchase price), and the
   area between them filled green above the reference and red below. byIndex spaces points evenly
   (daily closes: no weekend gaps); otherwise x follows the time from x0 to x1 (the trading session). */
let clipSeq = 0;
function chart(svg, s, byIndex) {
  const w = svg.clientWidth, h = svg.clientHeight;
  if (!s || !s.v || s.v.length === 0 || w < 4 || h < 4) { svg.innerHTML = ''; return; }
  svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
  if (!svg.dataset.cid) svg.dataset.cid = String(++clipSeq);
  const id = svg.dataset.cid;

  const v = s.v, n = v.length, ref = isNum(s.ref) ? s.ref : v[0];
  let lo = Math.min(ref, ...v), hi = Math.max(ref, ...v);
  if (hi - lo < Math.abs(ref) * 1e-4) { hi += Math.abs(ref) * 0.002 || 1; lo -= Math.abs(ref) * 0.002 || 1; }
  const pad = (hi - lo) * 0.1;
  lo -= pad; hi += pad;

  let xs;
  if (byIndex || !isNum(s.x0) || !isNum(s.x1) || s.x1 <= s.x0) xs = v.map((_, i) => n === 1 ? w : i / (n - 1) * w);
  else xs = s.t.map(t => clamp((t - s.x0) / (s.x1 - s.x0), 0, 1) * w);
  const y = val => (hi - val) / (hi - lo) * (h - 2) + 1;
  const yr = y(ref);

  const pts = v.map((val, i) => `${xs[i].toFixed(1)},${y(val).toFixed(1)}`);
  const line = 'M' + pts.join('L');
  const area = `${line}L${xs[n - 1].toFixed(1)},${yr.toFixed(1)}L${xs[0].toFixed(1)},${yr.toFixed(1)}Z`;
  const d = dir(v[n - 1] - ref, 6);
  const stroke = d >= 0 ? 'var(--up)' : 'var(--down)';
  const lx = xs[n - 1], ly = y(v[n - 1]);

  svg.innerHTML =
    `<defs><clipPath id="a${id}"><rect x="0" y="-5" width="${w}" height="${Math.max(0, yr + 5)}"/></clipPath>` +
    `<clipPath id="b${id}"><rect x="0" y="${yr}" width="${w}" height="${Math.max(0, h - yr + 5)}"/></clipPath></defs>` +
    `<path d="${area}" fill="var(--up-fill)" clip-path="url(#a${id})"/>` +
    `<path d="${area}" fill="var(--down-fill)" clip-path="url(#b${id})"/>` +
    `<line x1="0" x2="${w}" y1="${yr.toFixed(1)}" y2="${yr.toFixed(1)}" stroke="var(--ref)" stroke-width="1" stroke-dasharray="2 3"/>` +
    `<path d="${line}" fill="none" stroke="${stroke}" stroke-width="1.5" stroke-linejoin="round"/>` +
    `<circle cx="${lx.toFixed(1)}" cy="${ly.toFixed(1)}" r="2" fill="${stroke}"/>`;
}

/* ---- tiles ---- */
function stockTile() {
  const el = document.createElement('div');
  el.className = 'tile flat';
  el.innerHTML =
    '<div class="r1"><span class="nm"></span><span class="dp">–</span></div>' +
    '<div class="r2"><span class="sc"></span><span class="ov ovp">–</span><span class="ov ovm">–</span></div>' +
    '<div class="ch"><svg class="sp day"></svg><svg class="sp hist"></svg></div>';
  return el;
}

function indexTile() {
  const el = document.createElement('div');
  el.className = 'tile flat';
  el.innerHTML = '<div class="r1"><span class="nm"></span><span class="dp">–</span></div><div class="ch"><svg class="sp day"></svg></div>';
  return el;
}

function paintTile(el, item, extra) {
  const d = dir(item.changePct);
  const cls = ['tile', tint(d)];
  if (item.open) cls.push('open');
  if (item.stale) cls.push('stale');
  const c = cls.join(' ');
  if (el.className !== c) el.className = c;
  el.style.setProperty('--k', clamp(Math.abs(item.changePct || 0) / 3, 0, 1).toFixed(3));
  setText($('.nm', el), item.name);
  setText($('.dp', el), pct(item.changePct));
  if (extra) extra(el, item);
  chart($('svg.day', el), item.intraday, false);
}

/* ---- fitting text: measured on a canvas, so it does not depend on when the layout runs ---- */
const measure = document.createElement('canvas').getContext('2d');
function textWidth(el, text, px) {
  const cs = getComputedStyle(el);
  measure.font = `${cs.fontWeight} ${px}px ${cs.fontFamily}`;
  return measure.measureText(text).width;
}

/** Shrinks a name until it fits next to its percentage (down to 11 px), then leaves it to the ellipsis. */
function fitName(el, max) {
  const room = el.clientWidth - 2; // margin: layout rounds widths
  if (room <= 0) return;
  let size = max;
  while (size > 11 && textWidth(el, el.textContent, size) > room) size -= 0.5;
  const v = size === max ? '' : size + 'px';
  if (el.style.fontSize !== v) el.style.fontSize = v;
}

// GICS sector names that do not fit a half-width tile
const SECTOR_SHORT = {
  'communication services': 'Communication',
  'consumer discretionary': 'Cons. Discr.',
  'consumer staples': 'Cons. Staples',
  'information technology': 'Info Technology',
};
function fitSector(el, sector) {
  let text = sector || '';
  const short = SECTOR_SHORT[text.toLowerCase()];
  if (short && el.clientWidth > 0 && textWidth(el, text, parseFloat(getComputedStyle(el).fontSize)) > el.clientWidth - 2) text = short;
  setText(el, text);
}

function buildGrids(s) {
  const key = JSON.stringify([s.indices.map(i => i.name), s.stocks.map(x => x.name)]);
  if (key === tileKey) return;
  tileKey = key;
  const idx = $('#idx'), stk = $('#stk');
  idx.replaceChildren(...s.indices.map(indexTile));
  stk.replaceChildren(...s.stocks.map(stockTile));
  idx.hidden = s.indices.length === 0;
  stk.style.setProperty('--rows', String(Math.max(1, Math.ceil(s.stocks.length / 2))));
  if (s.stocks.length === 0) stk.innerHTML = '<div class="loading">No stocks in the list</div>';
}

/** Cells left empty in the last row of the stock grid show Bitcoin, then the dollar. */
function renderExtras(extras) {
  const stk = $('#stk');
  const stocks = stk.querySelectorAll('.tile:not(.xt)').length;
  const free = stocks === 0 ? 0 : (2 - stocks % 2) % 2;
  const want = (extras || []).slice(0, free);
  let tiles = [...stk.querySelectorAll('.tile.xt')];
  while (tiles.length > want.length) tiles.pop().remove();
  while (tiles.length < want.length) { const el = stockTile(); stk.appendChild(el); tiles.push(el); }
  want.forEach((x, i) => {
    const el = tiles[i];
    paintTile(el, x);
    el.classList.add('xt');
    setText($('.sc', el), x.note || '');
    const p = $('.ovp', el);
    const sym = x.currency ? (CUR[x.currency] ?? x.currency + ' ') : '';
    setText(p, sym + x.price.toLocaleString('en-US', { minimumFractionDigits: x.decimals, maximumFractionDigits: x.decimals }));
    p.className = 'ov ovp zero';
    fitName($('.nm', el), 17);
  });
}

function hhmm(iso) {
  const t = new Date(iso);
  return Number.isNaN(t.getTime()) ? '' : `${pad2(t.getHours())}:${pad2(t.getMinutes())}`;
}

function render(s) {
  last = s;
  const cur = s.base || 'EUR';
  renderWeather(s.weather);
  buildGrids(s);

  const idxTiles = $('#idx').children;
  s.indices.forEach((item, i) => idxTiles[i] && paintTile(idxTiles[i], item));

  const tiles = $('#stk').children;
  s.stocks.forEach((item, i) => {
    const el = tiles[i];
    if (!el || !el.classList.contains('tile')) return;
    paintTile(el, item, (el, it) => {
      const o = dir(it.overallPct);
      const ovp = $('.ovp', el), ovm = $('.ovm', el);
      setText(ovp, pct(it.overallPct)); ovp.className = 'ov ovp ' + tone(o);
      setText(ovm, money(it.pl, cur)); ovm.className = 'ov ovm ' + tone(dir(it.pl, 0));
      chart($('svg.hist', el), it.history, true);
    });
  });

  // names and sectors: fit after all numbers are in place
  s.stocks.forEach((item, i) => tiles[i] && tiles[i].classList.contains('tile') && fitSector($('.sc', tiles[i]), item.sector));
  for (const el of document.querySelectorAll('#stk .nm')) fitName(el, 17);
  for (const el of document.querySelectorAll('#idx .nm')) fitName(el, 18);
  renderExtras(s.extras);

  const foot = $('#upd');
  if (s.cached) { setText(foot, `cached data from ${hhmm(s.dataTime || s.updated)}, updating…`); foot.className = 'old'; }
  else { setText(foot, s.dataTime ? `updated ${hhmm(s.dataTime)}` : 'no data yet'); foot.className = ''; }
  setText($('#fx'), s.fx ? `${s.fx.pair} ${s.fx.rate.toFixed(4)}` : '');
  setText($('#warn'), (s.warnings || []).join(' · '));

  if (!s.cached && !sentRendered) {
    sentRendered = true;
    requestAnimationFrame(() => requestAnimationFrame(() => post('rendered')));
  }
}

function post(message) {
  if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
}

addEventListener('contextmenu', e => { e.preventDefault(); post('contextmenu'); });
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', e => {
    try { render(e.data); } catch (err) { setText($('#warn'), 'render error: ' + err.message); }
  });
}
