import {
  DEFAULT_CONFIG,
  LiquidGlassEngine,
  MATERIAL_PRESETS
} from "./chunk-4TLGP4GF.mjs";

// src/animations/spring.ts
var SPRING_PRESETS = {
  /** iOS default spring — slightly bouncy, smooth (sheet presentations) */
  default: { stiffness: 300, damping: 26, mass: 1, restThreshold: 0.01, restDisplacementThreshold: 0.01, clampOnRest: true },
  /** Snappy response — button presses, tab switches */
  snappy: { stiffness: 400, damping: 30, mass: 1, restThreshold: 0.01, restDisplacementThreshold: 0.01, clampOnRest: true },
  /** Bouncy — playful elements, notification badges */
  bouncy: { stiffness: 250, damping: 15, mass: 1, restThreshold: 0.01, restDisplacementThreshold: 0.01, clampOnRest: true },
  /** Gentle — large UI panels, background elements */
  gentle: { stiffness: 150, damping: 20, mass: 1, restThreshold: 0.01, restDisplacementThreshold: 0.01, clampOnRest: true },
  /** Stiff — micro-interactions, haptic-like feedback */
  stiff: { stiffness: 600, damping: 35, mass: 1, restThreshold: 0.01, restDisplacementThreshold: 0.01, clampOnRest: true },
  /** Liquid merge — slow, gooey, for blob merging animations */
  liquidMerge: { stiffness: 120, damping: 14, mass: 1.2, restThreshold: 5e-3, restDisplacementThreshold: 5e-3, clampOnRest: true },
  /** Liquid split — faster separation with slight overshoot */
  liquidSplit: { stiffness: 280, damping: 18, mass: 0.8, restThreshold: 0.01, restDisplacementThreshold: 0.01, clampOnRest: true }
};
var Spring = class {
  // Damped frequency
  constructor(initialValue = 0, config = "default") {
    this._startTime = 0;
    this._startValue = 0;
    this._startVelocity = 0;
    this._atRest = true;
    // Derived
    this._omega0 = 0;
    // Natural frequency
    this._zeta = 0;
    // Damping ratio
    this._omegaD = 0;
    this.cfg = typeof config === "string" ? { ...SPRING_PRESETS[config] } : { ...SPRING_PRESETS.default, ...config };
    this._value = initialValue;
    this._target = initialValue;
    this._velocity = 0;
    this.computeDerivedConstants();
  }
  computeDerivedConstants() {
    const { stiffness, damping, mass } = this.cfg;
    this._omega0 = Math.sqrt(stiffness / mass);
    this._zeta = damping / (2 * Math.sqrt(stiffness * mass));
    if (this._zeta < 1) {
      this._omegaD = this._omega0 * Math.sqrt(1 - this._zeta * this._zeta);
    } else {
      this._omegaD = 0;
    }
  }
  /** Set new target — starts or continues animation */
  setTarget(target) {
    if (target === this._target && this._atRest) return;
    this._startValue = this._value;
    this._startVelocity = this._velocity;
    this._startTime = -1;
    this._target = target;
    this._atRest = false;
  }
  /** Interrupt with new value (e.g., during gesture) */
  setValue(value, velocity = 0) {
    this._value = value;
    this._velocity = velocity;
    this._startValue = value;
    this._startVelocity = velocity;
    this._startTime = -1;
    this._atRest = false;
  }
  /** Add velocity impulse (e.g., flick gesture) */
  addVelocity(v) {
    this._velocity += v;
    this._startValue = this._value;
    this._startVelocity = this._velocity;
    this._startTime = -1;
    this._atRest = false;
  }
  /**
   * Advance spring to time `now` (ms timestamp from performance.now()).
   * Returns true if still animating, false if at rest.
   */
  tick(now) {
    if (this._atRest) return false;
    if (this._startTime < 0) {
      this._startTime = now;
    }
    const t = (now - this._startTime) / 1e3;
    const x0 = this._startValue - this._target;
    const v0 = this._startVelocity;
    let x;
    let v;
    if (this._zeta < 1) {
      const env = Math.exp(-this._zeta * this._omega0 * t);
      const cos = Math.cos(this._omegaD * t);
      const sin = Math.sin(this._omegaD * t);
      const A = x0;
      const B = (v0 + this._zeta * this._omega0 * x0) / this._omegaD;
      x = env * (A * cos + B * sin);
      v = env * ((B * this._omegaD - A * this._zeta * this._omega0) * cos - (A * this._omegaD + B * this._zeta * this._omega0) * sin);
    } else if (this._zeta === 1) {
      const env = Math.exp(-this._omega0 * t);
      x = env * (x0 + (v0 + this._omega0 * x0) * t);
      v = env * (v0 * (1 - this._omega0 * t) - x0 * this._omega0 * this._omega0 * t);
    } else {
      const s1 = -this._omega0 * (this._zeta - Math.sqrt(this._zeta * this._zeta - 1));
      const s2 = -this._omega0 * (this._zeta + Math.sqrt(this._zeta * this._zeta - 1));
      const A = (v0 - s2 * x0) / (s1 - s2);
      const B = x0 - A;
      x = A * Math.exp(s1 * t) + B * Math.exp(s2 * t);
      v = A * s1 * Math.exp(s1 * t) + B * s2 * Math.exp(s2 * t);
    }
    this._value = this._target + x;
    this._velocity = v;
    if (Math.abs(x) < this.cfg.restDisplacementThreshold && Math.abs(v) < this.cfg.restThreshold) {
      if (this.cfg.clampOnRest) {
        this._value = this._target;
        this._velocity = 0;
      }
      this._atRest = true;
      return false;
    }
    return true;
  }
  // ─── Accessors ────────────────────────────────────────────────
  get value() {
    return this._value;
  }
  get target() {
    return this._target;
  }
  get velocity() {
    return this._velocity;
  }
  get atRest() {
    return this._atRest;
  }
  /** Damping ratio — <1 bouncy, =1 critical, >1 overdamped */
  get dampingRatio() {
    return this._zeta;
  }
  /** Update spring config on the fly */
  updateConfig(config) {
    this._startValue = this._value;
    this._startVelocity = this._velocity;
    this._startTime = -1;
    this.cfg = { ...this.cfg, ...config };
    this.computeDerivedConstants();
  }
};
var SpringVector = class {
  constructor(initialValues, config = "default") {
    this.springs = initialValues.map((v) => new Spring(v, config));
  }
  setTarget(targets) {
    for (let i = 0; i < this.springs.length; i++) {
      this.springs[i].setTarget(targets[i]);
    }
  }
  setValue(values, velocities) {
    for (let i = 0; i < this.springs.length; i++) {
      this.springs[i].setValue(values[i], velocities?.[i] ?? 0);
    }
  }
  addVelocity(velocities) {
    for (let i = 0; i < this.springs.length; i++) {
      this.springs[i].addVelocity(velocities[i]);
    }
  }
  tick(now) {
    let anyActive = false;
    for (const s of this.springs) {
      if (s.tick(now)) anyActive = true;
    }
    return anyActive;
  }
  get values() {
    return this.springs.map((s) => s.value);
  }
  get atRest() {
    return this.springs.every((s) => s.atRest);
  }
  get velocities() {
    return this.springs.map((s) => s.velocity);
  }
};

