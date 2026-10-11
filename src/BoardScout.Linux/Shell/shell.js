// BoardScout for Linux: the dashboard shell. One event stream from the server feeds the shell and, through
// postMessage, the shared views in their frames (Topology, Connections, System).
'use strict';

const $ = id => document.getElementById(id);
function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}
const SVG = 'http://www.w3.org/2000/svg';
function sv(tag, attrs, parent) {
  const e = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs || {})) if (v != null) e.setAttribute(k, v);
  if (parent) parent.append(e);
  return e;
}

const state = {
  client: '', settings: null, meta: null, overview: null, telemetry: null, flow: null,
  firmware: null, firmwareBusy: false, log: [], tab: 'overview', frames: {},
  zoom: 1, panX: 0, panY: 0, selected: null, driverFilter: 'All', badge: null, reading: true
};
// The views' bridge reads the client id from here, so the server knows which dashboard a request came from.
window.BoardScoutShell = { get client() { return state.client; } };

function post(view, message) {
  return fetch('/api/msg/' + view, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-BoardScout': '1', 'X-BoardScout-Client': state.client },
    body: JSON.stringify(message),
    credentials: 'same-origin'
  }).catch(() => {});
}

// ---- Formatting ------------------------------------------------------------------------------------

function fmtBytes(n) {
  n = n || 0;
  if (n >= 1e12) return (n / 1e12).toFixed(2) + ' TB';
  if (n >= 1e9) return (n / 1e9).toFixed(n >= 1e11 ? 0 : 1) + ' GB';
  if (n >= 1e6) return Math.round(n / 1e6) + ' MB';
  return Math.round(n / 1e3) + ' KB';
}
function fmtRate(b) {
  if (!b || b < 1) return '0 B/s';
  if (b >= 1073741824) return (b / 1073741824).toFixed(1) + ' GB/s';
  if (b >= 1048576) return (b / 1048576).toFixed(b >= 104857600 ? 0 : 1) + ' MB/s';
  if (b >= 1024) return Math.round(b / 1024) + ' KB/s';
  return Math.round(b) + ' B/s';
}
function fmtBits(bytes) {
  const b = (bytes || 0) * 8;
  if (b >= 1e9) return (b / 1e9).toFixed(2) + ' Gbps';
  if (b >= 1e6) return (b / 1e6).toFixed(b >= 1e8 ? 0 : 1) + ' Mbps';
  if (b >= 1e3) return Math.round(b / 1e3) + ' Kbps';
  return Math.round(b) + ' bps';
}
function store(key, value) { try { localStorage.setItem('boardscout.' + key, value); } catch { /* storage can be off */ } }
function load(key) { try { return localStorage.getItem('boardscout.' + key); } catch { return null; } }

let toastTimer = 0;
function toast(text) {
  const t = $('toast');
  t.textContent = text;
  t.className = 'show';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.className = ''; }, 4200);
}
function setStatus(text) { $('statusText').textContent = text; }

async function copy(text, what) {
  try {
    await navigator.clipboard.writeText(text);
    toast(`${what} copied.`);
  } catch {
    // Clipboard access needs a secure context or a user gesture the browser accepts; fall back to a selection.
    const area = el('textarea');
    area.value = text;
    document.body.append(area);
    area.select();
    try { document.execCommand('copy'); toast(`${what} copied.`); } catch { toast(`Couldn't copy ${what.toLowerCase()}.`); }
    area.remove();
  }
}

// ---- Version badge (Glimmer orb) -------------------------------------------------------------------

let badgeApi = null;
function badge(stateName) {
  if (!badgeApi) return;
  clearTimeout(state.badgeTimer);
  badgeApi.setState(stateName);
  if (stateName === 'success') state.badgeTimer = setTimeout(() => badgeApi.setState('idle'), 1600);
}

// ---- Event stream ------------------------------------------------------------------------------------

function connect() {
  const events = new EventSource('/api/events');
  events.onmessage = e => {
    let envelope;
    try { envelope = JSON.parse(e.data); } catch { return; }
    route(envelope.view, envelope.msg);
  };
  events.onopen = () => setStatus(state.overview ? `Connected · read at ${state.overview.gatheredAt}` : 'Connected · reading this computer…');
  events.onerror = () => {
    setStatus('Lost the connection to BoardScout; trying again… (if it was stopped, start it again)');
    badge('error');
  };
}

function route(view, message) {
  if (!message) return;
  if (view === 'shell') { handle(message); return; }
  if (view === 'connections') {
    if (message.type === 'busy') { state.reading = true; badge('thinking'); setStatus('Reading devices and links…'); }
    else if (message.type === 'discovery') setStatus('Looking up router, network, and device names…');
    else if (message.type === 'connections' && message.data?.discovery === 'done') setStatus(`Read at ${message.data.gatheredAt} · network names found`);
  }
  const frame = state.frames[view];
  if (frame?.contentWindow) frame.contentWindow.postMessage({ boardscout: view, msg: message }, location.origin);
}

function handle(message) {
  switch (message.type) {
    case 'hello':
      state.client = message.client;
      post('shell', { type: 'ready' });
      post('shell', { type: 'tab', name: state.tab });
      post('shell', { type: 'visibility', hidden: document.hidden });
      break;
    case 'settings': applySettings(message); break;
    case 'meta': state.meta = message.data; renderMeta(); break;
    case 'overview':
      state.overview = message.data;
      renderOverview();
      if (state.reading) { state.reading = false; badge('success'); }
      setStatus(`Read at ${message.data.gatheredAt} in ${message.data.gatheredMs} ms`);
      break;
    case 'telemetry': state.telemetry = message.data; renderTelemetry(); break;
    case 'flow': state.flow = message.data; renderFlow(); break;
    case 'log': state.log = message.lines || []; renderLog(true); break;
    case 'logline':
      state.log.push(message.line);
      if (state.log.length > 500) state.log.shift();
      renderLog(false, message.line);
      break;
    case 'firmwarebusy': state.firmwareBusy = true; renderFirmware(); break;
    case 'firmware': state.firmwareBusy = false; state.firmware = message.data; renderFirmware(); break;
    case 'error': setStatus('Reading failed: ' + message.message); badge('error'); break;
  }
}

