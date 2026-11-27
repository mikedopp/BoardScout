// The Overview board in 3D: the same parts, positions and readings as the flat board, built as a small scene.
// Parts that report a temperature glow by how hot they are, busy drives and links light their traces, and a part
// is hovered or pinned exactly as on the flat board. Draws only when something changes (or a trace is flowing),
// so it costs next to nothing while it sits there. Loaded by overview.html; three.js travels in ../three.
import * as THREE from '../three/three.module.js';

const FONT = '"Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,"Noto Sans",Ubuntu,sans-serif';
const MONO = '"Cascadia Mono",ui-monospace,"JetBrains Mono","DejaVu Sans Mono",Consolas,monospace';
const INK = '#e8edf4', SOFT = '#8a9bb2', LIVE = '#5eead4', SILK = 'rgba(226,236,248,.6)';

export function supported() {
  try {
    const gl = document.createElement('canvas').getContext('webgl2');
    if (!gl) return false;
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return true;
  } catch { return false; }
}

// ---- Small builders -----------------------------------------------------------------------------------------
function roundedShape(w, h, r) {
  r = Math.max(0, Math.min(r, w / 2, h / 2));
  const x = -w / 2, y = -h / 2, s = new THREE.Shape();
  s.moveTo(x + r, y);
  s.lineTo(x + w - r, y); s.quadraticCurveTo(x + w, y, x + w, y + r);
  s.lineTo(x + w, y + h - r); s.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  s.lineTo(x + r, y + h); s.quadraticCurveTo(x, y + h, x, y + h - r);
  s.lineTo(x, y + r); s.quadraticCurveTo(x, y, x + r, y);
  return s;
}

// A block with softened edges: w along X, d along Z, h tall, sitting on y = 0.
function block(w, d, h, radius, material, bevel = 1.1) {
  bevel = Math.min(bevel, h / 2 - .05, w / 2 - .1, d / 2 - .1);
  const g = new THREE.ExtrudeGeometry(roundedShape(w - 2 * bevel, d - 2 * bevel, Math.max(0, radius - bevel)), {
    depth: h - 2 * bevel, bevelEnabled: true, bevelThickness: bevel, bevelSize: bevel, bevelSegments: 2, curveSegments: 6
  });
  g.rotateX(-Math.PI / 2);
  g.translate(0, bevel, 0);
  const m = new THREE.Mesh(g, material);
  m.castShadow = true;
  m.receiveShadow = true;
  return m;
}

function fitText(ctx, value, max) {
  value = value || '';
  if (max <= 0) return '';
  if (ctx.measureText(value).width <= max) return value;
  let keep = value.length;
  while (keep > 2 && ctx.measureText(value.slice(0, keep).trimEnd() + '…').width > max) keep--;
  return value.slice(0, keep).trimEnd() + '…';
}
function pill(ctx, x, y, w, h, r) {
  ctx.beginPath();
  if (ctx.roundRect) ctx.roundRect(x, y, w, h, r); else ctx.rect(x, y, w, h);
}
function spacing(ctx, value) { try { ctx.letterSpacing = value; } catch { /* older browsers */ } }

// Text and marks painted on a face. `draw(ctx, w, h)` works in board units; the canvas is sharper than that.
function decal(w, h, draw, scale = 3) {
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(2, Math.round(w * scale));
  canvas.height = Math.max(2, Math.round(h * scale));
  const ctx = canvas.getContext('2d');
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 8;
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, h),
    new THREE.MeshBasicMaterial({ map: texture, transparent: true, depthWrite: false, toneMapped: false }));
  mesh.userData.redraw = () => {
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    ctx.textBaseline = 'middle';
    ctx.textAlign = 'left';
    spacing(ctx, '0px');
    draw(ctx, w, h);
    texture.needsUpdate = true;
  };
  mesh.userData.redraw();
  return mesh;
}
// Lying on a top face (reads like the flat board), or standing on a side face.
function flat(mesh, y) { mesh.rotation.x = -Math.PI / 2; mesh.position.y = y; return mesh; }

// A path from the flat board ("M..H..V..Q..") as corner points.
function pointsOf(d) {
  const pts = [];
  let x = 0, y = 0;
  for (const [, cmd, rest] of d.matchAll(/([MHVQL])([^MHVQL]*)/g)) {
    const n = rest.split(/[\s,]+/).filter(Boolean).map(Number);
    if (cmd === 'M' || cmd === 'L') { x = n[0]; y = n[1]; }
    else if (cmd === 'H') x = n[0];
    else if (cmd === 'V') y = n[0];
    else if (cmd === 'Q') { pts.push([n[0], n[1]]); x = n[2]; y = n[3]; }
    pts.push([x, y]);
  }
  return pts;
}
// A flat ribbon along corner points; u runs with the distance, so a dash texture can travel along it.
function ribbon(pts, width, W, H, y) {
  const pos = [], uv = [], hw = width / 2;
  let run = 0;
  for (let i = 1; i < pts.length; i++) {
    const [ax, ay] = pts[i - 1], [bx, by] = pts[i];
    const len = Math.hypot(bx - ax, by - ay);
    if (len < .01) continue;
    const dx = (bx - ax) / len, dy = (by - ay) / len, nx = -dy * hw, ny = dx * hw;
    const sx = ax - dx * hw, sy = ay - dy * hw, ex = bx + dx * hw, ey = by + dy * hw;
    const u0 = run / 14, u1 = (run + len) / 14;
    const quad = [[sx + nx, sy + ny, u0, 0], [sx - nx, sy - ny, u0, 1], [ex - nx, ey - ny, u1, 1], [ex + nx, ey + ny, u1, 0]];
    for (const k of [0, 1, 2, 0, 2, 3]) { pos.push(quad[k][0] - W / 2, y, quad[k][1] - H / 2); uv.push(quad[k][2], quad[k][3]); }
    run += len;
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  return g;
}

// The modelled parts (parts.glb, beside this file): plain meshes with positions, normals and one material a piece.
// Read here rather than with a general loader, because that is all the file holds.
async function loadParts(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error('parts ' + response.status);
  const buf = await response.arrayBuffer(), view = new DataView(buf);
  if (view.getUint32(0, true) !== 0x46546C67) throw new Error('not a model file');
  const jsonLength = view.getUint32(12, true);
  const j = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, 20, jsonLength)));
  const bin = 20 + jsonLength + 8;
  const read = index => {
    const a = j.accessors[index], v = j.bufferViews[a.bufferView];
    const Type = a.componentType === 5126 ? Float32Array : a.componentType === 5125 ? Uint32Array : Uint16Array;
    const size = a.type === 'VEC3' ? 3 : 1, from = bin + (v.byteOffset || 0) + (a.byteOffset || 0);
    return new THREE.BufferAttribute(new Type(buf.slice(from, from + a.count * size * Type.BYTES_PER_ELEMENT)), size);
  };
  const models = {};
  for (const node of j.nodes || []) {
    if (node.mesh == null) continue;
    models[node.name] = j.meshes[node.mesh].primitives.map(prim => {
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', read(prim.attributes.POSITION));
      geometry.setAttribute('normal', read(prim.attributes.NORMAL));
      geometry.setIndex(read(prim.indices));
      geometry.userData.shared = true;
      return { geometry, material: j.materials[prim.material] || {} };
    });
  }
  return models;
}

const AZ = -.16, POL = .6;   // the first look: a little from the left, tilted back
const COOL = new THREE.Color('#2dd4bf'), WARM = new THREE.Color('#f59e0b'), HOT = new THREE.Color('#ef4444');
// How a temperature shows: nothing without a reading, teal when cool, amber by 60 °C, red by 90 °C.
function heat(celsius) {
  if (celsius == null || !isFinite(celsius)) return null;
  const x = Math.max(0, Math.min(1, (celsius - 35) / 55));
  const color = x < .5 ? COOL.clone().lerp(WARM, x * 2) : WARM.clone().lerp(HOT, (x - .5) * 2);
  return { color, strength: x * x * .9 + x * .12 };
}
function factTemp(p) {
  for (const f of p.facts || []) {
    if (!/temp/i.test(f.label)) continue;
    const m = /^\s*(-?\d+(?:\.\d+)?)\s*°\s*C/.exec(f.value || '');
    if (m) return +m[1];
  }
  return null;
}

