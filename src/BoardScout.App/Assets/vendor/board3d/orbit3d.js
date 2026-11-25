// What a 3D view needs around its scene: a renderer that draws only on demand, a camera that turns, slides and
// zooms about the floor and fits what is on it, a layer of flat tags over the picture, and pointer events told
// apart as hover, click and drag. The views put their own things in the scene.
import * as THREE from '../three/three.module.js';
export { THREE };

export function supported() {
  try {
    const gl = document.createElement('canvas').getContext('webgl2');
    if (!gl) return false;
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return true;
  } catch { return false; }
}

// opts: fov, az, pol (the first look), top (how tall the tallest thing stands), room {x, up, down} (how much of the
// picture the floor may fill), step(dt, now) -> true to keep animating, drawn() after each frame,
// hover(x, y), click(x, y), fail(why)
export function kit(stage, canvas, opts) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'low-power' });
  renderer.setClearColor(0x000000, 0);
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(opts.fov || 28, 1, 10, 40000);
  scene.add(new THREE.HemisphereLight(0xcfe3ff, 0x0a1018, .9));
  const key = new THREE.DirectionalLight(0xffffff, 2);
  key.position.set(-300, 900, 500);
  scene.add(key);

  const tags = document.createElement('div');
  tags.className = 'tags3d';
  canvas.after(tags);

  const HOME = { az: opts.az ?? -.2, pol: opts.pol ?? .7 }, room = { x: .94, up: .86, down: .86, ...opts.room };
  const K = { box: null, motion: true, shown: false, raf: 0, last: 0, moved: false, width: 0, height: 0,
    cam: { ...HOME, dist: 2000, tx: 0, tz: 0 }, goal: { ...HOME, dist: 2000, tx: 0, tz: 0 } };

  const probe = new THREE.PerspectiveCamera(), v = new THREE.Vector3();
  function fit() {
    if (!K.box) return 2000;
    const { min, max } = K.box, s = Math.sin(HOME.pol), cx = (min.x + max.x) / 2, cz = (min.z + max.z) / 2 + (max.z - min.z) * .08;
    const fits = dist => {
      probe.fov = camera.fov; probe.aspect = camera.aspect; probe.near = 1; probe.far = 80000;
      probe.position.set(cx + dist * s * Math.sin(HOME.az), dist * Math.cos(HOME.pol), cz + dist * s * Math.cos(HOME.az));
      probe.lookAt(cx, 0, cz);
      probe.updateProjectionMatrix(); probe.updateMatrixWorld();
      for (const x of [min.x, max.x]) for (const z of [min.z, max.z]) for (const y of z === min.z ? [0, opts.top || 0] : [0]) {
        v.set(x, y, z).project(probe);
        if (Math.abs(v.x) > room.x || v.y > room.up || v.y < -room.down) return false;
      }
      return true;
    };
    let near = 150, far = 40000;
    for (let i = 0; i < 24; i++) { const mid = (near + far) / 2; if (fits(mid)) far = mid; else near = mid; }
    return far;
  }
  function home() {
    const b = K.box;
    return { ...HOME, dist: fit(), tx: b ? (b.min.x + b.max.x) / 2 : 0, tz: b ? (b.min.z + b.max.z) / 2 + (b.max.z - b.min.z) * .08 : 0 };
  }
  function clampGoal() {
    const g = K.goal;
    g.pol = Math.max(.06, Math.min(1.45, g.pol));
    g.dist = Math.max(180, Math.min(fit() * 1.8, g.dist));
    if (K.box) {
      g.tx = Math.max(K.box.min.x, Math.min(K.box.max.x, g.tx));
      g.tz = Math.max(K.box.min.z, Math.min(K.box.max.z, g.tz));
    }
  }

  function invalidate() { if (!K.raf && K.shown) K.raf = requestAnimationFrame(frame); }
  function frame(now) {
    K.raf = 0;
    if (!K.shown) return;
    const dt = Math.min(.1, (now - (K.last || now)) / 1000);
    K.last = now;
    let moving = false;
    const ease = K.motion ? Math.min(1, dt * 11) : 1;
    for (const k of ['az', 'pol', 'dist', 'tx', 'tz']) {
      const gap = K.goal[k] - K.cam[k];
      if (Math.abs(gap) > (k === 'az' || k === 'pol' ? .0006 : .3)) { K.cam[k] += gap * ease; moving = true; } else K.cam[k] = K.goal[k];
    }
    const c = K.cam, s = Math.sin(c.pol);
    camera.position.set(c.tx + c.dist * s * Math.sin(c.az), c.dist * Math.cos(c.pol), c.tz + c.dist * s * Math.cos(c.az));
    camera.lookAt(c.tx, 0, c.tz);
    const busy = opts.step ? opts.step(dt, now, ease) : false;
    renderer.render(scene, camera);
    opts.drawn?.();
    if (moving) invalidate();
    else if (busy && !document.hidden) { K.raf = -1; setTimeout(() => { K.raf = 0; invalidate(); }, 30); }
  }

  function resize() {
    const w = stage.clientWidth, h = stage.clientHeight;
    if (!w || !h || (w === K.width && h === K.height)) return;
    K.width = w; K.height = h;
    renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    if (!K.moved) { K.goal = home(); K.cam = { ...K.goal }; }
    invalidate();
  }
  const watcher = new ResizeObserver(resize);
  watcher.observe(stage);

  let drag = null;
  canvas.addEventListener('pointerdown', e => {
    if (e.button !== 0 && e.button !== 2) return;
    drag = { x: e.clientX, y: e.clientY, pan: e.button === 2 || e.shiftKey, moved: false, id: e.pointerId };
    try { canvas.setPointerCapture(e.pointerId); } catch { /* fine */ }
  });
  canvas.addEventListener('pointermove', e => {
    if (!drag) { opts.hover?.(e.clientX, e.clientY); return; }
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    drag.moved = true; K.moved = true;
    drag.x = e.clientX; drag.y = e.clientY;
    const g = K.goal;
    if (drag.pan) {
      const k = g.dist * 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / Math.max(1, K.height);
      const c = Math.cos(g.az), s = Math.sin(g.az), tilt = Math.max(.3, Math.cos(g.pol));
      g.tx -= (dx * c + dy * s / tilt) * k;
      g.tz -= (-dx * s + dy * c / tilt) * k;
    } else { g.az -= dx * .005; g.pol -= dy * .0045; }
    clampGoal();
    invalidate();
  });
  const release = e => {
    if (!drag) return;
    const was = drag;
    drag = null;
    try { canvas.releasePointerCapture(was.id); } catch { /* fine */ }
    if (!was.moved && e.type === 'pointerup' && e.button === 0) opts.click?.(e.clientX, e.clientY);
  };
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('pointerleave', () => { if (!drag) opts.hover?.(-1, -1); });
  canvas.addEventListener('contextmenu', e => e.preventDefault());
  canvas.addEventListener('wheel', e => { e.preventDefault(); K.moved = true; K.goal.dist *= e.deltaY < 0 ? 1 / 1.12 : 1.12; clampGoal(); invalidate(); }, { passive: false });
  const reset = () => { K.moved = false; K.goal = home(); invalidate(); };
  canvas.addEventListener('keydown', e => {
    const g = K.goal;
    if (e.key === 'ArrowLeft') g.az -= .12; else if (e.key === 'ArrowRight') g.az += .12;
    else if (e.key === 'ArrowUp') g.pol -= .08; else if (e.key === 'ArrowDown') g.pol += .08;
    else if (e.key === '+' || e.key === '=') g.dist /= 1.15; else if (e.key === '-') g.dist *= 1.15;
    else if (e.key === 'Home') { reset(); e.preventDefault(); return; }
    else return;
    e.preventDefault(); K.moved = true; clampGoal(); invalidate();
  });
  canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); opts.fail?.('The graphics driver stopped the 3D view.'); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) invalidate(); });

  const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
  return {
    scene, camera, renderer, tags, invalidate, reset, K,
    setBox(box) { K.box = box; if (!K.moved) { K.goal = home(); K.cam = { ...K.goal }; } invalidate(); },
    fitDistance: fit,
    // Fly the camera's aim to a point on the floor, coming closer if it is far out.
    lookAt(x, z) { K.moved = true; K.goal.tx = x; K.goal.tz = z; K.goal.dist = Math.min(K.goal.dist, fit() * .6); clampGoal(); invalidate(); },
    pick(hits, x, y) {
      const box = canvas.getBoundingClientRect();
      if (!box.width || x < box.left || y < box.top) return null;
      ndc.set((x - box.left) / box.width * 2 - 1, -((y - box.top) / box.height) * 2 + 1);
      ray.setFromCamera(ndc, camera);
      return ray.intersectObjects(hits, false)[0]?.object || null;
    },
    // Where a point in the scene lands on the stage, in pixels (null when it is out of the picture).
    project(world) {
      v.copy(world).project(camera);
      if (v.z > 1 || Math.abs(v.x) > 1.08 || Math.abs(v.y) > 1.08) return null;
      return { x: (v.x + 1) / 2 * K.width, y: (1 - v.y) / 2 * K.height };
    },
    setMotion(on) { K.motion = on; invalidate(); },
    show(on) {
      K.shown = on;
      tags.hidden = !on;
      if (on) { K.width = 0; resize(); invalidate(); } else { if (K.raf > 0) cancelAnimationFrame(K.raf); K.raf = 0; }
    },
    dispose() { watcher.disconnect(); tags.remove(); renderer.dispose(); }
  };
}