// ---- Tabs ----------------------------------------------------------------------------------------------

const TABS = ['overview', 'topology', 'connections', 'drivers', 'storage', 'efficiency', 'system', 'log'];

function setTab(name) {
  if (!TABS.includes(name)) name = 'overview';
  state.tab = name;
  for (const section of document.querySelectorAll('.tab')) section.classList.toggle('on', section.dataset.tab === name);
  for (const button of document.querySelectorAll('.navbtn')) {
    if (button.dataset.tab === name) button.setAttribute('aria-current', 'page');
    else button.removeAttribute('aria-current');
  }
  const frame = document.querySelector(`.tab[data-tab="${name}"] iframe`);
  if (frame && !frame.getAttribute('src')) {
    frame.src = frame.dataset.src;
    state.frames[name] = frame;
  }
  if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
  store('tab', name);
  if (state.client) post('shell', { type: 'tab', name });
  if (name === 'log') { const log = $('log'); log.scrollTop = log.scrollHeight; }
}

// ---- Settings ------------------------------------------------------------------------------------------

function applySettings(s) {
  state.settings = s;
  const root = document.documentElement.style;
  root.setProperty('--glass-a', String(s.glassOpacity));
  root.setProperty('--glass-blur', s.glassBlur + 'px');
  document.body.classList.toggle('solid', !s.glass);
  document.body.classList.toggle('no-motion', !s.motion);
  document.body.classList.toggle('privacy', !!s.privacy);
  $('sPrivacy').checked = !!s.privacy;
  $('sGlass').checked = !!s.glass;
  $('sMotion').checked = !!s.motion;
  $('sBrowser').checked = !!s.openBrowser;
  $('sOpacity').value = Math.round(s.glassOpacity * 100);
  $('sOpacityValue').textContent = Math.round(s.glassOpacity * 100) + '%';
  $('sBlur').value = s.glassBlur;
  $('sBlurValue').textContent = s.glassBlur + ' px';
  $('sRefresh').value = String(s.refreshMs);
  $('sCrawlers').value = String([0, 1, 2, 4, 8].reduce((a, b) => Math.abs(b - s.crawlers) < Math.abs(a - s.crawlers) ? b : a));
  $('sOpacity').disabled = $('sBlur').disabled = !s.glass;
}

function saveSetting(change) {
  if (state.settings) applySettings({ ...state.settings, ...change });
  post('shell', { type: 'settings', ...change });
}

let sliderTimer = 0;
function slider(id, toChange) {
  $(id).addEventListener('input', () => {
    const change = toChange(+$(id).value);
    if (state.settings) applySettings({ ...state.settings, ...change });
    clearTimeout(sliderTimer);
    sliderTimer = setTimeout(() => post('shell', { type: 'settings', ...change }), 200);
  });
}

// ---- Telemetry tiles ----------------------------------------------------------------------------------

function tile(id, value, tone, key) {
  const t = $(id);
  t.querySelector('.v').textContent = value;
  if (key) t.querySelector('.k').textContent = key;
  t.classList.remove('good', 'warn', 'bad');
  if (tone) t.classList.add(tone);
}

function renderTelemetry() {
  const t = state.telemetry;
  if (!t) return;
  tile('tMem', `${t.memoryUsedGb.toFixed(1)} / ${t.memoryTotalGb.toFixed(1)} GB`, t.memoryUsedGb / t.memoryTotalGb > .9 ? 'warn' : null);
  tile('tCpu', `${Math.round(t.cpu)}%`, t.cpu > 90 ? 'warn' : null);
  if (t.cpuTemp != null) tile('tTemp', `${Math.round(t.cpuTemp)}°C`, t.cpuTemp >= 95 ? 'bad' : t.cpuTemp >= 85 ? 'warn' : 'good', 'CPU temp');
  else if (t.gpuTemp != null) tile('tTemp', `${Math.round(t.gpuTemp)}°C`, null, 'GPU temp');
  else tile('tTemp', 'Not reported', null, 'Temperature');
  if (t.fans?.length) {
    const spinning = t.fans.filter(f => f.rpm > 0);
    tile('tFans', spinning.length ? `${spinning[0].rpm} RPM${t.fans.length > 1 ? ` +${t.fans.length - 1}` : ''}` : 'Stopped', null, t.fans.length === 1 ? 'Fan' : 'Fans');
  } else tile('tFans', 'Not reported', null, 'Fans');
  tile('tNet', `${fmtBits(t.netIn)}↓ ${fmtBits(t.netOut)}↑`, null);
  // The board's live labels.
  const cpuLive = document.getElementById('liveCpu');
  if (cpuLive) cpuLive.textContent = `${Math.round(t.cpu)}% load${t.cpuTemp != null ? ` · ${Math.round(t.cpuTemp)}°C` : ''}`;
  const memLive = document.getElementById('liveMem');
  if (memLive) memLive.textContent = `MEMORY · ${t.memoryUsedGb.toFixed(1)} / ${t.memoryTotalGb.toFixed(1)} GB live`;
  const chipLive = document.getElementById('liveChipset');
  if (chipLive && t.chipsetTemp != null) chipLive.textContent = `${Math.round(t.chipsetTemp)}°C`;
  for (const fan of t.fans || []) {
    const node = document.querySelector(`[data-live="fan:${CSS.escape(fan.name)}"] .live`);
    if (node) node.textContent = `${fan.rpm} RPM`;
  }
}

// ---- Overview --------------------------------------------------------------------------------------------

