// Connections in 3D: the same map (every device where the flat map puts it), as blocks on a floor that rise with
// the traffic through them, joined by pipes with dots running toward the CPU (reads, downloads) and away from it
// (writes, uploads). Names and live rates are flat tags that face the reader. A click opens the same details.
import { THREE, kit, supported } from './orbit3d.js';
export { supported };

const IN = new THREE.Color('#58a6ff'), OUT = new THREE.Color('#f59e0b');
const FONT = '"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif';

// api: { colorOf(n), liveText(n), kindOf(n), onSelect(id), onBackground(), onFail(why) }
export function create(stage, canvas, api) {
  const S = { group: null, entries: new Map(), hits: [], links: [], selected: null, hovered: null, found: null, dots: null, W: 0, H: 0 };
  const dummy = new THREE.Object3D(), spot = new THREE.Vector3();

  const k = kit(stage, canvas, {
    fov: 26, az: -.12, pol: .6, top: 60, room: { x: .97, up: .86, down: .8 },
    hover(x, y) { setHovered(k.pick(S.hits, x, y)?.userData.id || null); },
    click(x, y) { const hit = k.pick(S.hits, x, y); if (hit) api.onSelect(hit.userData.id); else api.onBackground(); },
    fail: why => api.onFail(why),
    step(dt, now, ease) {
      let busy = false;
      for (const e of S.entries.values()) {
        const gap = e.goal - e.tall;
        if (Math.abs(gap) > .15) { e.tall += gap * Math.min(1, ease * .5); busy = true; } else e.tall = e.goal;
        e.slab.scale.y = e.tall;
        e.label.position.y = e.tall + .3;
      }
      // Dots: a few per busy link and direction, spaced along it, faster on a busier link.
      let count = 0;
      if (k.K.motion && S.dots) {
        for (const l of S.links) {
          for (const [rate, dir, color] of [[l.node.rin || 0, -1, IN], [l.node.rout || 0, 1, OUT]]) {
            if (rate < 512) continue;
            const n = Math.min(6, 1 + Math.floor(Math.log10(rate / 1024 + 1) * 1.4)), speed = (36 + 26 * Math.log10(rate / 1024 + 1)) / l.length;
            const key = dir < 0 ? 'pin' : 'pout';
            l[key] = ((l[key] || 0) + dt * speed) % 1;
            for (let i = 0; i < n && count < S.dots.count; i++) {
              let u = (l[key] + i / n) % 1;
              if (dir < 0) u = 1 - u;
              l.curve.getPoint(u, spot);
              dummy.position.set(spot.x, spot.y + (dir < 0 ? 2.2 : -1.2), spot.z + (dir < 0 ? -2.4 : 2.4));
              dummy.updateMatrix();
              S.dots.setMatrixAt(count, dummy.matrix);
              S.dots.setColorAt(count, color);
              count++;
            }
            busy = true;
          }
        }
      }
      if (S.dots) {
        S.dots.count = S.dots.userData.max;
        for (let i = count; i < S.dots.userData.shown; i++) { dummy.position.set(0, -9999, 0); dummy.updateMatrix(); S.dots.setMatrixAt(i, dummy.matrix); }
        S.dots.userData.shown = count;
        S.dots.instanceMatrix.needsUpdate = true;
        if (S.dots.instanceColor) S.dots.instanceColor.needsUpdate = true;
      }
      return busy;
    },
    drawn: placeTags
  });

  function clear() {
    if (!S.group) return;
    k.scene.remove(S.group);
    S.group.traverse(o => { o.geometry?.dispose(); for (const m of [].concat(o.material || [])) { m.map?.dispose(); m.dispose(); } });
    S.group = null; S.entries.clear(); S.hits = []; S.links = []; S.dots = null;
    k.tags.replaceChildren();
  }

  function fitText(ctx, value, max) {
    value = value || '';
    if (ctx.measureText(value).width <= max) return value;
    let keep = value.length;
    while (keep > 2 && ctx.measureText(value.slice(0, keep).trimEnd() + '…').width > max) keep--;
    return value.slice(0, keep).trimEnd() + '…';
  }
  function nameplate(n, w, d) {
    const scale = 2.5, c = document.createElement('canvas');
    c.width = Math.round(w * scale); c.height = Math.round(d * scale);
    const ctx = c.getContext('2d');
    ctx.scale(scale, scale);
    ctx.textBaseline = 'middle';
    ctx.font = `650 13.5px ${FONT}`; ctx.fillStyle = '#f1f5fa';
    ctx.fillText(fitText(ctx, (n.warning ? '⚠ ' : '') + n.name, w - 22), 12, d / 2 - 8);
    ctx.font = `11px ${FONT}`; ctx.fillStyle = '#9fb0c6';
    ctx.fillText(fitText(ctx, n.detail || api.kindOf(n) || '', w - 22), 12, d / 2 + 10);
    const texture = new THREE.CanvasTexture(c);
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.anisotropy = 8;
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, d), new THREE.MeshBasicMaterial({ map: texture, transparent: true, depthWrite: false, toneMapped: false }));
    mesh.rotation.x = -Math.PI / 2;
    return mesh;
  }

  // nodes: every node of the map (with .parent); pos: id -> {x, y, w, h} from the flat layout; links: the flat
  // map's links, each with the node it feeds and the points of its path.
  function build(nodes, pos, size, links) {
    const keep = S.selected;
    clear();
    S.W = size.width; S.H = size.height;
    const group = new THREE.Group();
    S.group = group;
    const box = new THREE.Box3();
    const X = x => x - S.W / 2, Z = y => y - S.H / 2;
    const unit = new THREE.BoxGeometry(1, 1, 1);
    unit.translate(0, .5, 0);
    for (const n of nodes) {
      const p = pos.get(n.id);
      if (!p) continue;
      const color = new THREE.Color(api.colorOf(n));
      const body = new THREE.MeshStandardMaterial({ color: new THREE.Color('#101d2b').lerp(color, .18), roughness: .5, metalness: .3, emissive: color, emissiveIntensity: 0 });
      const slab = new THREE.Mesh(unit, body);
      slab.scale.set(p.w, 8, p.h);
      slab.position.set(X(p.x + p.w / 2), 0, Z(p.y + p.h / 2));
      slab.userData.id = n.id;
      const rimMat = new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: .7 });
      const rim = new THREE.Mesh(unit, rimMat);
      rim.scale.set(p.w + 5, 1.6, p.h + 5);
      rim.position.copy(slab.position);
      const label = nameplate(n, p.w, p.h);
      label.position.set(slab.position.x, 8.3, slab.position.z);
      group.add(rim, slab, label);
      S.hits.push(slab);
      box.expandByPoint(new THREE.Vector3(X(p.x), 0, Z(p.y))).expandByPoint(new THREE.Vector3(X(p.x + p.w), 0, Z(p.y + p.h)));
      S.entries.set(n.id, { n, p, slab, body, rimMat, label, tall: 8, goal: 8, at: slab.position, el: null });
    }
    let max = 0;
    for (const link of links) {
      if (link.pts.length < 2) continue;
      const curve = new THREE.CurvePath();
      let length = 0;
      for (let i = 1; i < link.pts.length; i++) {
        const from = new THREE.Vector3(X(link.pts[i - 1][0]), 3.2, Z(link.pts[i - 1][1])), to = new THREE.Vector3(X(link.pts[i][0]), 3.2, Z(link.pts[i][1]));
        if (from.distanceTo(to) < .5) continue;
        curve.add(new THREE.LineCurve3(from, to));
        length += from.distanceTo(to);
      }
      if (!curve.curves.length) continue;
      const color = new THREE.Color(api.colorOf(link.node)), degraded = !!link.node.link?.degraded;
      const pipe = new THREE.Mesh(new THREE.TubeGeometry(curve, Math.max(8, Math.min(200, Math.round(length / 6))), degraded ? 1.3 : 2.2, 6, false),
        new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: .35, roughness: .4, transparent: true, opacity: degraded ? .5 : .75 }));
      group.add(pipe);
      S.links.push({ node: link.node, curve, length: Math.max(1, length), pipe });
      max += 12;
    }
    const dots = new THREE.InstancedMesh(new THREE.SphereGeometry(2.6, 10, 8), new THREE.MeshBasicMaterial({ toneMapped: false }), Math.min(2400, Math.max(12, max)));
    dots.userData.max = dots.count; dots.userData.shown = dots.count;
    dots.frustumCulled = false;
    group.add(dots);
    S.dots = dots;
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(S.W + 120, S.H + 120), new THREE.MeshStandardMaterial({ color: '#081822', roughness: .85, transparent: true, opacity: .5 }));
    floor.rotation.x = -Math.PI / 2;
    floor.position.y = -1;
    group.add(floor);
    k.scene.add(group);
    k.setBox(box);
    S.selected = keep && S.entries.has(keep) ? keep : null;
    S.hovered = null;
    refresh();
  }

  // Heights and glow from the rates the flat map has already worked out on each node.
  function refresh() {
    for (const e of S.entries.values()) {
      const rate = (e.n.rin || 0) + (e.n.rout || 0);
      e.goal = e.n.kind === 'cpu' ? 14 : 8 + Math.min(52, 15 * Math.log10(rate / 1024 + 1));
      const on = e.n.id === S.selected, over = e.n.id === S.hovered, hit = S.found?.has(e.n.id);
      e.rimMat.emissiveIntensity = on ? 2 : over ? 1.5 : hit ? 1.6 : .7;
      e.body.emissiveIntensity = on ? .5 : hit ? .4 : rate >= 1024 ? .18 : 0;
      const dim = S.found && !hit && !on;
      e.body.opacity = dim ? .35 : 1; e.body.transparent = !!dim;
      e.label.material.opacity = dim ? .35 : 1;
    }
    for (const l of S.links) l.pipe.material.emissiveIntensity = (l.node.rin || 0) + (l.node.rout || 0) >= 1024 ? 1.1 : .35;
    k.invalidate();
  }

  function setHovered(id) {
    if (id === S.hovered) return;
    S.hovered = id;
    canvas.style.cursor = id ? 'pointer' : '';
    refresh();
  }

  function placeTags() {
    const w = k.K.width, h = k.K.height, list = [];
    const close = k.K.cam.dist < k.fitDistance() * .7;
    for (const e of S.entries.values()) {
      const n = e.n, live = api.liveText(n) || '';
      let rank = n.kind === 'cpu' ? 80 : n.depth === 1 ? 40 : 5;
      if (live) rank = Math.max(rank, 60 + Math.min(15, Math.log10(((n.rin || 0) + (n.rout || 0)) / 1024 + 1) * 4));
      if (n.warning) rank = Math.max(rank, 55);
      if (S.found?.has(n.id)) rank = 85;
      if (n.id === S.hovered) rank = 90;
      if (n.id === S.selected) rank = 100;
      // A block with nothing to report keeps the name painted on it; its tag would only repeat that.
      if (rank < 50 && !(close && rank >= 40)) { if (e.el) e.el.hidden = true; continue; }
      spot.set(e.at.x, e.tall + 4, e.at.z);
      const at = k.project(spot);
      if (!at) { if (e.el) e.el.hidden = true; continue; }
      list.push({ e, live, rank, at });
    }
    list.sort((a, b) => b.rank - a.rank);
    const taken = [];
    for (const { e, live, rank, at } of list) {
      if (!e.el) {
        e.el = document.createElement('div');
        e.el.className = 'tag3d';
        e.el.append(document.createElement('b'), document.createElement('span'));
        k.tags.append(e.el);
      }
      const el = e.el, key = e.n.name + '|' + live;
      if (el.dataset.key !== key) { el.dataset.key = key; el.firstChild.textContent = e.n.name; el.lastChild.textContent = live; e.tw = 0; }
      el.hidden = false;
      if (!e.tw) { e.tw = el.offsetWidth; e.th = el.offsetHeight; }
      const left = Math.max(4, Math.min(w - e.tw - 4, at.x - e.tw / 2)), top = at.y - e.th - 9;
      if (rank < 90 && (top < 2 || taken.some(r => left < r[0] + r[2] + 4 && left + e.tw + 4 > r[0] && top < r[1] + r[3] + 3 && top + e.th + 3 > r[1]))) { el.hidden = true; continue; }
      taken.push([left, top, e.tw, e.th]);
      el.classList.toggle('sel', e.n.id === S.selected);
      el.classList.toggle('warn', !!e.n.warning);
      el.style.transform = `translate(${Math.round(left)}px,${Math.round(top)}px)`;
      el.style.setProperty('--stem', `${Math.round(Math.max(6, Math.min(e.tw - 6, at.x - left)))}px`);
    }
  }

  return {
    setMap: build,
    setFlow: refresh,
    setSelected(id, fly) {
      S.selected = id;
      const e = id && S.entries.get(id);
      if (e && fly) k.lookAt(e.at.x, e.at.z);
      refresh();
    },
    setFound(ids) { S.found = ids && ids.size ? ids : null; refresh(); },
    setMotion(on) { k.setMotion(on); },
    show(on) { k.show(on); if (!on) setHovered(null); },
    reset: k.reset,
    dispose() { clear(); k.dispose(); }
  };
}