// src/animations/scheduler.ts
var _instance = null;
var _AnimationScheduler = class _AnimationScheduler {
  constructor() {
    this.animations = /* @__PURE__ */ new Map();
    this.nextId = 0;
    this.rafId = null;
    this.running = false;
    // Performance tracking
    this._frameTime = 0;
    this._frameCount = 0;
    this._droppedFrames = 0;
    this._lastTimestamp = 0;
  }
  /** Whether the OS requests reduced motion. Result is cached and kept live
   *  via a matchMedia change listener. Safe in non-DOM/SSR (returns false). */
  static prefersReducedMotion() {
    if (_AnimationScheduler._reduceMotion !== null) return _AnimationScheduler._reduceMotion;
    if (typeof matchMedia !== "function") {
      return _AnimationScheduler._reduceMotion = false;
    }
    const q = matchMedia("(prefers-reduced-motion: reduce)");
    _AnimationScheduler._reduceMotion = q.matches;
    const onChange = () => {
      _AnimationScheduler._reduceMotion = q.matches;
    };
    if (typeof q.addEventListener === "function") q.addEventListener("change", onChange);
    else if (typeof q.addListener === "function") q.addListener(onChange);
    return _AnimationScheduler._reduceMotion;
  }
  /** Get singleton scheduler (all liquid elements share one loop) */
  static shared() {
    if (!_instance) {
      _instance = new _AnimationScheduler();
    }
    return _instance;
  }
  /**
   * Schedule an animation callback.
   * Callback is called every frame until it returns false.
   * Returns an ID that can be used to cancel.
   */
  schedule(callback, priority = 0) {
    const id = ++this.nextId;
    if (_AnimationScheduler.prefersReducedMotion()) {
      this.settleImmediately(callback);
      return id;
    }
    this.animations.set(id, { id, callback, priority });
    this.wake();
    return id;
  }
  /**
   * Fast-forward an animation to rest synchronously (no rAF, no paint between
   * steps → no visible motion). Used only under prefers-reduced-motion.
   * Springs are analytic in `now`, so they settle in a few steps; the step cap
   * bounds continuous/non-settling callbacks so this can never hang.
   */
  settleImmediately(callback) {
    const STEP = 1e3 / 60;
    const MAX_STEPS = 600;
    let now = typeof performance !== "undefined" ? performance.now() : 0;
    for (let i = 0; i < MAX_STEPS; i++) {
      if (!callback(now)) return;
      now += STEP;
    }
  }
  /** Cancel a scheduled animation */
  cancel(id) {
    this.animations.delete(id);
    if (this.animations.size === 0) {
      this.sleep();
    }
  }
  /** Cancel all animations */
  cancelAll() {
    this.animations.clear();
    this.sleep();
  }
  /** Number of active animations */
  get activeCount() {
    return this.animations.size;
  }
  /** Performance metrics */
  get metrics() {
    return {
      avgFrameTime: this._frameCount > 0 ? this._frameTime / this._frameCount : 0,
      frameCount: this._frameCount,
      droppedFrames: this._droppedFrames,
      activeAnimations: this.animations.size
    };
  }
  // ─── Internal ─────────────────────────────────────────────────
  wake() {
    if (this.running) return;
    this.running = true;
    this._lastTimestamp = 0;
    this.rafId = requestAnimationFrame((t) => this.tick(t));
  }
  sleep() {
    this.running = false;
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }
  }
  tick(now) {
    if (!this.running) return;
    const t0 = performance.now();
    if (this._lastTimestamp > 0) {
      const gap = now - this._lastTimestamp;
      if (gap > 20) this._droppedFrames++;
    }
    this._lastTimestamp = now;
    const completed = [];
    const sorted = [...this.animations.values()].sort((a, b) => a.priority - b.priority);
    for (const anim of sorted) {
      const stillActive = anim.callback(now);
      if (!stillActive) {
        completed.push(anim.id);
      }
    }
    for (const id of completed) {
      this.animations.delete(id);
    }
    const dt = performance.now() - t0;
    this._frameTime += dt;
    this._frameCount++;
    if (this.animations.size > 0) {
      this.rafId = requestAnimationFrame((t) => this.tick(t));
    } else {
      this.sleep();
    }
  }
  /** Destroy the scheduler (cleanup) */
  destroy() {
    this.cancelAll();
    if (_instance === this) _instance = null;
  }
};
// Accessibility: prefers-reduced-motion. Cached + live-tracked. When the OS
// asks to reduce motion, animations are fast-forwarded to their resting
// state instead of playing (see settleImmediately). For users WITHOUT the
// preference this is never consulted — default motion is unchanged.
_AnimationScheduler._reduceMotion = null;
var AnimationScheduler = _AnimationScheduler;