const KIND_LABEL = {
  cpu: 'Processor', dimm: 'Memory slot', memory: 'Memory', gpu: 'Graphics', nvme: 'NVMe SSD', disk: 'SATA drive',
  sata: 'SATA controller', 'usb-controller': 'USB controller', ethernet: 'Ethernet', wifi: 'Wi-Fi', audio: 'Audio',
  bluetooth: 'Bluetooth', chipset: 'Chipset', monitor: 'Display outputs', fan: 'Fan', slot: 'PCIe lanes', device: 'Device', optical: 'Optical drive'
};
const KIND_COLOR = {
  cpu: '#3d9eff', dimm: '#a3e635', memory: '#a3e635', gpu: '#a78bfa', nvme: '#10b981', disk: '#fb923c', optical: '#fb923c',
  sata: '#fb923c', 'usb-controller': '#2dd4bf', ethernet: '#10b981', wifi: '#34d399', audio: '#f472b6', bluetooth: '#818cf8',
  chipset: '#a78bfa', monitor: '#f472b6', fan: '#2dd4bf', slot: '#4a6080', device: '#8a9bb2'
};

function renderOverview() {
  const o = state.overview;
  $('title').textContent = o.title;
  document.title = `BoardScout · ${o.title}`;
  $('subtitle').textContent = o.subtitle;
  const used = o.dataUsedTb >= 1 ? `${o.dataUsedTb.toFixed(1)} TB` : `${Math.round(o.dataUsedTb * 1000)} GB`;
  tile('tData', used, null);
  const fixes = o.suggestions.filter(s => s.severity !== 'info').length;
  tile('tUpdates', fixes ? String(fixes) : 'None', fixes ? 'warn' : 'good', fixes === 1 ? 'Fix' : 'Fixes');
  renderBoard();
  renderDetails();
  renderDrivers();
  renderStorage();
  renderSuggestions();
  if (state.selected) {
    const part = o.parts.find(p => p.id === state.selected);
    if (part) inspect(part, true);
  }
}

function renderDetails() {
  const table = $('details');
  table.replaceChildren();
  for (const f of state.overview.details) {
    const tr = el('tr');
    tr.append(el('td', null, f.label), el('td', null, f.value));
    table.append(tr);
  }
}

// Lays the parts out like a board: rear I/O on the left, the CPU with its memory, PCIe lanes and M.2 below,
// the chipset and SATA ports on the right. Positions are schematic; only what Linux reports is drawn.
function renderBoard() {
  const o = state.overview;
  const svg = $('board');
  svg.replaceChildren();
  const parts = o.parts;
  const zone = z => parts.filter(p => p.zone === z);
  const pos = new Map();

  let ioY = 96;
  for (const p of zone('io')) { pos.set(p.id, { x: 44, y: ioY, w: 240, h: 58 }); ioY += 68; }
  const cpu = parts.find(p => p.kind === 'cpu');
  if (cpu) pos.set(cpu.id, { x: 330, y: 96, w: 300, h: 176 });
  let cy = 284;
  for (const p of zone('cpu').filter(p => p !== cpu)) { pos.set(p.id, { x: 330, y: cy, w: 300, h: 50 }); cy += 60; }
  const dimms = zone('memory');
  const dw = dimms.length > 6 ? 22 : dimms.length > 4 ? 28 : 36;
  dimms.forEach((p, i) => pos.set(p.id, { x: 672 + i * (dw + 12), y: 104, w: dw, h: 214 }));
  const memRight = 672 + Math.max(1, dimms.length) * (dw + 12);
  let fy = 96;
  const fanX = Math.max(memRight + 24, 900);
  for (const p of zone('fans')) { pos.set(p.id, { x: fanX, y: fy, w: 1090 - fanX, h: 34 }); fy += 42; }
  let sy = Math.max(cy, 344) + 18;
  for (const p of [...zone('m2'), ...zone('slots')]) {
    const lanes = p.width || 4;
    const w = lanes >= 16 ? 500 : lanes >= 8 ? 420 : lanes >= 4 ? 340 : 250;
    pos.set(p.id, { x: 330, y: sy, w, h: 48 });
    sy += 58;
  }
  const chip = zone('chipset')[0];
  const chipY = Math.max(352, fy + 10);
  if (chip) pos.set(chip.id, { x: 880, y: chipY, w: 210, h: 112 });
  let sataY = chip ? chipY + 136 : chipY;
  for (const p of zone('sata')) { pos.set(p.id, { x: 880, y: sataY, w: 210, h: 48 }); sataY += 56; }
  const height = Math.max(ioY, sy, sataY, fy, 420) + 40;
  svg.setAttribute('viewBox', `0 0 1120 ${height}`);
  $('boardWrap').style.aspectRatio = `1120 / ${height}`;

  const defs = sv('defs', null, svg);
  const pcb = sv('linearGradient', { id: 'pcb', x1: 0, y1: 0, x2: 1, y2: 1 }, defs);
  sv('stop', { offset: 0, 'stop-color': '#0d1b28' }, pcb);
  sv('stop', { offset: 1, 'stop-color': '#091219' }, pcb);
  const glassFill = sv('linearGradient', { id: 'glassFill', x1: 0, y1: 0, x2: 0, y2: 1 }, defs);
  sv('stop', { offset: 0, 'stop-color': '#fff', 'stop-opacity': .1 }, glassFill);
  sv('stop', { offset: .55, 'stop-color': '#fff', 'stop-opacity': .02 }, glassFill);
  sv('stop', { offset: 1, 'stop-color': '#000', 'stop-opacity': .16 }, glassFill);

  const viewport = sv('g', { id: 'vp' }, svg);
  sv('rect', { x: 20, y: 20, width: 1080, height: height - 40, rx: 24, fill: 'url(#pcb)', stroke: 'rgba(255,255,255,.08)' }, viewport);
  sv('text', { x: 44, y: 58, fill: '#3d9eff', 'font-size': 12, 'font-weight': 700, 'letter-spacing': '.12em' }, viewport).textContent = 'BOARD LAYOUT · AS LINUX REPORTS IT';
  sv('text', { x: 1076, y: 58, fill: '#c9d6e6', 'font-size': 14, 'font-weight': 600, 'text-anchor': 'end' }, viewport).textContent = `${o.title} · ${o.formFactor}`;
  if (dimms.length) {
    const mem = sv('text', { x: 672, y: 92, fill: '#bef264', 'font-size': 12, 'font-weight': 600, id: 'liveMem' }, viewport);
    mem.textContent = 'MEMORY';
  }
  if (zone('io').length) sv('text', { x: 44, y: 86, fill: '#5a7090', 'font-size': 11, 'font-weight': 700, 'letter-spacing': '.12em' }, viewport).textContent = 'I/O AND ONBOARD';
  if (zone('sata').length) sv('text', { x: 1090, y: sataY - zone('sata').length * 56 - 8, fill: '#fdba74', 'font-size': 11, 'font-weight': 700, 'letter-spacing': '.1em', 'text-anchor': 'end' }, viewport).textContent = 'SATA PORTS';

  // Traces: the CPU's memory bus, its lanes, the chipset link, and the chipset's own connections.
  const traces = sv('g', null, viewport);
  const motion = state.settings?.motion !== false;
  const trace = (d, color, flow) => {
    sv('path', { d, class: 'trace', stroke: color }, traces);
    if (flow && motion) sv('path', { d, class: 'trace flow', stroke: color }, traces);
  };
  const cpuBox = cpu && pos.get(cpu.id);
  if (cpuBox && dimms.length) trace(`M${cpuBox.x + cpuBox.w},${cpuBox.y + 60} H${memRight - 6}`, '#a3e635', true);
  const chipBox = chip && pos.get(chip.id);
  if (cpuBox && chipBox) trace(`M${cpuBox.x + cpuBox.w},${cpuBox.y + cpuBox.h - 30} H${chipBox.x + chipBox.w / 2} V${chipBox.y}`, '#a78bfa', true);
  for (const p of [...zone('m2'), ...zone('slots')]) {
    const r = pos.get(p.id);
    const cpuSide = /· CPU$/.test(p.detail || '');
    if (cpuSide && cpuBox) trace(`M${cpuBox.x + 18},${cpuBox.y + cpuBox.h} V${r.y + r.h / 2} H${r.x + 6}`, p.empty ? '#4a6080' : '#3d9eff', !p.empty);
    else if (chipBox) trace(`M${chipBox.x},${chipBox.y + chipBox.h - 20} H${r.x + r.w + 18} V${r.y + r.h / 2} H${r.x + r.w - 4}`, p.empty ? '#4a6080' : '#a78bfa', false);
  }
  if (chipBox) for (const p of zone('sata')) {
    const r = pos.get(p.id);
    trace(`M${chipBox.x + 14},${chipBox.y + chipBox.h} V${r.y + r.h / 2} H${r.x + 4}`, '#fb923c', false);
  }
  if (zone('io').length && (chipBox || cpuBox)) {
    const bus = 300;
    const io = zone('io').map(p => pos.get(p.id));
    for (const r of io) trace(`M${r.x + r.w - 4},${r.y + r.h / 2} H${bus}`, '#2dd4bf', false);
    const target = chipBox ? `V${height - 34} H${chipBox.x - 24} V${chipBox.y + 60} H${chipBox.x + 4}` : `V${cpuBox.y + 30} H${cpuBox.x + 4}`;
    trace(`M${bus},${io[0].y + io[0].h / 2} V${io[io.length - 1].y + io[io.length - 1].h / 2} ${target}`, '#2dd4bf', true);
  }

  const layer = sv('g', null, viewport);
  for (const p of parts) {
    const r = pos.get(p.id);
    if (r) drawPart(layer, p, r);
  }
  applyTransform();
}

