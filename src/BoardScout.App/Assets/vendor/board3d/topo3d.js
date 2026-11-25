// Topology in 3D: the same tree (the CPU, what hangs off it, and what hangs off the chipset), with each link drawn
// as a pipe as thick as the lanes it carries, so the chipset's shared uplink reads as the narrow place it is.
// Names and bus labels are flat tags that face the reader. Draws only when something changes or the pipes flow.
import * as THREE from '../three/three.module.js';

export function supported() {
  try {
    const gl = document.createElement('canvas').getContext('webgl2');
    if (!gl) return false;
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return true;
  } catch { return false; }
}

// How many lanes a link is worth, for its thickness: "PCIe 4.0 x16" is 16, a SATA or USB link 1.
function lanesOf(data) {
  const m = /x(\d+)/i.exec(data.bus || '');
  if (m) return +m[1];
  if (/DDR|memory|channel/i.test(data.bus || '')) return 8;
  if (/uplink/i.test(data.bus || '')) return 4;
  return 1;
}

// api: { onHover(datum, x, y), onMove(x, y), onLeave(), onFail(why) }
export function create(stage, canvas, api) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'low-power' });
  renderer.setClearColor(0x000000, 0);
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(28, 1, 10, 20000);
  scene.add(new THREE.HemisphereLight(0xcfe3ff, 0x0a1018, .9));
  const key = new THREE.DirectionalLight(0xffffff, 2);
  key.position.set(-300, 900, 500);
  scene.add(key);

  const tags = document.createElement('div');
  tags.className = 'tags3d';
  canvas.after(tags);

  const dash = (() => {
    const c = document.createElement('canvas');
    c.width = 32; c.height = 4;
    const ctx = c.getContext('2d');
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, 9, 4);
    const t = new THREE.CanvasTexture(c);
    t.wrapS = THREE.RepeatWrapping;
    return t;
  })();

  const HOME = { az: -.22, pol: .78 };
  const S = { group: null, nodes: [], links: [], hits: [], box: null, motion: true, shown: false, raf: 0, last: 0,
    cam: { ...HOME, dist: 2000, tx: 0, tz: 0 }, goal: { ...HOME, dist: 2000, tx: 0, tz: 0 }, moved: false, hovered: null, width: 0, height: 0 };

  const probe = new THREE.PerspectiveCamera(), v = new THREE.Vector3();
  function fit() {
    if (!S.box) return 2000;
    const { min, max } = S.box, s = Math.sin(HOME.pol);
    const cx = (min.x + max.x) / 2, cz = (min.z + max.z) / 2 + (max.z - min.z) * .16;
    const fits = dist => {
      probe.fov = camera.fov; probe.aspect = camera.aspect; probe.near = 1; probe.far = 40000;
      probe.position.set(cx + dist * s * Math.sin(HOME.az), dist * Math.cos(HOME.pol), cz + dist * s * Math.cos(HOME.az));
      probe.lookAt(cx, 0, cz);
      probe.updateProjectionMatrix(); probe.updateMatrixWorld();
      for (const x of [min.x, max.x]) for (const z of [min.z, max.z]) for (const y of [0, 90]) {
        v.set(x, y, z).project(probe);
        if (Math.abs(v.x) > .9 || v.y > .7 || v.y < -.82) return false;
      }
      return true;
    };
    let near = 200, far = 20000;
    for (let i = 0; i < 22; i++) { const mid = (near + far) / 2; if (fits(mid)) far = mid; else near = mid; }
    return far;
  }
  function home() { return { ...HOME, dist: fit(), tx: S.box ? (S.box.min.x + S.box.max.x) / 2 : 0, tz: S.box ? (S.box.min.z + S.box.max.z) / 2 + (S.box.max.z - S.box.min.z) * .16 : 0 }; }
  function place() {
    const c = S.cam, s = Math.sin(c.pol);
    camera.position.set(c.tx + c.dist * s * Math.sin(c.az), c.dist * Math.cos(c.pol), c.tz + c.dist * s * Math.cos(c.az));
    camera.lookAt(c.tx, 0, c.tz);
  }

  function clear() {
    if (!S.group) return;
    scene.remove(S.group);
    S.group.traverse(o => { o.geometry?.dispose(); for (const m of [].concat(o.material || [])) m.dispose(); });
    S.group = null; S.nodes = []; S.links = []; S.hits = [];
    tags.replaceChildren();
  }

  function tag(cls, title, value, color) {
    const el = document.createElement('div');
    el.className = 'tag3d ' + cls;
    const b = document.createElement('b'), span = document.createElement('span');
    b.textContent = title; span.textContent = value || '';
    if (color) (cls.includes('bus') ? b : span).style.color = color;
    el.append(b, span);
    tags.append(el);
    return el;
  }

  // root: a d3 hierarchy after d3.tree() has placed it (x across, y down the generations).
  function build(root) {
    clear();
    const group = new THREE.Group();
    S.group = group;
    const NW = 204, ND = 62, NH = 16;
    const at = d => new THREE.Vector3(d.x, 0, d.y * 1.25);
    S.box = new THREE.Box3();
    for (const d of root.descendants()) {
      const p = at(d), color = new THREE.Color(d.data.color || '#4a6080');
      const body = new THREE.MeshStandardMaterial({ color: new THREE.Color('#101d2b').lerp(color, .16), roughness: .5, metalness: .3,
        transparent: !!d.data.dashed, opacity: d.data.dashed ? .5 : 1 });
      const slab = new THREE.Mesh(new THREE.BoxGeometry(NW, NH, ND), body);
      slab.position.set(p.x, NH / 2, p.z);
      const rim = new THREE.Mesh(new THREE.BoxGeometry(NW + 5, 2, ND + 5), new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: d.data.dashed ? .25 : .8 }));
      rim.position.set(p.x, 1, p.z);
      slab.userData.node = S.nodes.length;
      group.add(slab, rim);
      S.hits.push(slab);
      S.box.expandByPoint(new THREE.Vector3(p.x - NW / 2, 0, p.z - ND / 2)).expandByPoint(new THREE.Vector3(p.x + NW / 2, 0, p.z + ND / 2));
      S.nodes.push({ d, p, rim: rim.material, base: d.data.dashed ? .25 : .8,
        el: tag(d.data.dashed ? 'mute' : '', d.data.name.length > 26 ? d.data.name.slice(0, 24) + '…' : d.data.name, d.data.detail || '', null) });
    }
    for (const link of root.links()) {
      const a = at(link.source), b = at(link.target), data = link.target.data;
      const lanes = lanesOf(data), radius = data.dashed ? 2 : 2.2 + Math.sqrt(lanes) * 2.6;
      const rise = 34 + radius * 2;
      const curve = new THREE.CubicBezierCurve3(
        new THREE.Vector3(a.x, NH * .6, a.z + ND / 2 - 4), new THREE.Vector3(a.x, rise, (a.z + b.z) / 2),
        new THREE.Vector3(b.x, rise, (a.z + b.z) / 2), new THREE.Vector3(b.x, NH * .6, b.z - ND / 2 + 4));
      const color = new THREE.Color(data.color || '#4a6080');
      const geometry = new THREE.TubeGeometry(curve, 40, radius, 12, false);
      const pipe = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: .25, roughness: .35, metalness: .4,
        transparent: true, opacity: data.dashed ? .3 : .86 }));
      group.add(pipe);
      let flow = null;
      if (!data.dashed && data.bus) {
        const map = dash.clone();
        map.needsUpdate = true;
        map.repeat.set(Math.max(2, Math.round(curve.getLength() / 46)), 1);
        flow = new THREE.Mesh(new THREE.TubeGeometry(curve, 40, radius * .55, 8, false),
          new THREE.MeshBasicMaterial({ color: color.clone().lerp(new THREE.Color('#ffffff'), .6), map, transparent: true, depthWrite: false, toneMapped: false }));
        flow.userData.speed = .35 + Math.sqrt(lanes) * .22;
        group.add(flow);
      }
      const mid = curve.getPoint(.62);
      S.links.push({ flow, mid, el: data.bus ? tag('bus', data.bus, data.bw || '', data.color) : null });
    }
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(S.box.max.x - S.box.min.x + 260, S.box.max.z - S.box.min.z + 220),
      new THREE.MeshStandardMaterial({ color: '#081822', roughness: .8, transparent: true, opacity: .55 }));
    floor.rotation.x = -Math.PI / 2;
    floor.position.set((S.box.min.x + S.box.max.x) / 2, -1, (S.box.min.z + S.box.max.z) / 2);
    group.add(floor);
    scene.add(group);
    if (!S.moved) { S.goal = home(); S.cam = { ...S.goal }; }
    invalidate();
  }

  function placeTags() {
    const w = S.width, h = S.height, taken = [];
    const put = (el, world, lift, always) => {
      v.copy(world); v.y += lift; v.project(camera);
      if (v.z > 1 || Math.abs(v.x) > 1.1 || Math.abs(v.y) > 1.1) { el.hidden = true; return; }
      el.hidden = false;
      const bw = el.offsetWidth, bh = el.offsetHeight;
      const x = (v.x + 1) / 2 * w, y = (1 - v.y) / 2 * h;
      const left = Math.max(4, Math.min(w - bw - 4, x - bw / 2)), top = y - bh - 8;
      if (!always && taken.some(r => left < r[0] + r[2] + 2 && left + bw + 2 > r[0] && top < r[1] + r[3] + 2 && top + bh + 2 > r[1])) { el.hidden = true; return; }
      taken.push([left, top, bw, bh]);
      el.style.transform = `translate(${Math.round(left)}px,${Math.round(top)}px)`;
      el.style.setProperty('--stem', `${Math.round(Math.max(6, Math.min(bw - 6, x - left)))}px`);
    };
    for (const n of S.nodes) put(n.el, n.p, 22, true);
    for (const l of S.links) if (l.el) put(l.el, l.mid, 6, false);
  }

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
      if (Math.abs(gap) > (k === 'az' || k === 'pol' ? .0006 : .3)) { S.cam[k] += gap * ease; moving = true; } else S.cam[k] = S.goal[k];
    }
    place();
    const flowing = S.motion && S.links.some(l => l.flow);
    for (const l of S.links) if (l.flow) { l.flow.visible = S.motion; if (flowing) l.flow.material.map.offset.x -= dt * l.flow.userData.speed; }
    renderer.render(scene, camera);
    placeTags();
    if (moving) invalidate();
    else if (flowing && !document.hidden) { S.raf = -1; setTimeout(() => { S.raf = 0; invalidate(); }, 30); }
  }

  function resize() {
    const w = stage.clientWidth, h = stage.clientHeight;
    if (!w || !h || (w === S.width && h === S.height)) return;
    S.width = w; S.height = h;
    renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    if (!S.moved) { S.goal = home(); S.cam = { ...S.goal }; }
    invalidate();
  }
  const watcher = new ResizeObserver(resize);
  watcher.observe(stage);

  const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
  function pick(x, y) {
    const box = canvas.getBoundingClientRect();
    if (!box.width) return null;
    ndc.set((x - box.left) / box.width * 2 - 1, -((y - box.top) / box.height) * 2 + 1);
    ray.setFromCamera(ndc, camera);
    const found = ray.intersectObjects(S.hits, false)[0];
    return found ? S.nodes[found.object.userData.node] : null;
  }
  function hover(node, x, y) {
    if (node !== S.hovered) {
      if (S.hovered) S.hovered.rim.emissiveIntensity = S.hovered.base;
      S.hovered = node;
      if (node) { node.rim.emissiveIntensity = 1.9; api.onHover(node.d, x, y); } else api.onLeave();
      canvas.style.cursor = node ? 'pointer' : '';
      invalidate();
    } else if (node) api.onMove(x, y);
  }
  function clampGoal() {
    const g = S.goal, far = fit() * 1.8;
    g.pol = Math.max(.08, Math.min(1.45, g.pol));
    g.dist = Math.max(240, Math.min(far, g.dist));
  }
  let drag = null;
  canvas.addEventListener('pointerdown', e => {
    if (e.button !== 0 && e.button !== 2) return;
    drag = { x: e.clientX, y: e.clientY, pan: e.button === 2 || e.shiftKey, id: e.pointerId };
    try { canvas.setPointerCapture(e.pointerId); } catch { /* fine */ }
  });
  canvas.addEventListener('pointermove', e => {
    if (!drag) { hover(pick(e.clientX, e.clientY), e.clientX, e.clientY); return; }
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    drag.x = e.clientX; drag.y = e.clientY;
    S.moved = true;
    const g = S.goal;
    if (drag.pan) {
      const k = g.dist * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / Math.max(1, S.height);
      const c = Math.cos(g.az), s = Math.sin(g.az), tilt = Math.max(.3, Math.cos(g.pol));
      g.tx -= (dx * c + dy * s / tilt) * k;
      g.tz -= (-dx * s + dy * c / tilt) * k;
    } else { g.az -= dx * .005; g.pol -= dy * .0045; }
    clampGoal();
    invalidate();
  });
  const release = e => { if (drag) { try { canvas.releasePointerCapture(drag.id); } catch { /* fine */ } drag = null; } };
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('pointerleave', () => { if (!drag) hover(null); });
  canvas.addEventListener('contextmenu', e => e.preventDefault());
  canvas.addEventListener('wheel', e => { e.preventDefault(); S.moved = true; S.goal.dist *= e.deltaY < 0 ? 1 / 1.12 : 1.12; clampGoal(); invalidate(); }, { passive: false });
  canvas.addEventListener('dblclick', () => { S.moved = false; S.goal = home(); invalidate(); });
  canvas.addEventListener('keydown', e => {
    const g = S.goal;
    if (e.key === 'ArrowLeft') g.az -= .12; else if (e.key === 'ArrowRight') g.az += .12;
    else if (e.key === 'ArrowUp') g.pol -= .08; else if (e.key === 'ArrowDown') g.pol += .08;
    else if (e.key === '+' || e.key === '=') g.dist /= 1.15; else if (e.key === '-') g.dist *= 1.15;
    else if (e.key === 'Home') { S.moved = false; S.goal = home(); invalidate(); e.preventDefault(); return; }
    else return;
    e.preventDefault(); S.moved = true; clampGoal(); invalidate();
  });
  canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); api.onFail('The graphics driver stopped the 3D view.'); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) invalidate(); });

  return {
    setTree: build,
    setMotion(on) { S.motion = on; invalidate(); },
    show(on) {
      S.shown = on;
      tags.hidden = !on;
      if (on) { S.width = 0; resize(); invalidate(); } else { if (S.raf > 0) cancelAnimationFrame(S.raf); S.raf = 0; hover(null); }
    },
    dispose() { watcher.disconnect(); clear(); tags.remove(); renderer.dispose(); }
  };
}