// src/animations/morph.ts
var DEFAULT_MORPH_CONFIG = {
  spring: "liquidMerge",
  blendRadius: 20,
  useClipPath: true,
  attractDistance: 80,
  attractStrength: 0.6
};
var _LiquidMorph = class _LiquidMorph {
  constructor(element, config = {}) {
    // border-radius
    // SVG clip-path elements (for blob morphing)
    this.svgClip = null;
    this.animId = null;
    this.el = element;
    this.cfg = { ...DEFAULT_MORPH_CONFIG, ...config };
    this.scheduler = AnimationScheduler.shared();
    this.clipId = `ql-morph-${++_LiquidMorph._uid}`;
    const springCfg = this.cfg.spring;
    const rect = element.getBoundingClientRect();
    const radius = parseFloat(getComputedStyle(element).borderRadius) || 0;
    this.xSpring = new Spring(rect.left, springCfg);
    this.ySpring = new Spring(rect.top, springCfg);
    this.wSpring = new Spring(rect.width, springCfg);
    this.hSpring = new Spring(rect.height, springCfg);
    this.rSpring = new Spring(radius, springCfg);
    if (this.cfg.useClipPath) {
      this.setupClipPath();
    }
  }
  /**
   * Morph to a new shape with liquid animation.
   * The element will spring-animate to the target dimensions.
   */
  morphTo(target) {
    this.xSpring.setTarget(target.x);
    this.ySpring.setTarget(target.y);
    this.wSpring.setTarget(target.width);
    this.hSpring.setTarget(target.height);
    this.rSpring.setTarget(target.borderRadius);
    this.startAnimation();
  }
  /**
   * Morph to match another element's bounds.
   */
  morphToElement(target) {
    const rect = target.getBoundingClientRect();
    const radius = parseFloat(getComputedStyle(target).borderRadius) || 0;
    this.morphTo({
      x: rect.left,
      y: rect.top,
      width: rect.width,
      height: rect.height,
      borderRadius: radius
    });
  }
  /**
   * Animate a "liquid stretch" — element expands then snaps back.
   * Used for press feedback, notifications, etc.
   */
  liquidPulse(scaleX = 1.05, scaleY = 0.95) {
    const rect = this.el.getBoundingClientRect();
    const cx = rect.left + rect.width / 2;
    const cy = rect.top + rect.height / 2;
    const newW = rect.width * scaleX;
    const newH = rect.height * scaleY;
    this.wSpring.setValue(newW, 0);
    this.hSpring.setValue(newH, 0);
    this.xSpring.setValue(cx - newW / 2, 0);
    this.ySpring.setValue(cy - newH / 2, 0);
    this.wSpring.setTarget(rect.width);
    this.hSpring.setTarget(rect.height);
    this.xSpring.setTarget(rect.left);
    this.ySpring.setTarget(rect.top);
    this.startAnimation();
  }
  /**
   * Liquid "jiggle" — like tapping a water droplet.
   * Applies velocity impulse for organic wobble.
   */
  jiggle(intensity = 200) {
    this.wSpring.addVelocity(intensity);
    this.hSpring.addVelocity(-intensity * 0.7);
    this.rSpring.addVelocity(intensity * 0.3);
    this.startAnimation();
  }
  /** Get current progress (useful for blending animations) */
  get progress() {
    return 0;
  }
  // ─── Internal ─────────────────────────────────────────────────
  startAnimation() {
    if (this.animId !== null) return;
    this.animId = this.scheduler.schedule((now) => {
      const xActive = this.xSpring.tick(now);
      const yActive = this.ySpring.tick(now);
      const wActive = this.wSpring.tick(now);
      const hActive = this.hSpring.tick(now);
      const rActive = this.rSpring.tick(now);
      this.applyTransform();
      const active = xActive || yActive || wActive || hActive || rActive;
      if (!active) {
        this.animId = null;
      }
      return active;
    });
  }
  applyTransform() {
    const w = this.wSpring.value;
    const h = this.hSpring.value;
    const r = this.rSpring.value;
    const rect = this.el.getBoundingClientRect();
    const scaleX = w / (rect.width || 1);
    const scaleY = h / (rect.height || 1);
    this.el.style.transform = `scale(${scaleX}, ${scaleY})`;
    this.el.style.borderRadius = `${r}px`;
    if (this.cfg.useClipPath) {
      this.updateClipPath(w, h, r);
    }
  }
  setupClipPath() {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("width", "0");
    svg.setAttribute("height", "0");
    svg.style.position = "absolute";
    svg.style.pointerEvents = "none";
    svg.innerHTML = `
      <defs>
        <clipPath id="${this.clipId}" clipPathUnits="objectBoundingBox">
          <path d=""/>
        </clipPath>
      </defs>
    `;
    document.body.appendChild(svg);
    this.svgClip = svg;
    this.el.style.clipPath = `url(#${this.clipId})`;
  }
  updateClipPath(w, h, r) {
    if (!this.svgClip) return;
    const path = this.svgClip.querySelector("path");
    if (!path) return;
    const rx = Math.min(r / w, 0.5);
    const ry = Math.min(r / h, 0.5);
    path.setAttribute("d", roundedRectPath(rx, ry));
  }
  destroy() {
    if (this.animId !== null) {
      this.scheduler.cancel(this.animId);
    }
    if (this.svgClip) {
      this.svgClip.remove();
    }
    this.el.style.clipPath = "";
    this.el.style.transform = "";
  }
};
_LiquidMorph._uid = 0;
var LiquidMorph = _LiquidMorph;
function generateMergeBlob(shape1, shape2, blendRadius, resolution = 64) {
  const points = [];
  for (let i = 0; i < resolution; i++) {
    const angle = i / resolution * Math.PI * 2;
    const px = Math.cos(angle);
    const py = Math.sin(angle);
    let bestDist = Infinity;
    for (let t = 0; t < 200; t++) {
      const step = t * 0.01;
      const sx = px * step;
      const sy = py * step;
      const d1 = Math.sqrt((sx - shape1.cx) ** 2 + (sy - shape1.cy) ** 2) - shape1.r;
      const d2 = Math.sqrt((sx - shape2.cx) ** 2 + (sy - shape2.cy) ** 2) - shape2.r;
      const k = blendRadius;
      const h = Math.max(k - Math.abs(d1 - d2), 0) / k;
      const d = Math.min(d1, d2) - h * h * h * k * (1 / 6);
      if (Math.abs(d) < Math.abs(bestDist)) {
        bestDist = d;
        if (Math.abs(d) < 0.01) {
          points.push([sx, sy]);
          break;
        }
      }
      if (d > 0 && bestDist < 0) {
        points.push([sx, sy]);
        break;
      }
    }
  }
  if (points.length < 3) return "";
  return pointsToSmoothPath(points);
}
var LiquidMetaball = class {
  // pixel skip for performance
  constructor(container, blendRadius = 20) {
    this.blobs = [];
    this.animId = null;
    this.resolution = 2;
    this.container = container;
    this.blendRadius = blendRadius;
    this.scheduler = AnimationScheduler.shared();
    this.canvas = document.createElement("canvas");
    this.ctx = this.canvas.getContext("2d");
    Object.assign(this.canvas.style, {
      position: "absolute",
      inset: "0",
      width: "100%",
      height: "100%",
      pointerEvents: "none",
      zIndex: "10"
    });
    container.style.position = container.style.position || "relative";
    container.appendChild(this.canvas);
    this.resize();
  }
  addBlob(element) {
    const rect = element.getBoundingClientRect();
    const radius = Math.min(rect.width, rect.height) / 2;
    this.blobs.push({ el: element, radius });
    this.startRendering();
  }
  removeBlob(element) {
    this.blobs = this.blobs.filter((b) => b.el !== element);
    if (this.blobs.length === 0) this.stopRendering();
  }
  /** Update blend smoothness */
  setBlendRadius(r) {
    this.blendRadius = r;
  }
  resize() {
    const rect = this.container.getBoundingClientRect();
    this.canvas.width = rect.width / this.resolution;
    this.canvas.height = rect.height / this.resolution;
  }
  startRendering() {
    if (this.animId !== null) return;
    this.animId = this.scheduler.schedule(() => {
      this.render();
      return this.blobs.length > 1;
    });
  }
  stopRendering() {
    if (this.animId !== null) {
      this.scheduler.cancel(this.animId);
      this.animId = null;
    }
    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
  }
  render() {
    const w = this.canvas.width;
    const h = this.canvas.height;
    const img = this.ctx.createImageData(w, h);
    const data = img.data;
    const containerRect = this.container.getBoundingClientRect();
    const positions = this.blobs.map((b) => {
      const r = b.el.getBoundingClientRect();
      return {
        cx: (r.left + r.width / 2 - containerRect.left) / this.resolution,
        cy: (r.top + r.height / 2 - containerRect.top) / this.resolution,
        r: b.radius / this.resolution
      };
    });
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        let minDist = Infinity;
        for (const blob of positions) {
          const dx = x - blob.cx;
          const dy = y - blob.cy;
          const d = Math.sqrt(dx * dx + dy * dy) - blob.r;
          const k = this.blendRadius / this.resolution;
          const hh = Math.max(k - Math.abs(minDist - d), 0) / k;
          minDist = Math.min(minDist, d) - hh * hh * hh * k * (1 / 6);
        }
        const i = (y * w + x) * 4;
        if (minDist < 0) {
          const edge = 1 - smoothstep(-3, 0, minDist);
          data[i] = 255;
          data[i + 1] = 255;
          data[i + 2] = 255;
          data[i + 3] = Math.round(edge * 40);
        } else if (minDist < 1.5) {
          const rim = 1 - minDist / 1.5;
          data[i] = 255;
          data[i + 1] = 255;
          data[i + 2] = 255;
          data[i + 3] = Math.round(rim * 180);
        }
      }
    }
    this.ctx.putImageData(img, 0, 0);
  }
  destroy() {
    this.stopRendering();
    this.canvas.remove();
  }
};
function roundedRectPath(rx, ry) {
  const x = rx;
  const y = ry;
  const w = 1 - 2 * rx;
  const h = 1 - 2 * ry;
  return `M ${x} 0 L ${x + w} 0 Q 1 0 1 ${y} L 1 ${y + h} Q 1 1 ${x + w} 1 L ${x} 1 Q 0 1 0 ${y + h} L 0 ${y} Q 0 0 ${x} 0 Z`;
}
function pointsToSmoothPath(points) {
  if (points.length < 3) return "";
  let d = `M ${points[0][0]} ${points[0][1]}`;
  for (let i = 0; i < points.length; i++) {
    const curr = points[i];
    const next = points[(i + 1) % points.length];
    const nextNext = points[(i + 2) % points.length];
    const cp1x = curr[0] + (next[0] - points[(i - 1 + points.length) % points.length][0]) / 6;
    const cp1y = curr[1] + (next[1] - points[(i - 1 + points.length) % points.length][1]) / 6;
    const cp2x = next[0] - (nextNext[0] - curr[0]) / 6;
    const cp2y = next[1] - (nextNext[1] - curr[1]) / 6;
    d += ` C ${cp1x} ${cp1y}, ${cp2x} ${cp2y}, ${next[0]} ${next[1]}`;
  }
  return d + " Z";
}
function smoothstep(edge0, edge1, x) {
  const t = Math.max(0, Math.min(1, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
}

// src/animations/transitions.ts
var DEFAULT_TRANSITION_CONFIG = {
  spring: "default",
  propertySprings: {},
  autoApply: true
};
var LiquidTransition = class {
  constructor(element, config = {}) {
    this.springs = /* @__PURE__ */ new Map();
    this.animId = null;
    this.el = element;
    this.cfg = { ...DEFAULT_TRANSITION_CONFIG, ...config };
    this.scheduler = AnimationScheduler.shared();
    this._initialState = this.readCurrentState();
    this.state = { ...this._initialState };
  }
  /**
   * Animate to target state with spring physics.
   * Only specified properties are animated — others stay put.
   */
  to(target, spring) {
    for (const [key, value] of Object.entries(target)) {
      if (value === void 0) continue;
      let s = this.springs.get(key);
      if (!s) {
        const cfg = this.cfg.propertySprings[key] || spring || this.cfg.spring;
        s = new Spring(this.state[key], cfg);
        this.springs.set(key, s);
      } else if (spring) {
        const resolved = typeof spring === "string" ? SPRING_PRESETS[spring] : spring;
        s.updateConfig(resolved);
      }
      s.setTarget(value);
    }
    this.startAnimation();
    return this;
  }
  /**
   * Instantly set values (no animation) — useful during gestures.
   */
  set(values) {
    for (const [key, value] of Object.entries(values)) {
      if (value === void 0) continue;
      this.state[key] = value;
      const s = this.springs.get(key);
      if (s) s.setValue(value);
    }
    if (this.cfg.autoApply) this.applyState();
    return this;
  }
  /**
   * Add velocity to properties — used after gesture release (flick).
   * The element will continue moving with momentum then spring back.
   */
  release(velocities, target) {
    for (const [key, velocity] of Object.entries(velocities)) {
      if (velocity === void 0) continue;
      let s = this.springs.get(key);
      if (!s) {
        const cfg = this.cfg.propertySprings[key] || this.cfg.spring;
        s = new Spring(this.state[key], cfg);
        this.springs.set(key, s);
      }
      s.addVelocity(velocity);
      if (target && target[key] !== void 0) {
        s.setTarget(target[key]);
      }
    }
    this.startAnimation();
    return this;
  }
  /**
   * Spring back to initial state (e.g., after hover/press ends).
   */
  reset(spring) {
    return this.to(this._initialState, spring || "snappy");
  }
  /**
   * Get current animated state.
   */
  getState() {
    return this.state;
  }
  /** Whether any spring is still animating */
  get isAnimating() {
    return this.animId !== null;
  }
  // ─── Internal ─────────────────────────────────────────────────
  startAnimation() {
    if (this.animId !== null) return;
    this.animId = this.scheduler.schedule((now) => {
      let anyActive = false;
      for (const [key, spring] of this.springs) {
        if (spring.tick(now)) {
          anyActive = true;
        }
        this.state[key] = spring.value;
      }
      if (this.cfg.autoApply) this.applyState();
      if (this.cfg.onUpdate) this.cfg.onUpdate(this.state);
      if (!anyActive) {
        this.animId = null;
        if (this.cfg.onComplete) this.cfg.onComplete();
      }
      return anyActive;
    });
  }
  applyState() {
    const s = this.state;
    const transforms = [];
    if (s.x !== 0 || s.y !== 0) transforms.push(`translate3d(${s.x}px, ${s.y}px, 0)`);
    if (s.scale !== 1) transforms.push(`scale(${s.scale})`);
    else if (s.scaleX !== 1 || s.scaleY !== 1) transforms.push(`scale(${s.scaleX}, ${s.scaleY})`);
    if (s.rotate !== 0) transforms.push(`rotate(${s.rotate}deg)`);
    this.el.style.transform = transforms.join(" ") || "none";
    if (s.opacity !== 1) {
      this.el.style.opacity = String(s.opacity);
    } else {
      this.el.style.opacity = "";
    }
    if (s.borderRadius > 0) {
      this.el.style.borderRadius = `${s.borderRadius}px`;
    }
  }
  readCurrentState() {
    const computed = getComputedStyle(this.el);
    const matrix = new DOMMatrix(computed.transform);
    return {
      x: matrix.e || 0,
      y: matrix.f || 0,
      scale: Math.sqrt(matrix.a * matrix.a + matrix.b * matrix.b) || 1,
      scaleX: matrix.a || 1,
      scaleY: matrix.d || 1,
      rotate: Math.atan2(matrix.b, matrix.a) * (180 / Math.PI) || 0,
      opacity: parseFloat(computed.opacity) || 1,
      borderRadius: parseFloat(computed.borderRadius) || 0,
      blur: 0,
      rimIntensity: 0
    };
  }
  destroy() {
    if (this.animId !== null) {
      this.scheduler.cancel(this.animId);
      this.animId = null;
    }
    this.springs.clear();
    this.el.style.transform = "";
    this.el.style.opacity = "";
  }
};
var LiquidLayoutAnimation = class {
  constructor() {
    this.animations = /* @__PURE__ */ new Map();
  }
  /**
   * Capture current positions of elements before a layout change.
   * Call this BEFORE modifying the DOM/layout.
   */
  capturePositions(elements) {
    const positions = /* @__PURE__ */ new Map();
    for (const el of elements) {
      positions.set(el, el.getBoundingClientRect());
    }
    return positions;
  }
  /**
   * Animate from captured positions to current positions.
   * Call this AFTER the DOM/layout change.
   * 
   * Uses FLIP (First, Last, Invert, Play) technique:
   * - Elements instantly appear at final position
   * - Transform offsets are applied to "fake" the old position
   * - Springs animate the offset back to zero
   */
  animateFromPositions(elements, previousPositions, spring = "default") {
    for (const el of elements) {
      const prev = previousPositions.get(el);
      if (!prev) continue;
      const curr = el.getBoundingClientRect();
      const dx = prev.left - curr.left;
      const dy = prev.top - curr.top;
      const sx = prev.width / curr.width;
      const sy = prev.height / curr.height;
      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5 && Math.abs(sx - 1) < 0.01 && Math.abs(sy - 1) < 0.01) {
        continue;
      }
      let lt = this.animations.get(el);
      if (!lt) {
        lt = new LiquidTransition(el);
        this.animations.set(el, lt);
      }
      lt.set({ x: dx, y: dy, scaleX: sx, scaleY: sy });
      lt.to({ x: 0, y: 0, scaleX: 1, scaleY: 1 }, spring);
    }
  }
  /**
   * Convenience: wrap a callback that changes layout.
   * Automatically captures before and animates after.
   */
  animate(elements, layoutChange, spring) {
    const positions = this.capturePositions(elements);
    layoutChange();
    requestAnimationFrame(() => {
      this.animateFromPositions(elements, positions, spring);
    });
  }
  destroy() {
    for (const lt of this.animations.values()) {
      lt.destroy();
    }
    this.animations.clear();
  }
};

// src/animations/gestures.ts
var DEFAULT_GESTURE_CONFIG = {
  dragSpring: "snappy",
  releaseSpring: "bouncy",
  pressScale: 0.95,
  pressSquish: 0.02,
  flickMultiplier: 1.2,
  maxDragDistance: Infinity,
  rubberBandFactor: 0.3,
  wobbleOnPress: true,
  scaleOnPress: true,
  velocityWindow: 100
};
var LiquidGesture = class {
  constructor(element, config = {}) {
    // Gesture state
    this.pressed = false;
    this.dragging = false;
    this.startX = 0;
    this.startY = 0;
    this.velocityHistory = [];
    // Pointer tracking
    this.pointerId = null;
    this.pressTimer = null;
    this.handlePointerDown = (e) => {
      if (this.pointerId !== null) return;
      this.pointerId = e.pointerId;
      this.el.setPointerCapture(e.pointerId);
      this.pressed = true;
      this.dragging = false;
      this.startX = e.clientX;
      this.startY = e.clientY;
      this.velocityHistory = [{ x: e.clientX, y: e.clientY, t: performance.now() }];
      if (this.cfg.scaleOnPress) {
        this.transition.to({
          scale: this.cfg.pressScale,
          scaleX: 1 + this.cfg.pressSquish,
          scaleY: 1 - this.cfg.pressSquish
        }, "stiff");
      }
      if (this.cfg.wobbleOnPress) {
        this.pressTimer = window.setTimeout(() => {
          if (this.pressed && !this.dragging) {
            this.transition.to({
              scaleX: 1 - this.cfg.pressSquish * 0.5,
              scaleY: 1 + this.cfg.pressSquish * 0.5
            }, "gentle");
          }
        }, 150);
      }
      this._onPress?.();
    };
    this.handlePointerMove = (e) => {
      if (e.pointerId !== this.pointerId) return;
      const dx = e.clientX - this.startX;
      const dy = e.clientY - this.startY;
      if (!this.dragging && (Math.abs(dx) > 5 || Math.abs(dy) > 5)) {
        this.dragging = true;
        this.transition.to({ scale: 1, scaleX: 1, scaleY: 1 }, "snappy");
        this._onDragStart?.(this.startX, this.startY);
      }
      if (this.dragging) {
        const dist = Math.sqrt(dx * dx + dy * dy);
        let effectiveX = dx;
        let effectiveY = dy;
        if (dist > this.cfg.maxDragDistance) {
          const excess = dist - this.cfg.maxDragDistance;
          const dampedExcess = excess * this.cfg.rubberBandFactor;
          const ratio = (this.cfg.maxDragDistance + dampedExcess) / dist;
          effectiveX = dx * ratio;
          effectiveY = dy * ratio;
        }
        const now = performance.now();
        this.velocityHistory.push({ x: e.clientX, y: e.clientY, t: now });
        while (this.velocityHistory.length > 0 && now - this.velocityHistory[0].t > this.cfg.velocityWindow) {
          this.velocityHistory.shift();
        }
        this.transition.set({ x: effectiveX, y: effectiveY });
        const [vx, vy] = this.computeVelocity();
        this._onDrag?.(effectiveX, effectiveY, vx, vy);
      }
    };
    this.handlePointerUp = (e) => {
      if (e.pointerId !== this.pointerId) return;
      this.el.releasePointerCapture(e.pointerId);
      this.pointerId = null;
      if (this.pressTimer) {
        clearTimeout(this.pressTimer);
        this.pressTimer = null;
      }
      const wasDragging = this.dragging;
      const [vx, vy] = this.computeVelocity();
      this.pressed = false;
      this.dragging = false;
      if (wasDragging) {
        this.transition.release(
          { x: vx * this.cfg.flickMultiplier, y: vy * this.cfg.flickMultiplier },
          { x: 0, y: 0, scale: 1, scaleX: 1, scaleY: 1 }
        );
        this._onDragEnd?.(vx, vy);
      } else {
        this.transition.to(
          { scale: 1, scaleX: 1, scaleY: 1 },
          "bouncy"
        );
        this._onTap?.();
      }
      this._onRelease?.();
    };
    this.el = element;
    this.cfg = { ...DEFAULT_GESTURE_CONFIG, ...config };
    this.transition = new LiquidTransition(element, {
      spring: this.cfg.dragSpring,
      propertySprings: {
        scale: this.cfg.releaseSpring,
        scaleX: this.cfg.releaseSpring,
        scaleY: this.cfg.releaseSpring
      }
    });
    this.bindEvents();
  }
  // ─── Event Callbacks ──────────────────────────────────────────
  onDragStart(cb) {
    this._onDragStart = cb;
    return this;
  }
  onDrag(cb) {
    this._onDrag = cb;
    return this;
  }
  onDragEnd(cb) {
    this._onDragEnd = cb;
    return this;
  }
  onPress(cb) {
    this._onPress = cb;
    return this;
  }
  onRelease(cb) {
    this._onRelease = cb;
    return this;
  }
  onTap(cb) {
    this._onTap = cb;
    return this;
  }
  // ─── Internal ─────────────────────────────────────────────────
  bindEvents() {
    const el = this.el;
    el.style.touchAction = "none";
    el.style.userSelect = "none";
    el.style.webkitUserSelect = "none";
    el.addEventListener("pointerdown", this.handlePointerDown, { passive: false });
    el.addEventListener("pointermove", this.handlePointerMove, { passive: true });
    el.addEventListener("pointerup", this.handlePointerUp, { passive: true });
    el.addEventListener("pointercancel", this.handlePointerUp, { passive: true });
  }
  computeVelocity() {
    if (this.velocityHistory.length < 2) return [0, 0];
    const recent = this.velocityHistory;
    const first = recent[0];
    const last = recent[recent.length - 1];
    const dt = (last.t - first.t) / 1e3;
    if (dt < 1e-3) return [0, 0];
    return [
      (last.x - first.x) / dt,
      (last.y - first.y) / dt
    ];
  }
  /** Get the underlying transition (for custom animation control) */
  getTransition() {
    return this.transition;
  }
  destroy() {
    this.el.removeEventListener("pointerdown", this.handlePointerDown);
    this.el.removeEventListener("pointermove", this.handlePointerMove);
    this.el.removeEventListener("pointerup", this.handlePointerUp);
    this.el.removeEventListener("pointercancel", this.handlePointerUp);
    if (this.pressTimer) clearTimeout(this.pressTimer);
    if (this.pointerId !== null) {
      this.el.releasePointerCapture(this.pointerId);
    }
    this.transition.destroy();
  }
};
var LiquidButton = class {
  constructor(element, config) {
    this.gesture = new LiquidGesture(element, {
      pressScale: 0.92,
      pressSquish: 0.03,
      scaleOnPress: true,
      wobbleOnPress: true,
      maxDragDistance: 30,
      rubberBandFactor: 0.1,
      releaseSpring: "bouncy",
      ...config
    });
  }
  onTap(cb) {
    this.gesture.onTap(cb);
    return this;
  }
  destroy() {
    this.gesture.destroy();
  }
};
var LiquidDrag = class {
  constructor(element, config) {
    this.gesture = new LiquidGesture(element, {
      pressScale: 1.03,
      // Slightly enlarge when grabbed
      pressSquish: 0,
      scaleOnPress: true,
      wobbleOnPress: false,
      maxDragDistance: Infinity,
      rubberBandFactor: 1,
      flickMultiplier: 0.8,
      releaseSpring: "default",
      ...config
    });
  }
  /** Set snap points — element springs to nearest on release */
  setSnapTargets(_targets) {
    return this;
  }
  onSnap(_cb) {
    return this;
  }
  onDrag(cb) {
    this.gesture.onDrag(cb);
    return this;
  }
  destroy() {
    this.gesture.destroy();
  }
};

// src/animations/group.ts
var DEFAULT_GROUP_CONFIG = {
  mergeDistance: 60,
  blendRadius: 24,
  spring: "liquidMerge",
  resolution: 3,
  renderBlob: true,
  bridgeOpacity: 0.15,
  bridgeColor: "rgba(255, 255, 255, 0.08)",
  bridgeRimColor: "rgba(255, 255, 255, 0.5)",
  bridgeRimWidth: 1.5,
  onMerge: void 0,
  onSplit: void 0
};
var LiquidGroup = class {
  constructor(container, config = {}) {
    this.members = /* @__PURE__ */ new Map();
    this.animId = null;
    this.needsUpdate = false;
    this.observer = null;
    this.resizeObserver = null;
    // Merge tracking
    this.mergedPairs = /* @__PURE__ */ new Set();
    this.container = container;
    this.cfg = { ...DEFAULT_GROUP_CONFIG, ...config };
    this.scheduler = AnimationScheduler.shared();
    this.blendSpring = new Spring(0, this.cfg.spring);
    this.canvas = document.createElement("canvas");
    this.ctx = this.canvas.getContext("2d");
    this.rimCanvas = document.createElement("canvas");
    this.rimCtx = this.rimCanvas.getContext("2d");
    if (this.cfg.renderBlob) {
      this.setupRenderLayer();
    }
    this.setupObservers();
  }
  /**
   * Add an element to the liquid group.
   * It will now participate in merge/split animations with other members.
   */
  add(element) {
    const rect = element.getBoundingClientRect();
    const containerRect = this.container.getBoundingClientRect();
    const radius = parseFloat(getComputedStyle(element).borderRadius) || 0;
    this.members.set(element, {
      el: element,
      cx: rect.left + rect.width / 2 - containerRect.left,
      cy: rect.top + rect.height / 2 - containerRect.top,
      rx: rect.width / 2,
      ry: rect.height / 2,
      radius: Math.min(radius, rect.width / 2, rect.height / 2),
      merged: /* @__PURE__ */ new Set()
    });
    this.needsUpdate = true;
    this.startAnimation();
  }
  /**
   * Remove an element from the group.
   */
  remove(element) {
    this.members.delete(element);
    this.needsUpdate = true;
    for (const key of this.mergedPairs) {
      if (key.includes(String(element))) {
        this.mergedPairs.delete(key);
      }
    }
  }
  /**
   * Force update positions (call after layout changes).
   */
  updatePositions() {
    const containerRect = this.container.getBoundingClientRect();
    for (const [el, info] of this.members) {
      const rect = el.getBoundingClientRect();
      info.cx = rect.left + rect.width / 2 - containerRect.left;
      info.cy = rect.top + rect.height / 2 - containerRect.top;
      info.rx = rect.width / 2;
      info.ry = rect.height / 2;
      info.radius = Math.min(
        parseFloat(getComputedStyle(el).borderRadius) || 0,
        rect.width / 2,
        rect.height / 2
      );
    }
    this.needsUpdate = true;
    this.startAnimation();
  }
  /**
   * Manually trigger a merge animation between specific elements.
   */
  merge(_elements, spring) {
    this.blendSpring = new Spring(0, spring || this.cfg.spring);
    this.blendSpring.setTarget(1);
    this.needsUpdate = true;
    this.startAnimation();
  }
  /**
   * Manually trigger a split animation.
   */
  split(spring) {
    this.blendSpring = new Spring(1, spring || "liquidSplit");
    this.blendSpring.setTarget(0);
    this.needsUpdate = true;
    this.startAnimation();
  }
  // ─── Internal ─────────────────────────────────────────────────
  setupRenderLayer() {
    Object.assign(this.canvas.style, {
      position: "absolute",
      inset: "0",
      width: "100%",
      height: "100%",
      pointerEvents: "none",
      zIndex: "5",
      opacity: String(this.cfg.bridgeOpacity),
      mixBlendMode: "screen"
    });
    Object.assign(this.rimCanvas.style, {
      position: "absolute",
      inset: "0",
      width: "100%",
      height: "100%",
      pointerEvents: "none",
      zIndex: "6"
    });
    this.container.style.position = this.container.style.position || "relative";
    this.container.appendChild(this.canvas);
    this.container.appendChild(this.rimCanvas);
    this.resizeCanvas();
  }
  resizeCanvas() {
    const rect = this.container.getBoundingClientRect();
    const res = this.cfg.resolution;
    this.canvas.width = Math.ceil(rect.width / res);
    this.canvas.height = Math.ceil(rect.height / res);
    this.rimCanvas.width = Math.ceil(rect.width / res);
    this.rimCanvas.height = Math.ceil(rect.height / res);
  }
  setupObservers() {
    this.observer = new MutationObserver(() => {
      this.updatePositions();
    });
    this.observer.observe(this.container, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["style", "class"]
    });
    this.resizeObserver = new ResizeObserver(() => {
      this.resizeCanvas();
      this.updatePositions();
    });
    this.resizeObserver.observe(this.container);
  }
  startAnimation() {
    if (this.animId !== null) return;
    this.animId = this.scheduler.schedule((now) => {
      this.updatePositions();
      const springActive = this.blendSpring.tick(now);
      if (this.cfg.renderBlob) {
        this.renderMergeField();
      }
      this.detectMerges();
      const hasActivity = springActive || this.needsUpdate;
      this.needsUpdate = false;
      if (!hasActivity) {
        this.animId = null;
      }
      return hasActivity;
    });
  }
  renderMergeField() {
    const w = this.canvas.width;
    const h = this.canvas.height;
    const res = this.cfg.resolution;
    const members = [...this.members.values()];
    if (members.length < 2) {
      this.ctx.clearRect(0, 0, w, h);
      this.rimCtx.clearRect(0, 0, w, h);
      return;
    }
    const img = this.ctx.createImageData(w, h);
    const data = img.data;
    const rimImg = this.rimCtx.createImageData(w, h);
    const rimData = rimImg.data;
    const blendK = this.cfg.blendRadius / res;
    for (let py = 0; py < h; py++) {
      for (let px = 0; px < w; px++) {
        let mergedDist = Infinity;
        for (const member of members) {
          const cx = member.cx / res;
          const cy = member.cy / res;
          const rx = member.rx / res;
          const ry = member.ry / res;
          const r = member.radius / res;
          const dx = Math.abs(px - cx) - (rx - r);
          const dy = Math.abs(py - cy) - (ry - r);
          const outsideDist = Math.sqrt(Math.max(dx, 0) ** 2 + Math.max(dy, 0) ** 2) - r;
          const d = Math.min(Math.max(dx, dy), outsideDist);
          if (mergedDist === Infinity) {
            mergedDist = d;
          } else {
            const hh = Math.max(blendK - Math.abs(mergedDist - d), 0) / blendK;
            mergedDist = Math.min(mergedDist, d) - hh * hh * hh * blendK * (1 / 6);
          }
        }
        const i = (py * w + px) * 4;
        let insideSingle = false;
        for (const member of members) {
          const cx = member.cx / res;
          const cy = member.cy / res;
          const rx = member.rx / res;
          const ry = member.ry / res;
          const r = member.radius / res;
          const dx = Math.abs(px - cx) - (rx - r);
          const dy = Math.abs(py - cy) - (ry - r);
          const outsideDist = Math.sqrt(Math.max(dx, 0) ** 2 + Math.max(dy, 0) ** 2) - r;
          const d = Math.min(Math.max(dx, dy), outsideDist);
          if (d < -2) {
            insideSingle = true;
            break;
          }
        }
        if (insideSingle) continue;
        if (mergedDist < 0) {
          const intensity = Math.min(1, -mergedDist / 3);
          data[i] = 255;
          data[i + 1] = 255;
          data[i + 2] = 255;
          data[i + 3] = Math.round(intensity * 60);
        }
        if (Math.abs(mergedDist) < this.cfg.bridgeRimWidth) {
          const rimIntensity = 1 - Math.abs(mergedDist) / this.cfg.bridgeRimWidth;
          rimData[i] = 255;
          rimData[i + 1] = 255;
          rimData[i + 2] = 255;
          rimData[i + 3] = Math.round(rimIntensity * 200);
        }
      }
    }
    this.ctx.putImageData(img, 0, 0);
    this.rimCtx.putImageData(rimImg, 0, 0);
  }
  detectMerges() {
    const members = [...this.members.values()];
    for (let i = 0; i < members.length; i++) {
      for (let j = i + 1; j < members.length; j++) {
        const a = members[i];
        const b = members[j];
        const dx = a.cx - b.cx;
        const dy = a.cy - b.cy;
        const centerDist = Math.sqrt(dx * dx + dy * dy);
        const edgeDist = centerDist - a.rx - b.rx;
        const pairKey = `${i}-${j}`;
        const wasMerged = this.mergedPairs.has(pairKey);
        const shouldMerge = edgeDist < this.cfg.mergeDistance;
        if (shouldMerge && !wasMerged) {
          this.mergedPairs.add(pairKey);
          a.merged.add(b.el);
          b.merged.add(a.el);
          this.cfg.onMerge?.([a.el, b.el]);
        } else if (!shouldMerge && wasMerged) {
          this.mergedPairs.delete(pairKey);
          a.merged.delete(b.el);
          b.merged.delete(a.el);
          this.cfg.onSplit?.([a.el, b.el]);
        }
      }
    }
  }
  /** Get elements currently merged with a given element */
  getMergedWith(element) {
    const info = this.members.get(element);
    return info ? [...info.merged] : [];
  }
  /** Check if two elements are currently merged */
  areMerged(a, b) {
    const info = this.members.get(a);
    return info?.merged.has(b) ?? false;
  }
  destroy() {
    if (this.animId !== null) {
      this.scheduler.cancel(this.animId);
    }
    if (this.observer) this.observer.disconnect();
    if (this.resizeObserver) this.resizeObserver.disconnect();
    this.canvas.remove();
    this.rimCanvas.remove();
    this.members.clear();
    this.mergedPairs.clear();
  }
};
var LiquidTabBar = class {
  constructor(container, items, config) {
    this.currentIndex = 0;
    this.animId = null;
    this.container = container;
    this.items = items;
    this.scheduler = AnimationScheduler.shared();
    this.spring = new Spring(0, config?.spring || "default");
    this.widthSpring = new Spring(0, config?.spring || "snappy");
    this.indicator = document.createElement("div");
    this.indicator.className = "ql-tab-indicator";
    Object.assign(this.indicator.style, {
      position: "absolute",
      top: "0",
      height: "100%",
      borderRadius: "inherit",
      pointerEvents: "none",
      transition: "none"
      // We handle animation
    });
    container.style.position = container.style.position || "relative";
    container.insertBefore(this.indicator, container.firstChild);
    if (items.length > 0) {
      this.selectImmediate(0);
    }
  }
  /**
   * Select a tab — the indicator morphs to it with liquid animation.
   * 
   * The stretch effect: indicator first expands to cover the gap
   * between old and new position, then contracts to the new tab.
   */
  select(index) {
    if (index === this.currentIndex) return;
    if (index < 0 || index >= this.items.length) return;
    const prevRect = this.items[this.currentIndex].getBoundingClientRect();
    const nextRect = this.items[index].getBoundingClientRect();
    const containerRect = this.container.getBoundingClientRect();
    const prevX = prevRect.left - containerRect.left;
    const nextX = nextRect.left - containerRect.left;
    const stretchLeft = Math.min(prevX, nextX);
    const stretchRight = Math.max(prevX + prevRect.width, nextX + nextRect.width);
    const stretchWidth = stretchRight - stretchLeft;
    this.currentIndex = index;
    this.spring.setTarget(nextX);
    this.widthSpring.setValue(stretchWidth, 0);
    this.widthSpring.setTarget(nextRect.width);
    this.startAnimation();
  }
  /** Set tab without animation */
  selectImmediate(index) {
    this.currentIndex = index;
    const rect = this.items[index].getBoundingClientRect();
    const containerRect = this.container.getBoundingClientRect();
    const x = rect.left - containerRect.left;
    this.spring.setValue(x);
    this.spring.setTarget(x);
    this.widthSpring.setValue(rect.width);
    this.widthSpring.setTarget(rect.width);
    this.indicator.style.transform = `translateX(${x}px)`;
    this.indicator.style.width = `${rect.width}px`;
  }
  /** Get indicator element (to apply liquid glass effect to it) */
  getIndicator() {
    return this.indicator;
  }
  startAnimation() {
    if (this.animId !== null) return;
    this.animId = this.scheduler.schedule((now) => {
      const posActive = this.spring.tick(now);
      const widthActive = this.widthSpring.tick(now);
      this.indicator.style.transform = `translateX(${this.spring.value}px)`;
      this.indicator.style.width = `${this.widthSpring.value}px`;
      const active = posActive || widthActive;
      if (!active) this.animId = null;
      return active;
    });
  }
  destroy() {
    if (this.animId !== null) {
      this.scheduler.cancel(this.animId);
    }
    this.indicator.remove();
  }
};
export {
  AnimationScheduler,
  DEFAULT_CONFIG,
  LiquidButton,
  LiquidDrag,
  LiquidGesture,
  LiquidGlassEngine,
  LiquidGroup,
  LiquidLayoutAnimation,
  LiquidMetaball,
  LiquidMorph,
  LiquidTabBar,
  LiquidTransition,
  MATERIAL_PRESETS,
  SPRING_PRESETS,
  Spring,
  SpringVector,
  generateMergeBlob
};
//# sourceMappingURL=index.mjs.map