function drawPart(layer, p, r) {
  const color = p.tone === 'warn' ? '#f59e0b' : KIND_COLOR[p.kind] || '#3d9eff';
  const g = sv('g', {
    class: 'part' + (p.empty ? ' empty' : '') + (state.selected === p.id ? ' sel' : ''),
    tabindex: 0, role: 'button', 'aria-label': `${KIND_LABEL[p.kind] || p.kind}: ${p.name}. ${p.detail || ''}`,
    transform: `translate(${r.x},${r.y})`, 'data-live': p.live, 'data-id': p.id
  }, layer);
  const face = sv('g', { class: 'face' }, g);
  const radius = p.kind === 'cpu' ? 18 : p.kind === 'dimm' ? 7 : 12;
  sv('rect', { width: r.w, height: r.h, rx: radius, fill: '#0c1620', 'fill-opacity': p.empty ? .5 : 1 }, face);
  sv('rect', { class: 'edge', width: r.w, height: r.h, rx: radius, fill: 'url(#glassFill)', stroke: p.empty ? '#4a6080' : color, 'stroke-width': p.kind === 'cpu' ? 2 : 1.5, 'stroke-opacity': .9 }, face);
  if (p.kind === 'dimm') {
    const label = sv('text', { x: r.w / 2, y: r.h / 2, fill: p.empty ? '#8a9bb2' : '#ecfccb', 'font-size': 12.5, 'font-weight': 600, 'text-anchor': 'middle', transform: `rotate(-90 ${r.w / 2} ${r.h / 2})` }, g);
    label.textContent = p.name;
    sv('text', { x: r.w / 2, y: r.h + 16, fill: '#8a9bb2', 'font-size': 10.5, 'text-anchor': 'middle' }, g).textContent = (p.detail || '').slice(0, 8);
  } else {
    const big = p.kind === 'cpu' || p.kind === 'chipset';
    const name = sv('text', { x: big ? r.w / 2 : 14, y: big ? r.h / 2 - 10 : 22, fill: '#e8edf4', 'font-size': big ? 17 : 13, 'font-weight': 600, 'text-anchor': big ? 'middle' : 'start' }, g);
    name.textContent = clip(p.name, big ? r.w - 30 : r.w - 24, big ? 9 : 7.4);
    const detail = sv('text', { x: big ? r.w / 2 : 14, y: big ? r.h / 2 + 14 : 40, fill: '#8a9bb2', 'font-size': big ? 12.5 : 11, 'text-anchor': big ? 'middle' : 'start', class: 'detail' }, g);
    detail.textContent = clip(p.detail || '', big ? r.w - 30 : r.w - 24, 6.3);
    if (p.kind === 'cpu') {
      const live = sv('text', { x: r.w / 2, y: r.h - 22, fill: '#7cc4ff', 'font-size': 12.5, 'text-anchor': 'middle', id: 'liveCpu' }, g);
      live.textContent = '';
      sv('rect', { x: 20, y: r.h - 12, width: r.w - 40, height: 4, rx: 2, fill: 'rgba(255,255,255,.08)' }, g);
      sv('rect', { x: 20, y: r.h - 12, width: 0, height: 4, rx: 2, fill: '#3d9eff', id: 'cpuBar' }, g);
    } else if (p.kind === 'chipset') {
      sv('text', { x: r.w / 2, y: r.h - 14, fill: '#c4b5fd', 'font-size': 12, 'text-anchor': 'middle', id: 'liveChipset' }, g);
    } else if (p.live && (p.live.startsWith('disk:') || p.live.startsWith('net:') || p.live.startsWith('fan:'))) {
      sv('text', { x: r.w - 12, y: 22, fill: '#2dd4bf', 'font-size': 11, 'text-anchor': 'end', class: 'live' }, g);
    }
  }
  g.addEventListener('mouseenter', () => inspect(p));
  g.addEventListener('focus', () => inspect(p));
  g.addEventListener('click', e => { e.stopPropagation(); select(p); });
  g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(p); } });
}