// ---- The view -------------------------------------------------------------------------------------------------
// api: { W, colorOf, sideOf, lanesOf, statusOf, onHover(p), onLeave(p), onSelect(p), onBackground(), onFail(why) }
export function create(stage, canvas, api) {
  const W = api.W;
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'low-power' });
  renderer.setClearColor(0x000000, 0);
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.shadowMap.autoUpdate = false;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1;

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(26, 1, 20, 12000);
  scene.add(new THREE.HemisphereLight(0xcfe3ff, 0x0a1018, .5));
  const key = new THREE.DirectionalLight(0xffffff, 1.9);
  key.position.set(-380, 900, 520);
  key.castShadow = true;
  key.shadow.mapSize.set(2048, 2048);
  Object.assign(key.shadow.camera, { left: -820, right: 820, top: 720, bottom: -720, near: 100, far: 2600 });
  key.shadow.bias = -.0004;
  key.shadow.normalBias = 1.2;
  scene.add(key, key.target);
  const rim = new THREE.DirectionalLight(0x7cc4ff, .9);
  rim.position.set(500, 300, -700);
  scene.add(rim);
  try {
    // A few soft panels to reflect in the metal: a ceiling light, a blue and a violet wall, a front strip.
    const room = new THREE.Scene();
    room.background = new THREE.Color(0x05080c);
    const panel = (color, power, x, y, z, w, h) => {
      const m = new THREE.Mesh(new THREE.PlaneGeometry(w, h),
        new THREE.MeshBasicMaterial({ color: new THREE.Color(color).multiplyScalar(power), side: THREE.DoubleSide }));
      m.position.set(x, y, z);
      m.lookAt(0, 0, 0);
      room.add(m);
    };
    panel(0xffffff, 6, 0, 40, 0, 40, 40);
    panel(0x7cc4ff, 3, -40, 10, 10, 30, 30);
    panel(0xa78bfa, 2, 40, 8, -10, 30, 30);
    panel(0xffffff, 1.5, 0, 5, 45, 60, 20);
    const pmrem = new THREE.PMREMGenerator(renderer);
    scene.environment = pmrem.fromScene(room, .03).texture;
    scene.environmentIntensity = .45;
    pmrem.dispose();
    room.traverse(o => { o.geometry?.dispose(); o.material?.dispose(); });
  } catch { /* no reflections; the lights carry it */ }

  const dash = (() => {
    const c = document.createElement('canvas');
    c.width = 28; c.height = 4;
    const ctx = c.getContext('2d');
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, 5, 4);
    const t = new THREE.CanvasTexture(c);
    t.wrapS = THREE.RepeatWrapping;
    t.colorSpace = THREE.SRGBColorSpace;
    return t;
  })();

  const S = {
    o: null, L: null, H: 720, board: null, entries: new Map(), hits: [], routes: [], memLabel: null,
    t: null, flow: null, selected: null, hovered: null, motion: true, shown: false, raf: 0, last: 0, lastFlow: 0,
    cam: { az: AZ, pol: POL, dist: 1800, tx: 0, tz: 0 }, goal: { az: AZ, pol: POL, dist: 1800, tx: 0, tz: 0 },
    moved: false, intro: true, halo: null, width: 0, height: 0, focusIndex: -1
  };

  // Names and live readings ride above the parts as flat tags that always face the reader: text painted on a
  // tilted part is small and skewed, a tag is not. The parts that carry a reading always show theirs; the rest
  // show when hovered, pinned, or when the board is zoomed in.
  const tags = document.createElement('div');
  tags.className = 'tags3d';
  canvas.after(tags);
  const anchor = new THREE.Vector3();
  function tagFor(e) {
    const p = e.p, empty = p.empty || p.tone === 'off';
    const short = (p.silk || p.name || '').replace(/^(AMD|Intel)\s+/i, '').replace(/\s+chipset$/i, '');
    let title = p.name, value = e.live || '', rank = 0;
    if (p.kind === 'cpu') rank = 80;
    else if (p.kind === 'gpu' && p.zone === 'slots' && !empty) rank = 70;
    else if (p.kind === 'chipset') { title = short; rank = 55; }
    else if (p.kind === 'dimm' && !empty) { title = `${p.name}${p.silk ? ' · ' + p.silk.replace(/^DDR\d_/, '') : ''}`; value = e.temp != null ? `${Math.round(e.temp)}°C` : ''; rank = 50; }
    else if ((p.kind === 'nvme' || p.kind === 'disk') && !empty) { rank = 45; if (!value && e.temp != null) value = `${Math.round(e.temp)}°C`; }
    else if (p.kind === 'fan') { title = p.silk || p.name; rank = /RPM/.test(value) ? 30 : 0; }
    else if (p.kind === 'cooler') rank = 60;
    else if (empty) title = `${p.silk ? p.silk + ' · ' : ''}${p.tone === 'off' ? 'Off' : 'Open'}`;
    else if (p.silk && p.silk !== p.name && p.zone !== 'io') title = `${p.silk} · ${p.name}`;
    if (e.busy) rank = Math.max(rank, 65);
    if (p.tone === 'warn') rank = Math.max(rank, 60);
    if (p.id === S.hovered) rank = 90;
    if (p.id === S.selected) rank = 100;
    return { title, value, rank };
  }
  function placeTags() {
    const w = S.width, h = S.height;
    if (!w || !h) return;
    const near = S.cam.dist < fitDistance(POL) * .62;
    const list = [];
    for (const e of S.entries.values()) {
      const t = tagFor(e);
      if (t.rank < 20 && !near) { if (e.tag) e.tag.hidden = true; continue; }
      anchor.set(e.g.position.x, e.top + e.lift + 5, e.g.position.z).project(camera);
      if (anchor.z > 1 || Math.abs(anchor.x) > 1.05 || Math.abs(anchor.y) > 1.05) { if (e.tag) e.tag.hidden = true; continue; }
      list.push({ e, t, x: (anchor.x + 1) / 2 * w, y: (1 - anchor.y) / 2 * h });
    }
    list.sort((a, b) => b.t.rank - a.t.rank);
    const taken = [];
    for (const { e, t, x, y } of list) {
      if (!e.tag) {
        e.tag = document.createElement('div');
        e.tag.className = 'tag3d';
        e.tag.append(document.createElement('b'), document.createElement('span'));
        tags.append(e.tag);
      }
      const el = e.tag, key = t.title + '|' + t.value;
      if (el.dataset.key !== key) {
        el.dataset.key = key;
        el.firstChild.textContent = t.title;
        el.lastChild.textContent = t.value;
        e.tagW = 0;
      }
      const hot = t.value && e.temp != null ? heat(e.temp) : null;
      el.lastChild.style.color = hot ? '#' + hot.color.clone().lerp(new THREE.Color('#ffffff'), .35).getHexString() : '';
      el.hidden = false;
      if (!e.tagW) { e.tagW = el.offsetWidth; e.tagH = el.offsetHeight; }
      const bw = e.tagW, bh = e.tagH;
      const left = Math.max(4, Math.min(w - bw - 4, x - bw / 2)), top = y - bh - 10;
      const clash = top < 2 || taken.some(r => left < r[0] + r[2] + 4 && left + bw + 4 > r[0] && top < r[1] + r[3] + 3 && top + bh + 3 > r[1]);
      if (clash && t.rank < 90) { el.hidden = true; continue; }
      taken.push([left, top, bw, bh]);
      el.classList.toggle('sel', e.p.id === S.selected);
      el.classList.toggle('warn', e.p.tone === 'warn');
      el.classList.toggle('mute', !!(e.p.empty || e.p.tone === 'off'));
      el.style.transform = `translate(${Math.round(left)}px,${Math.round(top)}px)`;
      el.style.setProperty('--stem', `${Math.round(Math.max(6, Math.min(bw - 6, x - left)))}px`);
    }
  }

  let PARTS = null;
  loadParts(new URL('./parts.glb', import.meta.url)).then(models => { PARTS = models; if (S.o) { buildBoard(); invalidate(); } })
    .catch(err => console.warn('3D part models unavailable; drawing plain blocks', err));

  // ---- Camera ------------------------------------------------------------------------------------------------
  // The nearest distance from which the whole board, and the tallest parts on it, still fit the stage.
  const probe = new THREE.PerspectiveCamera(), corner = new THREE.Vector3();
  function fitDistance(pol, tz = S.homeZ || 0) {
    const s = Math.sin(pol), hw = W / 2, hh = S.H / 2;
    const fits = dist => {
      probe.fov = camera.fov; probe.aspect = camera.aspect; probe.near = 1; probe.far = 20000;
      probe.position.set(dist * s * Math.sin(AZ), dist * Math.cos(pol), tz + dist * s * Math.cos(AZ));
      probe.lookAt(0, 0, tz);
      probe.updateProjectionMatrix();
      probe.updateMatrixWorld();
      for (const x of [-hw, hw]) for (const z of [-hh, hh]) for (const y of z < 0 ? [0, 100] : [0]) {
        corner.set(x, y, z).project(probe);
        if (Math.abs(corner.x) > .985 || corner.y > .93 || corner.y < -.86) return false;
      }
      return true;
    };
    let near = 200, far = 9000;
    for (let i = 0; i < 20; i++) { const mid = (near + far) / 2; if (fits(mid)) far = mid; else near = mid; }
    return far;
  }
  function home() {
    // Aim a little in front of the middle: the near edge looks bigger, and this is where the board sits closest.
    let best = 0, dist = Infinity;
    for (let tz = 0; tz <= 200; tz += 20) { const d = fitDistance(POL, tz); if (d < dist - .5) { dist = d; best = tz; } }
    S.homeZ = best;
    return { az: AZ, pol: POL, dist, tx: 0, tz: best };
  }
  function placeCamera() {
    const c = S.cam, s = Math.sin(c.pol);
    camera.position.set(c.tx + c.dist * s * Math.sin(c.az), c.dist * Math.cos(c.pol), c.tz + c.dist * s * Math.cos(c.az));
    camera.lookAt(c.tx, 0, c.tz);
  }
  function clampGoal() {
    const g = S.goal, far = fitDistance(POL) * 1.7;
    g.pol = Math.max(.08, Math.min(1.46, g.pol));
    g.dist = Math.max(260, Math.min(far, g.dist));
    g.tx = Math.max(-W / 2, Math.min(W / 2, g.tx));
    g.tz = Math.max(-S.H / 2, Math.min(S.H / 2, g.tz));
  }

  // ---- Building the board --------------------------------------------------------------------------------------
  function clear() {
    if (!S.board) return;
    scene.remove(S.board);
    S.board.traverse(o => {
      if (!o.geometry?.userData.shared) o.geometry?.dispose();
      for (const m of [].concat(o.material || [])) { if (m.map && m.map !== dash) m.map.dispose(); m.dispose(); }
    });
    S.board = null;
    S.entries.clear();
    S.hits = [];
    S.routes = [];
    S.halo = null;
    S.memLabel = null;
    tags.replaceChildren();
  }

  function cardLabel(ctx, w, d, e) {
    const p = e.p, empty = p.empty || p.tone === 'off';
    if (p.kind === 'header') {
      ctx.font = `600 11px ${MONO}`; ctx.fillStyle = 'rgba(226,236,248,.8)'; ctx.textAlign = 'center';
      spacing(ctx, '.08em');
      ctx.fillText(fitText(ctx, p.name, w - 12), w / 2, d / 2 + .5);
      return;
    }
    if (p.kind === 'fan') {
      ctx.font = `10.5px ${MONO}`; ctx.fillStyle = 'rgba(226,236,248,.85)';
      spacing(ctx, '.08em');
      ctx.fillText(fitText(ctx, p.silk || p.name, w * .5), 10, d / 2 + .5);
      spacing(ctx, '0px');
      ctx.font = `11px ${FONT}`; ctx.textAlign = 'right'; ctx.fillStyle = p.tone === 'good' ? LIVE : '#7f93ad';
      ctx.fillText(fitText(ctx, e.live || p.detail || '', w * .45), w - 10, d / 2 + .5);
      return;
    }
    let x = 12;
    if (p.silk && p.zone !== 'io') {
      ctx.font = `10.5px ${MONO}`;
      spacing(ctx, '.08em');
      const tw = ctx.measureText(p.silk).width;
      pill(ctx, x, d / 2 - 9, tw + 14, 18, 5);
      ctx.fillStyle = 'rgba(255,255,255,.07)'; ctx.fill();
      ctx.strokeStyle = 'rgba(255,255,255,.16)'; ctx.lineWidth = 1; ctx.stroke();
      ctx.fillStyle = empty ? 'rgba(226,236,248,.6)' : 'rgba(226,236,248,.92)';
      ctx.fillText(p.silk, x + 7, d / 2 + .5);
      spacing(ctx, '0px');
      x += tw + 24;
    }
    const lanes = (p.zone === 'm2' || p.zone === 'slots') ? api.lanesOf(p) : null;
    const right = lanes ? 46 : 12;
    const two = d >= 44;
    ctx.font = `600 13px ${FONT}`; ctx.fillStyle = empty ? '#9db0c8' : INK;
    const liveWidth = e.live ? Math.min(150, w * .34) : 0;
    ctx.fillText(fitText(ctx, p.name, w - x - right - (two ? 0 : liveWidth)), x, two ? d / 2 - 8 : d / 2 + .5);
    ctx.font = `11px ${FONT}`;
    if (two) {
      ctx.fillStyle = SOFT;
      ctx.fillText(fitText(ctx, (p.detail || '').replace(/ · (CPU|chipset)$/, ''), w - x - right - liveWidth - 8), x, d / 2 + 10);
    }
    if (e.live) {
      ctx.fillStyle = LIVE; ctx.textAlign = 'right';
      ctx.fillText(fitText(ctx, e.live, liveWidth), w - right, two ? d / 2 + 10 : d / 2 + .5);
      ctx.textAlign = 'left';
    }
    if (lanes) {
      pill(ctx, w - 40, d / 2 - 10, 28, 20, 6);
      ctx.fillStyle = 'rgba(255,255,255,.07)'; ctx.fill();
      ctx.strokeStyle = p.tone === 'warn' ? 'rgba(245,158,11,.7)' : 'rgba(255,255,255,.16)'; ctx.stroke();
      ctx.font = `600 11px ${MONO}`; ctx.textAlign = 'center'; ctx.fillStyle = p.tone === 'warn' ? '#fcd38d' : '#b8c7d9';
      ctx.fillText('x' + lanes, w - 26, d / 2 + .5);
    }
  }

  function buildPart(p, r, parent) {
    const H = S.H;
    const base = new THREE.Color(api.colorOf(p));
    const empty = p.empty || p.tone === 'off';
    const accent = empty ? new THREE.Color('#56708f') : base;
    const g = new THREE.Group();
    g.position.set(r.x + r.w / 2 - W / 2, 0, r.y + r.h / 2 - H / 2);
    const e = { p, r, g, glow: [], decals: [], top: 0, live: '', lift: 0, liftGoal: 0, temp: factTemp(p), busy: false, rimBase: empty ? .22 : .55 };
    const std = (color, opts) => new THREE.MeshStandardMaterial({ color, roughness: .55, metalness: .25, ...opts });
    const face = (tint = .1, opts) => std(new THREE.Color('#101d2b').lerp(base, tint), opts);
    const radius = p.kind === 'cpu' ? 16 : p.kind === 'dimm' ? 4 : p.kind === 'header' || p.kind === 'fan' ? 7 : 10;

    e.rim = std(accent, { emissive: accent, emissiveIntensity: e.rimBase, transparent: empty, opacity: empty ? .75 : 1 });
    const rimMesh = block(r.w + 3.2, r.h + 3.2, 1.4, radius + 1.6, e.rim, .3);
    rimMesh.castShadow = false;
    g.add(rimMesh);
    // A modelled piece: its own materials, so each part can take its lane colour and glow on its own.
    const model = (name, sx = 1, sy = 1, sz = 1) => {
      const group = new THREE.Group();
      for (const piece of PARTS[name] || []) {
        const m = piece.material, pbr = m.pbrMetallicRoughness || {}, c = pbr.baseColorFactor || [1, 1, 1, 1], x = m.extras || {};
        const material = new THREE.MeshStandardMaterial({ color: new THREE.Color().setRGB(c[0], c[1], c[2]), roughness: pbr.roughnessFactor ?? 1, metalness: pbr.metallicFactor ?? 1 });
        if (x.part_tint) material.color.lerp(accent, x.part_light ? 1 : .5);
        if (x.part_light) { material.emissive.copy(material.color); material.emissiveIntensity = empty ? .2 : 1.1; }
        else if (m.emissiveFactor) { material.emissive.setRGB(...m.emissiveFactor); material.emissiveIntensity = .3; }
        if ((m.name || '').startsWith('Heat')) e.glow.push(material);
        const mesh = new THREE.Mesh(piece.geometry, material);
        mesh.castShadow = true; mesh.receiveShadow = true;
        group.add(mesh);
      }
      group.scale.set(sx, sy, sz);
      g.add(group);
      return group;
    };
    const top = (mesh, y, x = 0, z = 0) => { flat(mesh, y); mesh.position.x = x; mesh.position.z = z; g.add(mesh); e.decals.push(mesh); return mesh; };
    const side = (mesh, turn, x, y, z) => { mesh.rotation.y = turn; mesh.position.set(x, y, z); g.add(mesh); e.decals.push(mesh); return mesh; };

    if (p.kind === 'cpu') {
      let iw = r.w - 44, ih = r.h - 44;
      if (PARTS) {
        model('cpu', r.w / 272, 1, r.h / 196);
        iw = 184 * r.w / 272; ih = 126 * r.h / 196;
        e.top = 17;
      } else {
        g.add(block(r.w, r.h, 9, 16, face(.06)));
        const lid = std('#3a4b5f', { metalness: .85, roughness: .34 });
        const ihs = block(iw, ih, 12, 10, lid, 1.6);
        ihs.position.y = 9;
        g.add(ihs);
        e.glow.push(lid);
        e.top = 21;
      }
      top(decal(iw, ih, (ctx, w, h) => {
        ctx.textAlign = 'center';
        if (p.silk) { ctx.font = `10px ${MONO}`; ctx.fillStyle = SILK; ctx.textAlign = 'right'; ctx.fillText(p.silk, w - 12, 14); ctx.textAlign = 'center'; }
        ctx.font = `650 18px ${FONT}`; ctx.fillStyle = '#f4f7fb';
        ctx.fillText(fitText(ctx, p.name, w - 24), w / 2, h / 2 - 22);
        ctx.font = `12px ${FONT}`; ctx.fillStyle = '#a9b8cb';
        ctx.fillText(fitText(ctx, p.detail || '', w - 24), w / 2, h / 2 - 1);
        ctx.font = `600 12.5px ${FONT}`; ctx.fillStyle = '#9fd2ff';
        ctx.fillText(fitText(ctx, e.live, w - 24), w / 2, h / 2 + 22);
        const load = Math.max(0, Math.min(1, (S.t?.cpu ?? S.flow?.cpu ?? 0) / 100));
        pill(ctx, 18, h - 22, w - 36, 5, 2.5); ctx.fillStyle = 'rgba(255,255,255,.12)'; ctx.fill();
        if (load > 0) { pill(ctx, 18, h - 22, (w - 36) * load, 5, 2.5); ctx.fillStyle = '#3d9eff'; ctx.fill(); }
        ctx.beginPath(); ctx.moveTo(8, h - 20); ctx.lineTo(8, h - 8); ctx.lineTo(20, h - 8); ctx.closePath();
        ctx.fillStyle = 'rgba(201,162,39,.7)'; ctx.fill();
      }), e.top + .3);
    } else if (p.kind === 'dimm' || (p.zone === 'memory' && r.h > 120)) {
      if (PARTS) model('dimm_slot', 1, 1, r.h / 198);
      else {
        g.add(block(r.w, r.h, 7, 4, std('#0b141d', { roughness: .7 })));
        for (const z of [-r.h / 2 + 4, r.h / 2 - 4]) {
          const latch = block(r.w + 4, 8, 13, 2, std(empty ? '#3b4b5f' : '#c9d3df', { roughness: .6 }), .6);
          latch.position.z = z;
          g.add(latch);
        }
      }
      e.top = 13;
      if (!empty && PARTS) {
        const sz = r.h / 198;
        model('dimm', 1, 1, sz);
        e.top = 90;
        const paint = (ctx, w, h) => {
          ctx.font = `650 17px ${FONT}`; ctx.fillStyle = '#eef4fb'; ctx.textAlign = 'center';
          ctx.fillText(fitText(ctx, p.name, w - 12), w / 2, h / 2 - 13);
          ctx.font = `11px ${MONO}`; ctx.fillStyle = 'rgba(226,236,248,.72)';
          ctx.fillText(fitText(ctx, (p.silk || p.detail || '').replace(/^DDR\d_/, ''), w - 12), w / 2, h / 2 + 5);
          if (e.temp != null) { ctx.font = `600 12px ${FONT}`; ctx.fillStyle = '#' + heat(e.temp).color.clone().lerp(new THREE.Color('#ffffff'), .25).getHexString(); ctx.fillText(`${Math.round(e.temp)}°C`, w / 2, h / 2 + 22); }
        };
        side(decal(160 * sz, 58, paint), Math.PI / 2, 4.9, 46, 0);
        side(decal(160 * sz, 58, paint), -Math.PI / 2, -4.9, 46, 0);
      } else if (!empty) {
        const len = r.h - 18, tall = 86, thick = Math.max(7, Math.min(10, r.w * .34));
        const spreader = std(new THREE.Color('#1c3148').lerp(base, .3), { metalness: .7, roughness: .38 });
        const stick = block(thick, len, tall, 2, spreader, .8);
        stick.position.y = 5;
        g.add(stick);
        e.glow.push(spreader);
        e.top = 5 + tall;
        const paint = (ctx, w, h) => {
          ctx.fillStyle = 'rgba(255,255,255,.05)';
          for (let x = 10; x < w - 10; x += 9) ctx.fillRect(x, 8, 3, h - 16);
          ctx.font = `650 17px ${FONT}`; ctx.fillStyle = '#eef4fb'; ctx.textAlign = 'center';
          ctx.fillText(fitText(ctx, p.name, w - 16), w / 2, h / 2 - 12);
          ctx.font = `11px ${MONO}`; ctx.fillStyle = 'rgba(226,236,248,.7)';
          ctx.fillText(fitText(ctx, (p.silk || p.detail || '').replace(/^DDR\d_/, ''), w - 16), w / 2, h / 2 + 8);
          if (e.temp != null) { ctx.font = `600 12px ${FONT}`; ctx.fillStyle = '#' + heat(e.temp).color.getHexString(); ctx.fillText(`${Math.round(e.temp)}°C`, w / 2, h / 2 + 27); }
        };
        side(decal(len, tall, paint), Math.PI / 2, thick / 2 + .35, 5 + tall / 2, 0);
        side(decal(len, tall, paint), -Math.PI / 2, -thick / 2 - .35, 5 + tall / 2, 0);
      }
    } else if (p.kind === 'gpu' && p.zone === 'slots' && !empty && PARTS) {
      const sx = (r.w - 12) / 528, sz = (r.h - 10) / 38;
      model('slot_body', r.w / 500, 1, 1).position.z = -13 * sz;
      model('gpu', sx, 1, sz);
      e.top = 98;
      side(decal(232 * sx, 78, (ctx, w, h) => {
        ctx.font = `650 17px ${FONT}`; ctx.fillStyle = '#f1f5fa';
        ctx.fillText(fitText(ctx, p.name, w - 20), 14, h / 2 - 20);
        ctx.font = `11.5px ${FONT}`; ctx.fillStyle = SOFT;
        ctx.fillText(fitText(ctx, (p.detail || '').replace(/ · (CPU|chipset)$/, ''), w - 20), 14, h / 2);
        ctx.font = `600 12.5px ${FONT}`; ctx.fillStyle = LIVE;
        ctx.fillText(fitText(ctx, e.live, w - 20), 14, h / 2 + 20);
      }), 0, -142 * sx, 51, 16 * sz + .4);
      top(decal(500 * sx, 24 * sz, (ctx, w, d) => cardLabel(ctx, w, d, { ...e, live: '' })), 98.3, 0, 2.5 * sz);
    } else if (p.kind === 'gpu' && p.zone === 'slots' && !empty) {
      const slot = block(r.w, 12, 8, 3, std('#0b141d', { roughness: .7 }));
      slot.position.z = -r.h / 2 + 10;
      g.add(slot);
      const cw = r.w - 6, cd = r.h - 10, tall = 92;
      const shroud = std('#19232f', { metalness: .55, roughness: .42 });
      const card = block(cw, cd, tall, 7, shroud, 1.8);
      card.position.y = 6;
      g.add(card);
      const bar = block(cw - 40, 3, 3, 1.5, std(base, { emissive: base, emissiveIntensity: 1.1 }), .5);
      bar.position.set(0, 6 + tall - 9, cd / 2 + .6);
      bar.castShadow = false;
      g.add(bar);
      e.glow.push(shroud);
      e.top = 6 + tall;
      side(decal(cw, tall, (ctx, w, h) => {
        const fans = w > 420 ? 3 : 2, rad = Math.min(30, h / 2 - 16), cy = h / 2 + 4;
        const span = Math.min(w * .5, fans * (rad * 2 + 16));
        for (let i = 0; i < fans; i++) {
          const cx = w - 26 - rad - i * (span / fans + 4);
          ctx.beginPath(); ctx.arc(cx, cy, rad, 0, Math.PI * 2); ctx.fillStyle = 'rgba(0,0,0,.45)'; ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,.14)'; ctx.lineWidth = 1.2; ctx.stroke();
          ctx.strokeStyle = 'rgba(255,255,255,.1)';
          for (let b = 0; b < 9; b++) {
            const a = b * Math.PI * 2 / 9;
            ctx.beginPath(); ctx.moveTo(cx + Math.cos(a) * 6, cy + Math.sin(a) * 6);
            ctx.quadraticCurveTo(cx + Math.cos(a + .5) * rad * .7, cy + Math.sin(a + .5) * rad * .7, cx + Math.cos(a + .8) * (rad - 3), cy + Math.sin(a + .8) * (rad - 3));
            ctx.stroke();
          }
          ctx.beginPath(); ctx.arc(cx, cy, 6, 0, Math.PI * 2); ctx.fillStyle = 'rgba(255,255,255,.12)'; ctx.fill();
        }
        const room = w - span - 60;
        ctx.font = `650 17px ${FONT}`; ctx.fillStyle = '#f1f5fa';
        ctx.fillText(fitText(ctx, p.name, room), 18, h / 2 - 16);
        ctx.font = `11.5px ${FONT}`; ctx.fillStyle = SOFT;
        ctx.fillText(fitText(ctx, (p.detail || '').replace(/ · (CPU|chipset)$/, ''), room), 18, h / 2 + 4);
        ctx.font = `600 12.5px ${FONT}`; ctx.fillStyle = LIVE;
        ctx.fillText(fitText(ctx, e.live, room), 18, h / 2 + 24);
      }), 0, 0, 6 + tall / 2, cd / 2 + .4);
      top(decal(cw, cd, (ctx, w, d) => cardLabel(ctx, w, d, { ...e, live: '' })), 6 + tall + .3);
    } else if (p.kind === 'chipset' && PARTS) {
      const sx = r.w / 192, sz = r.h / 112;
      model('heatsink', sx, 1, sz);
      e.top = 18;
      top(decal(128 * sx, 50 * sz, (ctx, w, h) => {
        const short = (p.silk || p.name || '').replace(/^(AMD|Intel)\s+/i, '').replace(/\s+chipset$/i, '');
        ctx.textAlign = 'center';
        ctx.font = `650 17px ${FONT}`; ctx.fillStyle = '#ede9fe'; ctx.fillText(fitText(ctx, short, w - 12), w / 2, h / 2 - 12);
        ctx.font = `11px ${FONT}`; ctx.fillStyle = '#b3abd3'; ctx.fillText('Chipset', w / 2, h / 2 + 4);
        ctx.font = `600 12px ${FONT}`; ctx.fillStyle = '#c4b5fd'; ctx.fillText(e.live, w / 2, h / 2 + 19);
      }), 18.3);
    } else if (p.kind === 'chipset') {
      const sink = face(.22, { metalness: .7, roughness: .4 });
      g.add(block(r.w, r.h, 18, 8, sink, 1.6));
      e.glow.push(sink);
      e.top = 18;
      top(decal(r.w, r.h, (ctx, w, h) => {
        ctx.save(); pill(ctx, 8, 8, w - 16, h - 16, 6); ctx.clip();
        ctx.strokeStyle = 'rgba(255,255,255,.055)'; ctx.lineWidth = 3;
        for (let x = -h; x < w + h; x += 9) { ctx.beginPath(); ctx.moveTo(x, h); ctx.lineTo(x + h, 0); ctx.stroke(); }
        ctx.restore();
        const short = (p.silk || p.name || '').replace(/^(AMD|Intel)\s+/i, '').replace(/\s+chipset$/i, '');
        ctx.textAlign = 'center';
        ctx.font = `650 18px ${FONT}`; ctx.fillStyle = '#ede9fe'; ctx.fillText(fitText(ctx, short, w - 24), w / 2, h / 2 - 10);
        ctx.font = `11.5px ${FONT}`; ctx.fillStyle = '#b3abd3'; ctx.fillText('Chipset', w / 2, h / 2 + 9);
        ctx.font = `600 12px ${FONT}`; ctx.fillStyle = '#c4b5fd'; ctx.fillText(e.live, w / 2, h - 16);
      }), 18.3);
    } else if (PARTS && p.kind === 'rear-io') {
      model('rear_io', r.w / 232, 1, r.h / 70);
      e.top = 44;
      top(decal(r.w - 8, r.h * 50 / 70, (ctx, w, d) => cardLabel(ctx, w, d, e)), 44.3, 0, -r.h * 9 / 70);
    } else if (PARTS && (p.kind === 'nvme' || p.kind === 'wifi') && !empty) {
      const len = r.w - 26, deep = r.h - 16;
      model('m2_conn', 1, 1, deep / 30).position.x = -r.w / 2 + 6;
      model('m2_body', len / 300, 1, deep / 30).position.x = 5;
      model('m2_screw').position.x = r.w / 2 - 7;
      e.top = 7;
      top(decal(len - 8, deep, (ctx, w, d) => cardLabel(ctx, w, d, e)), 7.1, 5);
    } else if (PARTS && (p.zone === 'slots' || p.zone === 'm2' || p.zone === 'sata' || p.kind === 'fan' || p.kind === 'header')) {
      // A low plate that carries the label, with the real connector on it.
      const plate = face(empty ? .02 : .12, empty ? { transparent: true, opacity: .55 } : null);
      const slab = block(r.w, r.h, 3, radius, plate, .9);
      if (empty) slab.castShadow = false;
      g.add(slab);
      e.glow.push(plate);
      e.top = 3;
      let labelW = r.w, labelD = r.h, lx = 0, lz = 0;
      if (p.zone === 'slots') {
        model('slot_body', (r.w - 22) / 500, 1, 1).position.set(-5, 2, -r.h / 2 + 8);
        model('slot_latch').position.set(r.w / 2 - 9, 2, -r.h / 2 + 8);
        labelD = r.h - 14; lz = 7;
        e.top = 12;
        if (!empty) {
          const riser = block(r.w - 40, 4, 58, 1.5, std(new THREE.Color('#0c2a20').lerp(base, .15), { roughness: .6 }), .6);
          riser.position.set(-5, 6, -r.h / 2 + 8);
          g.add(riser);
          e.top = 64;
        }
      } else if (p.zone === 'm2') {
        model('m2_conn', 1, 1, (r.h - 16) / 30).position.set(-r.w / 2 + 6, 2, 0);
        model('m2_screw').position.set(r.w / 2 - 8, 2, 0);
        labelW = r.w - 30;
      } else if (p.zone === 'sata') {
        model('sata').position.set(r.w / 2 - 15, 2, 0);
        if (!empty && (p.kind === 'disk' || p.kind === 'optical')) model('sata_plug').position.set(r.w / 2 - 15, 2, 0);
        labelW = r.w - 30; lx = -15;
        e.top = 14;
      } else {
        const wide = p.kind === 'header';
        model(wide ? 'pins10' : 'pins4').position.set(-r.w / 2 + (wide ? 18 : 15), 2.5, 0);
        labelW = r.w - (wide ? 34 : 28); lx = wide ? 17 : 14;
        e.top = 13;
      }
      top(decal(labelW, labelD, (ctx, w, d) => cardLabel(ctx, w, d, e)), 3.3, lx, lz);
    } else {
      const tall = p.kind === 'rear-io' ? 44 : empty ? 3 : p.kind === 'disk' || p.kind === 'optical' ? 14 : p.kind === 'fan' || p.kind === 'header' ? 10 : 8;
      const body = p.kind === 'rear-io' ? std('#232e3b', { metalness: .7, roughness: .4 })
        : face(empty ? .02 : .12, empty ? { transparent: true, opacity: .55 } : null);
      const slab = block(r.w, r.h, tall, radius, body, Math.min(1.2, tall / 3));
      if (empty) slab.castShadow = false;
      g.add(slab);
      e.glow.push(body);
      e.top = tall;
      if (!empty && p.zone === 'slots') {
        // A card standing in its slot, behind the label.
        const riser = block(r.w - 16, 5, 56, 1.5, std(new THREE.Color('#10291f').lerp(base, .12), { roughness: .6 }), .6);
        riser.position.set(0, tall - 1, -r.h / 2 + 7);
        g.add(riser);
      }
      if (!empty && (p.kind === 'nvme' || p.kind === 'wifi')) {
        const screw = new THREE.Mesh(new THREE.CylinderGeometry(3.2, 3.2, 2.4, 16), std('#c9a227', { metalness: .9, roughness: .3 }));
        screw.position.set(r.w / 2 - 9, tall + 1, 0);
        g.add(screw);
      }
      if (!empty && (p.kind === 'disk' || p.kind === 'optical')) {
        const plug = block(16, r.h - 14, 7, 2, std('#fb923c', { emissive: '#fb923c', emissiveIntensity: .25 }), .8);
        plug.position.set(r.w / 2 - 14, tall - 1, 0);
        g.add(plug);
      }
      const labelWidth = !empty && (p.kind === 'disk' || p.kind === 'optical') ? r.w - 26 : !empty && (p.kind === 'nvme' || p.kind === 'wifi') ? r.w - 14 : r.w;
      top(decal(labelWidth, r.h, (ctx, w, d) => cardLabel(ctx, w, d, e)), tall + .3, (labelWidth - r.w) / 2);
      if (p.kind === 'rear-io') {
        side(decal(r.h, tall, (ctx, w, h) => {
          ctx.fillStyle = 'rgba(0,0,0,.55)';
          const ports = [[8, 9, 13, 9], [8, 24, 13, 9], [27, 8, 9, 26], [42, 9, 11, 7], [42, 20, 11, 7], [42, 31, 11, 5]];
          for (const [x, y, pw, ph] of ports) if (x + pw < w - 4) { pill(ctx, x, y, pw, ph, 1.5); ctx.fill(); }
          for (let i = 0; i < 3; i++) { ctx.beginPath(); ctx.arc(w - 9, 10 + i * 12, 3.4, 0, Math.PI * 2); ctx.fill(); }
        }), -Math.PI / 2, -r.w / 2 - .35, tall / 2, 0);
      }
    }

    if (p.tone === 'warn') {
      const amber = new THREE.Color('#f59e0b');
      e.beacon = new THREE.Mesh(new THREE.SphereGeometry(4.4, 20, 14), std(amber, { emissive: amber, emissiveIntensity: 1.6 }));
      e.beacon.position.set(r.w / 2 - 9, e.top + 9, -r.h / 2 + 9);
      g.add(e.beacon);
    }
    e.act = new THREE.Mesh(new THREE.SphereGeometry(2.6, 14, 10), new THREE.MeshBasicMaterial({ color: LIVE, toneMapped: false }));
    e.act.position.set(-r.w / 2 + 8, e.top + 3.4, r.h / 2 - 8);
    e.act.visible = false;
    g.add(e.act);

    const hit = new THREE.Mesh(new THREE.BoxGeometry(r.w, e.top + 2, r.h), new THREE.MeshBasicMaterial({ colorWrite: false, depthWrite: false }));
    hit.position.y = (e.top + 2) / 2;
    hit.userData.id = p.id;
    g.add(hit);
    S.hits.push(hit);
    parent.add(g);
    S.entries.set(p.id, e);
    applyHeat(e);
    return e;
  }

  function buildBoard() {
    clear();
    const o = S.o, L = S.L, H = S.H;
    if (!o || !L) return;
    const board = new THREE.Group();
    S.board = board;

    const pcb = block(W - 28, H - 28, 6, 24, new THREE.MeshStandardMaterial({ color: '#081822', roughness: .62, metalness: .2 }), 1.2);
    pcb.position.y = -6;
    board.add(pcb);
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(4200, 4200), new THREE.ShadowMaterial({ opacity: .34 }));
    floor.rotation.x = -Math.PI / 2;
    floor.position.y = -6.5;
    floor.receiveShadow = true;
    board.add(floor);

    // What the board prints on itself: a fine grid, its name, the zone names, and the mounting holes.
    const silk = (ctx, value, x, y, size = 10.5, color = 'rgba(226,236,248,.5)', align = 'left', weight = '') => {
      ctx.font = `${weight} ${size}px ${MONO}`; ctx.fillStyle = color; ctx.textAlign = align;
      ctx.fillText(value, x, y - size * .35);
    };
    board.add(flat(decal(W, H, ctx => {
      ctx.fillStyle = 'rgba(255,255,255,.06)';
      for (let y = 26; y < H - 20; y += 22) for (let x = 26; x < W - 20; x += 22) ctx.fillRect(x, y, 1.6, 1.6);
      for (const [x, y] of [[36, 36], [W - 36, 36], [36, H - 36], [W - 36, H - 36]]) {
        ctx.beginPath(); ctx.arc(x, y, 9, 0, Math.PI * 2); ctx.fillStyle = 'rgba(201,162,39,.2)'; ctx.fill();
        ctx.strokeStyle = 'rgba(214,180,90,.5)'; ctx.lineWidth = 1.5; ctx.stroke();
        ctx.beginPath(); ctx.arc(x, y, 4.2, 0, Math.PI * 2); ctx.fillStyle = '#04080c'; ctx.fill();
      }
      spacing(ctx, '.16em');
      ctx.font = `600 13px ${MONO}`;
      silk(ctx, fitText(ctx, (o.layout || o.title || '').toUpperCase(), 640), 56, 58, 13, 'rgba(226,236,248,.66)', 'left', '600');
      spacing(ctx, '.08em');
      silk(ctx, (o.formFactor || '').toUpperCase(), W - 56, 58, 11, undefined, 'right');
      if (L.fans.length) silk(ctx, 'FAN HEADERS', L.fanX, 90);
      if (L.slots.length) silk(ctx, 'EXPANSION', 330, L.pos.get(L.slots[0].id).y - 12);
      if (o.parts.some(p => p.zone === 'sata')) silk(ctx, 'SATA', 912, L.sataTop - 12);
      if (L.headers.length) silk(ctx, 'HEADERS', 56, L.headerTop - 12);
      if (L.io.length) silk(ctx, 'I/O AND ONBOARD', 56, 80);
      for (const p of L.dimms) {
        const r = L.pos.get(p.id);
        silk(ctx, (p.silk || p.detail || '').replace(/^DDR\d_/, ''), r.x + r.w / 2, r.y + r.h + 18, 10, undefined, 'center');
      }
      for (const label of S.labels || []) silk(ctx, label.text, label.x, label.y, 9.5);
    }, 2), .12));
    if (L.dimms.length) {
      S.memLabel = flat(decal(500, 16, (ctx, w, h) => {
        spacing(ctx, '.08em');
        ctx.font = `10.5px ${MONO}`; ctx.fillStyle = 'rgba(226,236,248,.6)';
        const t = S.t;
        ctx.fillText(t ? `MEMORY · ${t.memoryUsedGb.toFixed(1)} / ${t.memoryTotalGb.toFixed(1)} GB IN USE` : 'MEMORY', 0, h / 2);
      }), .16);
      S.memLabel.position.set(642 + 250 - W / 2, .16, 84 - H / 2);
      board.add(S.memLabel);
    }

    // Traces: a soft glow, the line, and dashes that travel while the link is busy.
    for (const route of S.routeSpecs || []) {
      const pts = pointsOf(route.d);
      if (pts.length < 2) continue;
      const color = new THREE.Color(route.color);
      const mat = o => new THREE.MeshBasicMaterial({ color, transparent: true, depthWrite: false, toneMapped: false, side: THREE.DoubleSide, ...o });
      const glow = new THREE.Mesh(ribbon(pts, 9, W, H, .3), mat({ opacity: .09 }));
      const line = new THREE.Mesh(ribbon(pts, 2.6, W, H, .42), mat({ opacity: .62 }));
      board.add(glow, line);
      let flow = null;
      if (route.flow) {
        flow = new THREE.Mesh(ribbon(pts, 2.8, W, H, .54), mat({ color: color.clone().lerp(new THREE.Color('#ffffff'), .45), map: dash, opacity: .95 }));
        board.add(flow);
      }
      S.routes.push({ forId: route.forId, line, flow, live: false });
    }

    const halo = new THREE.Mesh(new THREE.PlaneGeometry(1, 1),
      new THREE.MeshBasicMaterial({ color: '#7cc4ff', transparent: true, opacity: .2, depthWrite: false, toneMapped: false }));
    halo.rotation.x = -Math.PI / 2;
    halo.position.y = .7;
    halo.visible = false;
    board.add(halo);
    S.halo = halo;

    for (const p of o.parts) {
      const r = L.pos.get(p.id);
      if (r) buildPart(p, r, board);
    }
    scene.add(board);
    updateLive(true);
    markSelection();
    renderer.shadowMap.needsUpdate = true;
  }

  // ---- Live readings ---------------------------------------------------------------------------------------------
  const rate = b => !b || b < 1 ? '0 B/s' : b >= 1073741824 ? (b / 1073741824).toFixed(1) + ' GB/s' : b >= 1048576 ? (b / 1048576).toFixed(b >= 104857600 ? 0 : 1) + ' MB/s' : b >= 1024 ? Math.round(b / 1024) + ' KB/s' : Math.round(b) + ' B/s';
  const bits = bytes => { const b = (bytes || 0) * 8; return b >= 1e9 ? (b / 1e9).toFixed(2) + ' Gbps' : b >= 1e6 ? (b / 1e6).toFixed(b >= 1e8 ? 0 : 1) + ' Mbps' : b >= 1e3 ? Math.round(b / 1e3) + ' Kbps' : Math.round(b) + ' bps'; };

  function applyHeat(e) {
    const h = heat(e.temp);
    for (const m of e.glow) {
      if (h) { m.emissive.copy(h.color); m.emissiveIntensity = h.strength; }
      else m.emissiveIntensity = 0;
    }
  }

  function updateLive(force) {
    const t = S.t, f = S.flow;
    const busyIds = new Set();
    for (const e of S.entries.values()) {
      const p = e.p;
      let live = e.live, temp = e.temp, busy = false;
      if (p.kind === 'cpu' && t) {
        live = `${Math.round(t.cpu)}% load${t.cpuTemp != null ? ` · ${Math.round(t.cpuTemp)}°C` : ''}${t.cpuWatts != null ? ` · ${Math.round(t.cpuWatts)} W` : ''}`;
        temp = t.cpuTemp ?? null;
      } else if (p.kind === 'chipset' && t) {
        live = t.chipsetTemp != null ? `${Math.round(t.chipsetTemp)}°C` : '';
        temp = t.chipsetTemp ?? null;
      } else if (p.kind === 'gpu' && p.zone === 'slots' && t) {
        live = [t.gpuTemp != null ? `${Math.round(t.gpuTemp)}°C` : null, t.gpuWatts != null ? `${Math.round(t.gpuWatts)} W` : null].filter(Boolean).join(' · ');
        temp = t.gpuTemp ?? null;
      } else if (p.live?.startsWith('fan:') && t) {
        const fan = (t.fans || []).find(x => 'fan:' + x.name === p.live);
        if (fan) live = fan.rpm > 0 ? `${fan.rpm} RPM` : 'Stopped';
      } else if (p.live?.startsWith('cooler:') && t) {
        const cooler = (t.coolers || []).find(c => c.key === p.live);
        if (cooler) live = cooler.line;
      } else if (p.live?.startsWith('disk:') && f) {
        const d = f.disk?.[p.live.slice(5)];
        busy = !!d && d[0] + d[1] > 1024;
        live = busy ? `R ${rate(d[0])} · W ${rate(d[1])}` : '';
      } else if (p.live?.startsWith('net:') && f) {
        const n = f.net?.[p.live.slice(4)];
        busy = !!n && n[0] + n[1] > 2048;
        live = busy ? `↓${bits(n[0])} ↑${bits(n[1])}` : '';
      }
      if (busy) busyIds.add(p.id);
      const cpuBar = p.kind === 'cpu';
      if (force || live !== e.live || temp !== e.temp || busy !== e.busy || cpuBar) {
        const heatChanged = temp !== e.temp;
        e.live = live; e.temp = temp; e.busy = busy;
        e.act.visible = busy;
        if (heatChanged || force) applyHeat(e);
        for (const d of e.decals) d.userData.redraw();
      }
    }
    for (const r of S.routes) {
      const live = r.forId ? busyIds.has(r.forId) : false;
      r.live = live;
      r.line.material.opacity = live ? 1 : .62;
      if (r.flow) r.flow.visible = S.motion && (r.forId ? live : true);
    }
    S.memLabel?.userData.redraw();
    invalidate();
  }

  // ---- Hover, pin, and the halo -------------------------------------------------------------------------------
  function markSelection() {
    for (const e of S.entries.values()) {
      const on = e.p.id === S.selected, over = e.p.id === S.hovered;
      e.liftGoal = on ? 7 : over ? 4 : 0;
      e.rim.emissiveIntensity = on ? 1.7 : over ? 1.2 : e.rimBase;
    }
    const sel = S.selected && S.entries.get(S.selected);
    if (S.halo) {
      S.halo.visible = !!sel;
      if (sel) { S.halo.scale.set(sel.r.w + 22, sel.r.h + 22, 1); S.halo.position.set(sel.g.position.x, .7, sel.g.position.z); }
    }
    invalidate();
  }
  function setHovered(id) {
    if (S.hovered === id) return;
    const before = S.hovered && S.entries.get(S.hovered), now = id && S.entries.get(id);
    S.hovered = id;
    if (before) api.onLeave(before.p);
    if (now) api.onHover(now.p);
    canvas.style.cursor = now ? 'pointer' : '';
    markSelection();
  }

  const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
  function pick(clientX, clientY) {
    const box = canvas.getBoundingClientRect();
    if (!box.width || !box.height) return null;
    ndc.set((clientX - box.left) / box.width * 2 - 1, -((clientY - box.top) / box.height) * 2 + 1);
    ray.setFromCamera(ndc, camera);
    const found = ray.intersectObjects(S.hits, false)[0];
    return found ? found.object.userData.id : null;
  }

  // ---- Drawing -----------------------------------------------------------------------------------------------
  function invalidate() { if (!S.raf && S.shown) S.raf = requestAnimationFrame(frame); }
  function frame(now) {
    S.raf = 0;
    if (!S.shown) return;
    const dt = Math.min(.1, (now - (S.last || now)) / 1000);
    S.last = now;
    let moving = false;
    const ease = S.motion ? Math.min(1, dt * 11) : 1;
    for (const k of ['az', 'pol', 'dist', 'tx', 'tz']) {
      const gap = S.goal[k] - S.cam[k];
      if (Math.abs(gap) > (k === 'az' || k === 'pol' ? .0006 : .25)) { S.cam[k] += gap * (S.intro && S.motion ? Math.min(1, dt * 3.2) : ease); moving = true; }
      else S.cam[k] = S.goal[k];
    }
    if (!moving) S.intro = false;
    placeCamera();
    let lifting = false;
    for (const e of S.entries.values()) {
      const gap = e.liftGoal - e.lift;
      if (Math.abs(gap) > .05) { e.lift += gap * ease; lifting = true; } else e.lift = e.liftGoal;
      e.g.position.y = e.lift;
    }
    if (lifting) renderer.shadowMap.needsUpdate = true;
    const flowing = S.motion && S.routes.some(r => r.flow?.visible);
    if (flowing) dash.offset.x = (dash.offset.x - dt * 1.43) % 1;
    const pulsing = S.motion && [...S.entries.values()].some(e => e.beacon || e.busy);
    if (pulsing) {
      const beat = .5 + .5 * Math.sin(now / 300);
      for (const e of S.entries.values()) {
        if (e.beacon) e.beacon.material.emissiveIntensity = 1 + beat * 1.4;
        if (e.busy) e.act.material.opacity = .35 + beat * .65, e.act.material.transparent = true;
      }
    }
    renderer.render(scene, camera);
    placeTags();
    if (moving || lifting) invalidate();
    else if ((flowing || pulsing) && !document.hidden) {
      // Flowing traces need a steady trickle of frames, not every one.
      S.raf = -1;
      setTimeout(() => { S.raf = 0; invalidate(); }, 30);
    }
  }

  function resize() {
    const w = stage.clientWidth, h = stage.clientHeight;
    if (!w || !h || (w === S.width && h === S.height)) return;
    S.width = w; S.height = h;
    renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    if (!S.moved) { S.goal = home(); if (!S.intro) S.cam = { ...S.goal }; }
    invalidate();
  }
  const watcher = new ResizeObserver(resize);
  watcher.observe(stage);

  // ---- Mouse, touch, keys --------------------------------------------------------------------------------------
  let drag = null;
  canvas.addEventListener('pointerdown', e => {
    if (e.button !== 0 && e.button !== 2) return;
    drag = { x: e.clientX, y: e.clientY, pan: e.button === 2 || e.shiftKey, moved: false, id: e.pointerId };
    try { canvas.setPointerCapture(e.pointerId); } catch { /* fine */ }
  });
  canvas.addEventListener('pointermove', e => {
    if (!drag) { setHovered(pick(e.clientX, e.clientY)); return; }
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    drag.moved = true;
    S.moved = true; S.intro = false;
    stage.classList.add('dragging');
    drag.x = e.clientX; drag.y = e.clientY;
    const g = S.goal;
    if (drag.pan) {
      const k = g.dist * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / Math.max(1, S.height);
      const c = Math.cos(g.az), s = Math.sin(g.az);
      g.tx -= (dx * c + dy * s / Math.max(.3, Math.cos(g.pol))) * k;
      g.tz -= (-dx * s + dy * c / Math.max(.3, Math.cos(g.pol))) * k;
    } else { g.az -= dx * .005; g.pol -= dy * .0045; }
    clampGoal();
    invalidate();
  });
  const release = e => {
    if (!drag) return;
    const was = drag;
    drag = null;
    stage.classList.remove('dragging');
    try { canvas.releasePointerCapture(was.id); } catch { /* fine */ }
    if (was.moved || e.type === 'pointercancel' || e.button !== 0) return;
    const id = pick(e.clientX, e.clientY);
    if (id) api.onSelect(S.entries.get(id).p); else api.onBackground();
  };
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('pointerleave', () => { if (!drag) setHovered(null); });
  canvas.addEventListener('contextmenu', e => e.preventDefault());
  canvas.addEventListener('wheel', e => {
    e.preventDefault();
    zoomBy(e.deltaY < 0 ? 1.12 : 1 / 1.12);
  }, { passive: false });
  canvas.addEventListener('dblclick', e => { if (!pick(e.clientX, e.clientY)) reset(); });
  canvas.addEventListener('keydown', e => {
    const g = S.goal;
    const step = n => {
      const parts = (S.o?.parts || []).filter(p => S.entries.has(p.id));
      if (!parts.length) return;
      S.focusIndex = (S.focusIndex + n + parts.length) % parts.length;
      setHovered(parts[S.focusIndex].id);
    };
    if (e.key === 'ArrowLeft') g.az -= .12;
    else if (e.key === 'ArrowRight') g.az += .12;
    else if (e.key === 'ArrowUp') g.pol -= .08;
    else if (e.key === 'ArrowDown') g.pol += .08;
    else if (e.key === '+' || e.key === '=') g.dist /= 1.15;
    else if (e.key === '-') g.dist *= 1.15;
    else if (e.key === 'Home') { reset(); e.preventDefault(); return; }
    else if (e.key === '.' || e.key === ']') { step(1); e.preventDefault(); return; }
    else if (e.key === ',' || e.key === '[') { step(-1); e.preventDefault(); return; }
    else if ((e.key === 'Enter' || e.key === ' ') && S.hovered) { api.onSelect(S.entries.get(S.hovered).p); e.preventDefault(); return; }
    else return;
    e.preventDefault();
    S.moved = true; S.intro = false;
    clampGoal();
    invalidate();
  });
  canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); api.onFail('The graphics driver stopped the 3D view.'); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) invalidate(); });

  function zoomBy(factor) {
    S.moved = true; S.intro = false;
    S.goal.dist /= factor;
    clampGoal();
    invalidate();
  }
  function reset() {
    S.moved = false; S.intro = false;
    S.goal = home();
    invalidate();
  }

  return {
    setBoard(o, L, routes) {
      S.o = o; S.L = L; S.H = L.height;
      S.routeSpecs = routes.routes; S.labels = routes.labels;
      buildBoard();
      if (!S.moved) {
        S.goal = home();
        // First look: start above the board and settle into the tilted view.
        S.cam = S.intro && S.motion ? { ...S.goal, az: 0, pol: .1, dist: S.goal.dist * 1.12 } : { ...S.goal };
      }
      invalidate();
    },
    setTelemetry(t) { S.t = t; updateLive(false); },
    setFlow(f) { S.flow = f; updateLive(false); },
    setSelected(id) { S.selected = id; markSelection(); },
    setMotion(on) { S.motion = on; if (!on) S.intro = false; updateLive(false); },
    show(on) {
      S.shown = on;
      tags.hidden = !on;
      if (on) { S.width = 0; resize(); invalidate(); }
      else { if (S.raf > 0) cancelAnimationFrame(S.raf); S.raf = 0; setHovered(null); }
    },
    zoomBy, reset,
    zoomPercent() { return Math.round(fitDistance(POL) / S.goal.dist * 100); },
    dispose() { watcher.disconnect(); clear(); tags.remove(); renderer.dispose(); }
  };
}
