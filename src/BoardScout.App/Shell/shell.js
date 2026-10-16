// BoardScout's browser dashboard shell: the Linux edition, and Windows PCs reached over SSH or served with
// --serve. One event stream from the server feeds the shell and, through postMessage, the shared views in their
// frames (Overview, Topology, Connections, System, Machines).
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
  driverFilter: 'All', badge: null, reading: true, platform: '', agent: false, driverCheck: null,
  history: { cpu: [], net: [] }
};
const windows = () => state.platform === 'windows';
// The views' bridge reads the client id from here, so the server knows which dashboard a request came from;
// the Overview frame opens other tabs and the Connections map through it.
window.BoardScoutShell = {
  get client() { return state.client; },
  showOnMap: id => showOnMap(id),
  setTab: name => setTab(name)
};

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
    case 'log': state.log = message.lines || []; renderLog(true); break;
    case 'logline':
      state.log.push(message.line);
      if (state.log.length > 500) state.log.shift();
      renderLog(false, message.line);
      break;
    case 'firmwarebusy': state.firmwareBusy = true; renderFirmware(); break;
    case 'firmware': state.firmwareBusy = false; state.firmware = message.data; renderFirmware(); break;
    case 'drivercheck': state.driverCheck = message; renderDriverButton(); break;
    case 'error': setStatus('Reading failed: ' + message.message); badge('error'); break;
  }
}

// ---- Tabs ----------------------------------------------------------------------------------------------

const TABS = ['overview', 'topology', 'connections', 'drivers', 'storage', 'efficiency', 'system', 'machines', 'log'];