function clip(text, width, charWidth) {
  const max = Math.max(4, Math.floor(width / charWidth));
  return text.length > max ? text.slice(0, max - 1).trimEnd() + '…' : text;
}

function select(p) {
  state.selected = p.id;
  for (const g of document.querySelectorAll('#board .part')) g.classList.toggle('sel', g.dataset.id === p.id);
  inspect(p, true);
}

function inspect(p, pinned) {
  if (state.selected && !pinned && state.selected !== p.id) return;
  const box = $('inspect');
  box.replaceChildren();
  const kind = el('div', 'eyebrow', KIND_LABEL[p.kind] || 'Part');
  kind.style.color = KIND_COLOR[p.kind] || '';
  box.append(kind, el('h3', null, p.name));
  const chipText = p.tone === 'warn' ? 'Needs attention' : p.empty ? 'Open' : p.tone === 'good' ? 'Working' : 'Reported';
  box.append(el('span', 'status-chip ' + (p.tone === 'warn' ? 'warn' : p.empty ? 'open' : 'good'), chipText));
  if (p.detail) box.append(el('p', 'muted', p.detail));
  if (p.facts?.length) {
    const table = el('table', 'facts');
    for (const f of p.facts) {
      const tr = el('tr');
      tr.append(el('td', null, f.label), el('td', null, f.value));
      table.append(tr);
    }
    box.append(table);
  }
  if (p.node) {
    const show = el('button', 'btn small', 'Show on the Connections map');
    show.style.marginTop = '12px';
    show.onclick = () => showOnMap(p.node);
    box.append(show);
  }
  if (state.selected) {
    const clear = el('button', 'btn small', 'Unpin');
    clear.style.margin = '12px 0 0 8px';
    clear.onclick = () => { state.selected = null; for (const g of document.querySelectorAll('#board .part.sel')) g.classList.remove('sel'); };
    box.append(clear);
  }
}

// Opens Connections and highlights the card, once the map has drawn it.
function showOnMap(id) {
  setTab('connections');
  let tries = 30;
  const attempt = () => {
    try {
      const w = state.frames.connections?.contentWindow;
      if (w?.document.getElementById('c-' + id) && typeof w.showNodes === 'function') { w.showNodes([id]); return; }
    } catch { /* the frame is still loading */ }
    if (--tries > 0) setTimeout(attempt, 300);
  };
  attempt();
}

function renderFlow() {
  const flow = state.flow;
  if (!flow) return;
  for (const g of document.querySelectorAll('#board .part[data-live]')) {
    const live = g.dataset.live;
    const label = g.querySelector('.live');
    if (live.startsWith('disk:')) {
      const d = flow.disk?.[live.slice(5)];
      const rate = d ? d[0] + d[1] : 0;
      g.classList.toggle('busy', rate > 1024);
      if (label) label.textContent = rate > 1024 ? `R ${fmtRate(d[0])} · W ${fmtRate(d[1])}` : '';
    } else if (live.startsWith('net:')) {
      const n = flow.net?.[live.slice(4)];
      const rate = n ? n[0] + n[1] : 0;
      g.classList.toggle('busy', rate > 2048);
      if (label) label.textContent = rate > 2048 ? `↓${fmtBits(n[0])} ↑${fmtBits(n[1])}` : '';
    }
  }
  const bar = document.getElementById('cpuBar');
  const cpuPart = state.overview?.parts.find(p => p.kind === 'cpu');
  if (bar && cpuPart) bar.setAttribute('width', String(Math.max(0, Math.min(1, flow.cpu / 100)) * (300 - 40)));
}

// ---- Zoom and pan ----------------------------------------------------------------------------------------

function applyTransform() {
  const vp = document.getElementById('vp');
  if (vp) vp.setAttribute('transform', `translate(${state.panX},${state.panY}) scale(${state.zoom})`);
  $('zoomReset').textContent = Math.round(state.zoom * 100) + '%';
}
function zoomBy(factor, cx = 560, cy = 300) {
  const next = Math.max(.5, Math.min(3, state.zoom * factor));
  state.panX = cx - (cx - state.panX) * (next / state.zoom);
  state.panY = cy - (cy - state.panY) * (next / state.zoom);
  state.zoom = next;
  applyTransform();
}
function setupBoardControls() {
  const wrap = $('boardWrap');
  const svg = $('board');
  const toBoard = (clientX, clientY) => {
    const point = svg.createSVGPoint();
    point.x = clientX;
    point.y = clientY;
    const matrix = svg.getScreenCTM();
    return matrix ? point.matrixTransform(matrix.inverse()) : { x: 560, y: 300 };
  };
  wrap.addEventListener('wheel', e => {
    e.preventDefault();
    const p = toBoard(e.clientX, e.clientY);
    zoomBy(e.deltaY < 0 ? 1.12 : 1 / 1.12, p.x, p.y);
  }, { passive: false });
  let drag = null;
  wrap.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    drag = { x: e.clientX, y: e.clientY, panX: state.panX, panY: state.panY, moved: false };
  });
  window.addEventListener('pointermove', e => {
    if (!drag) return;
    const matrix = svg.getScreenCTM();
    const scale = matrix ? matrix.a : 1;
    const dx = (e.clientX - drag.x) / scale, dy = (e.clientY - drag.y) / scale;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    drag.moved = true;
    wrap.classList.add('dragging');
    state.panX = drag.panX + dx;
    state.panY = drag.panY + dy;
    applyTransform();
  });
  window.addEventListener('pointerup', () => { drag = null; wrap.classList.remove('dragging'); });
  $('zoomIn').onclick = () => zoomBy(1.2);
  $('zoomOut').onclick = () => zoomBy(1 / 1.2);
  $('zoomReset').onclick = () => { state.zoom = 1; state.panX = 0; state.panY = 0; applyTransform(); };
}

// ---- Drivers, storage, efficiency ------------------------------------------------------------------------

const DRIVER_GROUPS = {
  'Graphics': /Display|VGA|3D/i, 'Network': /Network|Ethernet|Wireless/i, 'Storage': /Storage|SATA|NVMe|RAID|IDE|Drive firmware/i,
  'USB': /USB/i, 'Audio': /Audio|Multimedia/i, 'Firmware': /Firmware/i
};
function driverGroup(row) {
  for (const [name, pattern] of Object.entries(DRIVER_GROUPS)) if (pattern.test(row.category)) return name;
  return 'Other';
}
function renderDrivers() {
  const rows = state.overview?.drivers || [];
  const chips = $('driverChips');
  const groups = ['All', ...Object.keys(DRIVER_GROUPS).filter(g => rows.some(r => driverGroup(r) === g)), ...(rows.some(r => driverGroup(r) === 'Other') ? ['Other'] : [])];
  chips.replaceChildren(...groups.map(g => {
    const b = el('button', 'chip', g);
    b.setAttribute('aria-pressed', String(state.driverFilter === g));
    b.onclick = () => { state.driverFilter = g; renderDrivers(); };
    return b;
  }));
  const query = $('driverSearch').value.trim().toLowerCase();
  const shown = rows.filter(r => (state.driverFilter === 'All' || driverGroup(r) === state.driverFilter) &&
    (!query || [r.device, r.category, r.driver, r.version, r.source, r.firmware, r.status].some(v => (v || '').toLowerCase().includes(query))));
  const table = $('driversTable');
  table.replaceChildren();
  const head = el('tr');
  for (const h of ['Device', 'Kind', 'Driver', 'Version and source', 'Firmware', 'Status']) head.append(el('th', null, h));
  table.append(el('thead'));
  table.firstChild.append(head);
  const body = el('tbody');
  for (const r of shown) {
    const tr = el('tr');
    const device = el('td', null, r.device);
    if (r.node) { device.style.cursor = 'pointer'; device.title = 'Show on the Connections map'; device.onclick = () => showOnMap(r.node); }
    const status = el('td', 'tone-' + (r.tone === 'warn' ? 'warn' : r.tone === 'good' ? 'good' : 'info'), r.status);
    tr.append(device, el('td', null, r.category), el('td', 'num', r.driver), el('td', null, [r.version, r.source].filter(Boolean).join(' · ')), el('td', 'num', r.firmware || ''), status);
    body.append(tr);
  }
  if (!shown.length) { const tr = el('tr'); const td = el('td', 'muted', 'Nothing matches.'); td.colSpan = 6; tr.append(td); body.append(tr); }
  table.append(body);
}

function renderFirmware() {
  const box = $('firmwareBody');
  box.replaceChildren();
  const again = el('button', 'btn', state.firmwareBusy ? 'Asking fwupd…' : state.firmware ? 'Check again' : 'Check firmware');
  again.disabled = state.firmwareBusy;
  again.onclick = () => post('shell', { type: 'firmware' });
  const f = state.firmware;
  if (f) {
    if (f.note) box.append(el('div', 'callout' + (f.updates.length ? ' warn' : ''), f.note));
    if (f.updates.length) {
      const t = el('table', 'grid');
      t.style.marginTop = '10px';
      const head = el('tr');
      for (const h of ['Device', 'Installed', 'Available', 'What it fixes']) head.append(el('th', null, h));
      t.append(head);
      for (const u of f.updates) {
        const tr = el('tr');
        const what = el('td', null, [u.summary, u.urgency ? `(${u.urgency} urgency)` : null].filter(Boolean).join(' '));
        if (u.url) { const a = el('a', null, ' Details'); a.href = u.url; a.target = '_blank'; a.rel = 'noopener noreferrer'; what.append(a); }
        tr.append(el('td', null, u.device), el('td', 'num', u.current || ''), el('td', 'num', u.latest), what);
        t.append(tr);
      }
      box.append(t);
      box.append(el('p', 'muted', 'Install them with your software center or fwupdmgr update; BoardScout never installs firmware.'));
    }
    if (f.devices.length) {
      const t = el('table', 'grid');
      t.style.marginTop = '12px';
      const head = el('tr');
      for (const h of ['Device fwupd knows', 'Maker', 'Firmware', 'Updatable']) head.append(el('th', null, h));
      t.append(head);
      for (const d of f.devices) {
        const tr = el('tr');
        tr.append(el('td', null, d.name), el('td', null, d.vendor || ''), el('td', 'num', d.version || ''), el('td', null, d.updatable ? 'Yes' : 'No'));
        t.append(tr);
      }
      box.append(t);
    }
    box.append(el('p', 'muted', `Checked at ${f.checkedAt}.`));
  }
  box.append(again);
  $('firmwareBtn').disabled = state.firmwareBusy;
}