function setTab(name) {
  if (!TABS.includes(name) || (name === 'machines' && state.agent)) name = 'overview';
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

// Which platform this dashboard reads (Linux, or Windows), and whether it was opened over SSH by another
// BoardScout: the wording, the Drivers tab's button, and the Machines tab follow.
function applyPlatform(s) {
  const platform = s.platform === 'windows' ? 'windows' : 'linux';
  const agent = !!s.agent;
  if (platform === state.platform && agent === state.agent) return;
  state.platform = platform;
  state.agent = agent;
  document.body.classList.toggle('platform-windows', platform === 'windows');
  document.body.classList.toggle('platform-linux', platform === 'linux');
  document.body.classList.toggle('agent', agent);
  $('remoteChip').textContent = agent ? `Over SSH${s.remoteName ? ' · ' + s.remoteName : ''}` : '';
  $('navDrivers').textContent = windows() ? 'Drivers and official update pages' : 'Kernel drivers and firmware';
  $('navSystem').textContent = windows() ? 'Windows, updates, apps, tasks' : 'Distribution, packages, timers';
  $('popTitle').textContent = `BoardScout${windows() ? '' : ' for Linux'}${agent ? ' · over SSH' : ''}`;
  $('diagStop').textContent = agent ? 'Disconnect' : 'Stop BoardScout';
  renderDriverButton();
  if (agent && state.tab === 'machines') setTab('overview');
  if (state.overview) renderOverview();
}

function applySettings(s) {
  state.settings = s;
  applyPlatform(s);
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

// ---- Live gauges ---------------------------------------------------------------------------------------

function gauge(id, value, tone, key, fraction) {
  const t = $(id);
  const v = t.querySelector('.v');
  if (value instanceof Node) v.replaceChildren(value); else v.textContent = value;
  if (key) t.querySelector('.k').textContent = key;
  t.classList.remove('good', 'warn', 'bad');
  if (tone) t.classList.add(tone);
  const meter = t.querySelector('.meter i');
  if (meter && fraction != null) meter.style.width = Math.round(Math.max(0, Math.min(1, fraction)) * 100) + '%';
}

// A sparkline of the last minute or so, scaled to its own peak (with a floor so quiet links stay flat).
function spark(id, values, floor) {
  const svg = $(id).querySelector('.spark');
  if (!svg || values.length < 2) return;
  const max = Math.max(floor, ...values);
  const step = 100 / (values.length - 1);
  const points = values.map((v, i) => `${(i * step).toFixed(1)},${(25 - (v / max) * 23).toFixed(1)}`);
  svg.querySelector('.stroke').setAttribute('d', 'M' + points.join(' L'));
  svg.querySelector('.area').setAttribute('d', `M0,26 L${points.join(' L')} L100,26 Z`);
}
function remember(list, value) { list.push(value); if (list.length > 40) list.shift(); }
function withUnit(value, unit) {
  const span = el('span', null, value);
  span.append(' ', el('small', null, unit));
  return span;
}

function renderTelemetry() {
  const t = state.telemetry;
  if (!t) return;
  remember(state.history.cpu, t.cpu);
  remember(state.history.net, (t.netIn || 0) + (t.netOut || 0));
  gauge('tCpu', `${Math.round(t.cpu)}%`, t.cpu > 90 ? 'warn' : null);
  spark('tCpu', state.history.cpu, 25);
  gauge('tMem', withUnit(t.memoryUsedGb.toFixed(1), `/ ${t.memoryTotalGb.toFixed(1)} GB`), t.memoryUsedGb / t.memoryTotalGb > .9 ? 'warn' : null, null,
    t.memoryUsedGb / t.memoryTotalGb);
  const temp = t.cpuTemp ?? t.gpuTemp;
  if (temp != null) gauge('tTemp', `${Math.round(temp)}°C`, temp >= 95 ? 'bad' : temp >= 85 ? 'warn' : 'good', t.cpuTemp != null ? 'CPU temp' : 'GPU temp', temp / 100);
  else gauge('tTemp', 'Not reported', null, 'Temperature', 0);
  gauge('tNet', `↓ ${fmtBits(t.netIn)}  ↑ ${fmtBits(t.netOut)}`, null);
  spark('tNet', state.history.net, 12500);
}

// ---- Overview (the board itself is the shared view in its frame) ---------------------------------------------

function renderOverview() {
  const o = state.overview;
  $('title').textContent = o.title;
  document.title = `BoardScout · ${o.title}`;
  $('subtitle').textContent = o.subtitle;
  renderHealth();
  renderDrivers();
  renderStorage();
  renderSuggestions();
}

// One chip in the header for what the Efficiency tab found; the Overview's panel lists the items.
function renderHealth() {
  const o = state.overview;
  const items = o.suggestions.filter(s => s.severity !== 'info').length + o.parts.filter(p => p.tone === 'warn' && !o.suggestions.some(s => s.title.includes(p.name))).length;
  const chip = $('healthChip');
  chip.className = items ? 'warn' : 'good';
  chip.textContent = items ? (items === 1 ? 'One thing worth a look' : `${items} things worth a look`) : 'Healthy';
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
    (!query || [r.device, r.category, r.driver, r.version, r.source, r.firmware, r.status, r.latest].some(v => (v || '').toLowerCase().includes(query))));
  const table = $('driversTable');
  table.replaceChildren();
  const head = el('tr');
  // Windows: the driver's version and date, and what the driver check found (with the official page).
  const columns = windows() ? ['Device', 'Kind', 'Driver', 'Date and source', 'Latest', 'Status'] : ['Device', 'Kind', 'Driver', 'Version and source', 'Firmware', 'Status'];
  for (const h of columns) head.append(el('th', null, h));
  table.append(el('thead'));
  table.firstChild.append(head);
  const body = el('tbody');
  for (const r of shown) {
    const tr = el('tr');
    const device = el('td', null, r.device);
    if (r.node) { device.style.cursor = 'pointer'; device.title = 'Show on the Connections map'; device.onclick = () => showOnMap(r.node); }
    const status = el('td', 'tone-' + (r.tone === 'warn' ? 'warn' : r.tone === 'good' ? 'good' : 'info'), r.status);
    if (r.url) {
      const link = el('a', null, ' Official page');
      link.href = r.url; link.target = '_blank'; link.rel = 'noopener noreferrer';
      status.append(link);
    }
    tr.append(device, el('td', null, r.category), el('td', 'num', r.driver), el('td', null, [r.version, r.source].filter(Boolean).join(' · ')),
      el('td', 'num', (windows() ? r.latest : r.firmware) || ''), status);
    body.append(tr);
  }
  if (!shown.length) {
    const tr = el('tr');
    const td = el('td', 'muted', rows.length ? 'Nothing matches.' : windows() ? 'The inventory is still running; drivers show up when it finishes.' : 'Nothing read yet.');
    td.colSpan = 6; tr.append(td); body.append(tr);
  }
  table.append(body);
}

// Windows: the header's button checks drivers (DriverScout) instead of fwupd firmware.
function renderDriverButton() {
  const button = $('firmwareBtn');
  if (!windows()) {
    button.textContent = 'Check firmware';
    button.title = 'Ask fwupd which firmware updates are waiting (nothing is installed)';
    button.disabled = state.firmwareBusy;
    return;
  }
  const c = state.driverCheck || {};
  button.textContent = c.busy ? 'Checking drivers…' : c.scanning ? 'Taking inventory…' : 'Check drivers';
  button.title = 'Look for newer drivers on vendor, OEM, and Windows Update catalog pages (nothing is installed)' + (c.checkedAt ? `. Last checked ${c.checkedAt}.` : '');
  button.disabled = !!c.busy || !!c.scanning;
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
    const name = el('td', 'num', windows() ? d.name : '/dev/' + d.name);
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
  $('popVersion').textContent = `v${m.version}${m.commit ? ` (${m.commit})` : ''} · .NET ${m.runtime} · ${m.platform === 'windows' ? m.windows : m.distro}`;
  const deps = $('depsTable');
  deps.replaceChildren();
  const row = (name, value, isCode) => {
    const tr = el('tr');
    const td = el('td');
    if (isCode) td.append(el('code', null, value)); else td.textContent = value;
    tr.append(el('td', null, name), td);
    deps.append(tr);
  };
  row('Credentials', 'None. BoardScout uses no accounts, passwords, API keys, or tokens; the dashboard\'s access key is made fresh each time it starts and kept only in the data folder.' +
    (m.agent != null ? ' This copy was started over SSH with the key of the account it runs as; it holds no SSH keys itself.' : ' Machines signs in with your own SSH keys and never asks for or keeps a password.'));
  if (m.agent == null && m.ssh) row('SSH (Machines)', `${m.ssh}; ~/.ssh/config and known_hosts, plus BoardScout's own known_hosts in the data folder`);
  if (m.platform === 'windows') { renderWindowsMeta(m, row); return; }
  row('Linux interfaces', '/sys/bus/pci, /sys/bus/usb, /sys/block, /sys/class/net, /sys/class/hwmon, /sys/class/drm, /sys/class/dmi/id, /proc/interrupts, /proc/diskstats, /proc/net/*, /run/udev/data/+dmi:id, /etc/os-release', true);
  const what = {
    iw: 'Wi-Fi link details and the router\'s model from its beacons', nmcli: 'Wi-Fi details and connection names (NetworkManager)',
    bluetoothctl: 'Paired Bluetooth devices (BlueZ)', udisksctl: 'Eject for USB drives (udisks2)', fwupdmgr: 'Firmware versions and updates (fwupd, LVFS)',
    systemctl: 'Timers, services, and default target', 'apt-get': 'Updates waiting (simulated, read-only)', 'dpkg-query': 'Installed packages (Debian, Ubuntu)',
    rpm: 'Installed packages (Fedora, RHEL, openSUSE)', snap: 'Installed snaps', flatpak: 'Installed Flatpak apps', dotnet: '.NET runtimes and SDKs on this computer',
    udevadm: 'Memory slots, on systems where udev\'s cache isn\'t readable', dnf: 'Updates waiting, from dnf\'s cached metadata (Fedora, RHEL)',
    zypper: 'Updates waiting, from cached repositories (openSUSE)', pacman: 'Installed packages and updates waiting against the last sync (Arch)',
    apk: 'Installed packages and updates waiting (Alpine)'
  };
  for (const t of m.tools) row(t.name, `${t.path ? t.path : 'not installed'} — ${what[t.name] || ''}`);
  row('Bundled data', 'IEEE OUI registry (oui.tsv), PCI ID Repository (pci.ids), USB ID Repository (usb.ids), inside the app');
  row('Bundled libraries', 'D3 7, QuickLiquid, Skitter, FlashStream, Glimmer badge: all served from the app, never a CDN');
  row('Runtime', `.NET ${m.runtime}, built into the program (no .NET install needed)`);
  row('Data folder', m.dataFolder, true);
  row('Environment', 'BOARDSCOUT_DATA (data folder), XDG_DATA_HOME, DISPLAY / WAYLAND_DISPLAY (opening the browser)', true);
  if (m.agent == null) row('On machines it opens', '~/.cache/boardscout on Linux, %LOCALAPPDATA%\\BoardScout\\Remote on Windows: its copy, settings, and inventory there', true);
  row('Dashboard address', m.url, true);
  row('External hosts', 'Only when you click: 1.1.1.1/cdn-cgi/trace (public IP) and speed.cloudflare.com (speed test). fwupd talks to the LVFS on its own schedule.' +
    (m.agent == null ? ' Machines: the machines you open, over SSH; github.com/mikedopp/BoardScout/releases only when you click Get for a build this computer lacks.' : ''));
  versionsTable([['BoardScout', `${m.version}${m.commit ? ` (${m.commit})` : ''}`], ['.NET runtime', m.runtime], ['Distribution', m.distro], ['Kernel', m.kernel],
    ['Running as', m.root ? 'root' : m.user], ['Started', m.agent != null ? `over SSH${m.agent ? ' (as ' + m.agent + ')' : ''}` : 'on this computer'],
    ['Dashboard', m.url], ['Data folder', m.dataFolder]]);
}

function versionsTable(rows) {
  const versions = $('versionsTable');
  versions.replaceChildren();
  for (const [k, v] of rows) {
    const tr = el('tr');
    tr.append(el('td', null, k), el('td', null, v));
    versions.append(tr);
  }
}

// What the Windows edition depends on when it serves this dashboard (over SSH, or --serve).
function renderWindowsMeta(m, row) {
  row('Windows interfaces', 'SetupAPI and the PnP device tree, USB hub IOCTLs, storage IOCTLs, SMBIOS, WMI/CIM, the registry, IP Helper, WLAN API, performance counters', true);
  row('DriverScout', 'Inventory and driver checks: PowerShell scripts bundled with the app (Windows PowerShell 5.1), read-only');
  row('LibreHardwareMonitor', `CPU, GPU, chipset, and fan sensors (MPL-2.0). Board sensors need the PawnIO driver (${m.sensors?.pawnIo ? 'installed' + (m.sensors.pawnIoVersion ? ' ' + m.sensors.pawnIoVersion : '') : 'not installed'}) and administrator rights (${m.sensors?.elevated ? 'yes' : 'no'}).`);
  row('Bundled data', 'IEEE OUI registry (oui.tsv), PCI ID Repository (pci.ids), USB ID Repository (usb.ids), inside the app');
  row('Bundled libraries', 'ASP.NET Core (this dashboard\'s server), D3 7, QuickLiquid, Skitter, FlashStream, Glimmer badge: all served from the app, never a CDN');
  row('Runtime', `.NET ${m.runtime}, built into the program (no .NET install needed)`);
  row('Data folder', m.dataFolder, true);
  row('Environment', 'BOARDSCOUT_DATA (data folder), DOTNET_BUNDLE_EXTRACT_BASE_DIR (where the program unpacks itself)', true);
  row('Dashboard address', m.url, true);
  row('External hosts', 'Only when you click: 1.1.1.1/cdn-cgi/trace (public IP), speed.cloudflare.com (speed test), and vendor, OEM, and Microsoft Update catalog pages (Check drivers).');
  versionsTable([['BoardScout', `${m.version}${m.commit ? ` (${m.commit})` : ''}`], ['.NET runtime', m.runtime], ['Windows', m.windows],
    ['Running as', `${m.user}${m.root ? ' (administrator)' : ''}`], ['Started', m.agent != null ? `over SSH${m.agent ? ' (as ' + m.agent + ')' : ''}` : 'on this PC (--serve)'],
    ['Dashboard', m.url], ['Data folder', m.dataFolder]]);
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
  $('firmwareBtn').onclick = () => { setTab('drivers'); post('shell', { type: windows() ? 'drivers' : 'firmware' }); };
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
    setStatus(state.agent ? 'Disconnected: BoardScout stopped on that machine. Open it again from Machines.'
      : windows() ? 'BoardScout stopped. Start it again with BoardScout.exe --serve.' : 'BoardScout stopped. Start it again from the app menu or with boardscout.');
    closePop();
  };
  $('dataFolderBtn').onclick = () => state.meta && copy(state.meta.dataFolder, 'Data folder path');
  $('healthChip').onclick = () => setTab('efficiency');
  $('driverSearch').addEventListener('input', renderDrivers);

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