function renderStorage() {
  const o = state.overview;
  const volumes = $('volumes');
  volumes.replaceChildren();
  for (const v of o.volumes) {
    const card = el('div', 'vol');
    card.append(el('b', null, v.mountPoint), el('small', null, [v.label, v.fileSystem, v.lvm ? 'LVM' : null, v.readOnly ? 'read-only' : null].filter(Boolean).join(' · ')));
    const bar = el('div', 'bar');
    const fill = el('i');
    fill.style.width = '0%';
    fill.style.background = v.usedPercent >= 95 ? 'var(--bad)' : v.usedPercent >= 85 ? 'var(--warn)' : 'var(--good)';
    bar.append(fill);
    card.append(bar, el('small', null, `${fmtBytes(v.freeBytes)} free of ${fmtBytes(v.sizeBytes)} · ${Math.round(v.usedPercent)}% used${v.disks ? ' · on ' + v.disks : ''}`));
    volumes.append(card);
    requestAnimationFrame(() => { fill.style.width = Math.min(100, v.usedPercent) + '%'; });
  }
  if (!o.volumes.length) volumes.append(el('div', 'muted', 'No filesystems are mounted from local drives.'));
  const table = $('disksTable');
  table.replaceChildren();
  const head = el('tr');
  for (const h of ['Drive', 'Model', 'Type', 'Bus', 'Size', 'Firmware', 'Temperature']) head.append(el('th', null, h));
  table.append(head);
  for (const d of o.disks) {
    const tr = el('tr');
    const name = el('td', 'num', '/dev/' + d.name);
    if (d.node) { name.style.cursor = 'pointer'; name.title = 'Show on the Connections map'; name.onclick = () => showOnMap(d.node); }
    tr.append(name, el('td', null, d.model), el('td', null, d.kind), el('td', null, d.bus.toUpperCase()), el('td', 'num', fmtBytes(d.sizeBytes)),
      el('td', 'num', d.firmware || ''), el('td', 'num', d.temperatureC != null ? Math.round(d.temperatureC) + '°C' : '—'));
    table.append(tr);
  }
}

function renderSuggestions() {
  const box = $('suggestions');
  box.replaceChildren();
  const icon = { warning: '!', improvement: '↑', info: 'i' };
  for (const s of state.overview.suggestions) {
    const card = el('div', 'sug ' + s.severity);
    card.append(el('span', 'ic', icon[s.severity] || 'i'), el('div', 't', s.title));
    if (s.detail) card.append(el('div', 'd', s.detail));
    if (s.action) card.append(el('div', 'a', 'Do this: ' + s.action));
    card.append(el('div', 'c', s.category));
    box.append(card);
  }
}

// ---- Log ---------------------------------------------------------------------------------------------------

function renderLog(all, line) {
  const pre = $('log');
  const atBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 40;
  if (all) pre.textContent = state.log.join('\n');
  else pre.textContent += (pre.textContent ? '\n' : '') + line;
  if (atBottom) pre.scrollTop = pre.scrollHeight;
}

// ---- Version pop-out -------------------------------------------------------------------------------------

function renderMeta() {
  const m = state.meta;
  $('versionText').textContent = 'v' + m.version;
  $('popVersion').textContent = `v${m.version}${m.commit ? ` (${m.commit})` : ''} · .NET ${m.runtime} · ${m.distro}`;
  const deps = $('depsTable');
  deps.replaceChildren();
  const row = (name, value, isCode) => {
    const tr = el('tr');
    const td = el('td');
    if (isCode) td.append(el('code', null, value)); else td.textContent = value;
    tr.append(el('td', null, name), td);
    deps.append(tr);
  };
  row('Credentials', 'None. BoardScout uses no accounts, passwords, API keys, or tokens; the dashboard\'s access key is made fresh each time it starts and kept only in the data folder.');
  row('Linux interfaces', '/sys/bus/pci, /sys/bus/usb, /sys/block, /sys/class/net, /sys/class/hwmon, /sys/class/drm, /sys/class/dmi/id, /proc/interrupts, /proc/diskstats, /proc/net/*, /run/udev/data/+dmi:id, /etc/os-release', true);
  const what = {
    iw: 'Wi-Fi link details and the router\'s model from its beacons', nmcli: 'Wi-Fi details and connection names (NetworkManager)',
    bluetoothctl: 'Paired Bluetooth devices (BlueZ)', udisksctl: 'Eject for USB drives (udisks2)', fwupdmgr: 'Firmware versions and updates (fwupd, LVFS)',
    systemctl: 'Timers, services, and default target', 'apt-get': 'Updates waiting (simulated, read-only)', 'dpkg-query': 'Installed packages (Debian, Ubuntu)',
    rpm: 'Installed packages (Fedora, RHEL)', snap: 'Installed snaps', flatpak: 'Installed Flatpak apps', dotnet: '.NET runtimes and SDKs on this computer',
    udevadm: 'Memory slots, on systems where udev\'s cache isn\'t readable'
  };
  for (const t of m.tools) row(t.name, `${t.path ? t.path : 'not installed'} — ${what[t.name] || ''}`);
  row('Bundled data', 'IEEE OUI registry (oui.tsv), PCI ID Repository (pci.ids), USB ID Repository (usb.ids), inside the app');
  row('Bundled libraries', 'D3 7, QuickLiquid, Skitter, FlashStream, Glimmer badge: all served from the app, never a CDN');
  row('Runtime', `.NET ${m.runtime}, built into the program (no .NET install needed)`);
  row('Data folder', m.dataFolder, true);
  row('Environment', 'BOARDSCOUT_DATA (data folder), XDG_DATA_HOME, DISPLAY / WAYLAND_DISPLAY (opening the browser)', true);
  row('Dashboard address', m.url, true);
  row('External hosts', 'Only when you click: 1.1.1.1/cdn-cgi/trace (public IP) and speed.cloudflare.com (speed test). fwupd talks to the LVFS on its own schedule.');
  const versions = $('versionsTable');
  versions.replaceChildren();
  for (const [k, v] of [['BoardScout', `${m.version}${m.commit ? ` (${m.commit})` : ''}`], ['.NET runtime', m.runtime], ['Distribution', m.distro], ['Kernel', m.kernel],
    ['Running as', m.root ? 'root' : m.user], ['Dashboard', m.url], ['Data folder', m.dataFolder]]) {
    const tr = el('tr');
    tr.append(el('td', null, k), el('td', null, v));
    versions.append(tr);
  }
}

let lastFocus = null;
function openPop() {
  lastFocus = document.activeElement;
  $('popout').classList.add('open');
  $('scrim').classList.add('open');
  $('versionBtn').setAttribute('aria-expanded', 'true');
  $('popClose').focus();
}
function closePop() {
  $('popout').classList.remove('open');
  $('scrim').classList.remove('open');
  $('versionBtn').setAttribute('aria-expanded', 'false');
  (lastFocus || $('versionBtn')).focus();
}
function popPane(name) {
  for (const b of document.querySelectorAll('.pop-tabs button')) b.setAttribute('aria-selected', String(b.dataset.pane === name));
  for (const p of document.querySelectorAll('.pop-pane')) p.classList.toggle('on', p.dataset.pane === name);
}

async function diagnostics() {
  try {
    const response = await fetch('/api/diagnostics', { credentials: 'same-origin' });
    copy(await response.text(), 'Diagnostics');
  } catch {
    toast('Couldn\'t read diagnostics.');
  }
}

// ---- Wiring --------------------------------------------------------------------------------------------------

function init() {
  if (window.GlimmerBadge) badgeApi = GlimmerBadge.attach($('versionBtn'), { app: 'BoardScout', preset: 'aurora', contextMenu: false });
  for (const button of document.querySelectorAll('.navbtn')) button.onclick = () => setTab(button.dataset.tab);
  setTab((location.hash || '').slice(1) || load('tab') || 'overview');
  window.addEventListener('hashchange', () => setTab(location.hash.slice(1)));

  $('scanBtn').onclick = () => { state.reading = true; badge('thinking'); setStatus('Reading every device again…'); post('shell', { type: 'rescan' }); };
  $('firmwareBtn').onclick = () => { setTab('drivers'); post('shell', { type: 'firmware' }); };
  $('firmwareBtn2').onclick = () => post('shell', { type: 'firmware' });
  const menu = $('exportMenu');
  $('exportBtn').onclick = e => {
    e.stopPropagation();
    const open = !menu.classList.contains('open');
    menu.classList.toggle('open', open);
    $('exportBtn').setAttribute('aria-expanded', String(open));
    if (open) menu.querySelector('button').focus();
  };
  document.addEventListener('click', () => { menu.classList.remove('open'); $('exportBtn').setAttribute('aria-expanded', 'false'); });
  for (const item of menu.querySelectorAll('button')) {
    item.onclick = () => {
      const what = item.dataset.export;
      if (what === 'diagnostics') diagnostics();
      else location.href = `/api/export/${what}.json`;
    };
  }

  $('versionBtn').onclick = openPop;
  $('popClose').onclick = closePop;
  $('scrim').onclick = closePop;
  for (const b of document.querySelectorAll('.pop-tabs button')) b.onclick = () => popPane(b.dataset.pane);
  $('privacyChip').onclick = () => saveSetting({ privacy: false });
  $('sPrivacy').onchange = () => saveSetting({ privacy: $('sPrivacy').checked });
  $('sGlass').onchange = () => saveSetting({ glass: $('sGlass').checked });
  $('sMotion').onchange = () => saveSetting({ motion: $('sMotion').checked });
  $('sBrowser').onchange = () => saveSetting({ openBrowser: $('sBrowser').checked });
  $('sRefresh').onchange = () => saveSetting({ refreshMs: +$('sRefresh').value });
  $('sCrawlers').onchange = () => saveSetting({ crawlers: +$('sCrawlers').value });
  slider('sOpacity', v => ({ glassOpacity: v / 100 }));
  slider('sBlur', v => ({ glassBlur: v }));
  $('sResetGlass').onclick = () => post('shell', { type: 'settings', resetGlass: true });
  $('diagCopy').onclick = diagnostics;
  $('copyDiag').onclick = diagnostics;
  $('copyLog').onclick = () => copy(state.log.join('\n'), 'Log');
  $('diagLog').onclick = () => { closePop(); setTab('log'); };
  $('diagStop').onclick = () => {
    post('shell', { type: 'quit' });
    setStatus('BoardScout stopped. Start it again from the app menu or with boardscout.');
    closePop();
  };
  $('dataFolderBtn').onclick = () => state.meta && copy(state.meta.dataFolder, 'Data folder path');
  $('driverSearch').addEventListener('input', renderDrivers);
  setupBoardControls();
  $('boardWrap').addEventListener('click', e => {
    if (e.target.closest('.part')) return;
    state.selected = null;
    for (const g of document.querySelectorAll('#board .part.sel')) g.classList.remove('sel');
  });

  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && $('popout').classList.contains('open')) { closePop(); return; }
    if (e.ctrlKey && e.shiftKey && (e.key === 'P' || e.key === 'p')) {
      e.preventDefault();
      saveSetting({ privacy: !state.settings?.privacy });
    }
  });
  document.addEventListener('visibilitychange', () => post('shell', { type: 'visibility', hidden: document.hidden }));
  renderFirmware();
  connect();
}

init();
