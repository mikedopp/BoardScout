// src/core/config.ts
var DEFAULT_CONFIG = {
  blur: 3,
  saturation: 1.5,
  tint: "255, 255, 255",
  tintOpacity: 0.04,
  refractionStrength: 22,
  bezelWidth: 34,
  thickness: 24,
  ior: 1.5,
  chromaticAberration: 0.18,
  lightAngle: -35,
  edgeHighlight: 0.9,
  specularStrength: 0.26,
  fresnelPower: 2.2,
  // Off by default: a hover-triggered rim brightening reads as a broken
  // hover state in practice — light shouldn't change because a cursor
  // entered the element. Opt-in only.
  hoverLighting: false,
  cursorTracking: false,
  parallax: false,
  inertia: true,
  dynamicLighting: false,
  elevation: 1,
  noiseOpacity: 0,
  noiseScale: 1,
  borderRadius: 28,
  quality: "high",
  refractionMode: "auto",
  appearance: "auto",
  tintStrength: 1,
  respectPreferences: true,
  dispersionMode: "auto"
};
var MATERIAL_PRESETS = {
  // Inspired by clear Liquid Glass — transparent, strong lensing
  clear: { blur: 2, saturation: 1.55, tintOpacity: 0.03, refractionStrength: 26, bezelWidth: 36, thickness: 26 },
  thin: { blur: 6, saturation: 1.5, tintOpacity: 0.05, refractionStrength: 18, bezelWidth: 26, thickness: 18 },
  // Inspired by regular Liquid Glass — frosted, softer lensing
  regular: { blur: 14, saturation: 1.7, tintOpacity: 0.09, refractionStrength: 16, bezelWidth: 30, thickness: 20 },
  thick: { blur: 22, saturation: 1.8, tintOpacity: 0.13, refractionStrength: 12, bezelWidth: 28, thickness: 18 },
  ultra: { blur: 30, saturation: 1.85, tintOpacity: 0.17, refractionStrength: 10, bezelWidth: 26, thickness: 16 },
  adaptive: { blur: 12, saturation: 1.6, tintOpacity: 0.06, refractionStrength: 18, bezelWidth: 30, thickness: 20, adaptiveTint: true }
};
function resolveGlassConfig(input) {
  const config = Object.fromEntries(Object.entries(input).filter(([, v]) => v !== void 0));
  const preset = config.material ? MATERIAL_PRESETS[config.material] : void 0;
  const resolved = { ...DEFAULT_CONFIG, ...preset, ...config };
  if (config.distortionStrength !== void 0 && config.refractionStrength === void 0) resolved.refractionStrength = config.distortionStrength;
  if (config.dynamicLighting && config.cursorTracking === void 0) resolved.cursorTracking = true;
  const bounds = {
    blur: [0, 80],
    saturation: [0, 4],
    tintOpacity: [0, 1],
    refractionStrength: [0, 150],
    bezelWidth: [0.5, 256],
    thickness: [0.1, 256],
    ior: [1, 3],
    chromaticAberration: [0, 1],
    edgeHighlight: [0, 1],
    specularStrength: [0, 1],
    fresnelPower: [1, 6],
    elevation: [0, 5],
    noiseOpacity: [0, 1],
    noiseScale: [0.1, 10],
    borderRadius: [0, 1e4],
    backdropLuminance: [0, 1],
    tintStrength: [0, 5]
  };
  for (const [key, range] of Object.entries(bounds)) {
    const k = key;
    const value = resolved[k];
    if (value === void 0) continue;
    const [lo, hi] = range;
    resolved[key] = typeof value === "number" && Number.isFinite(value) ? Math.max(lo, Math.min(hi, value)) : DEFAULT_CONFIG[k];
  }
  if (!Number.isFinite(resolved.lightAngle)) resolved.lightAngle = DEFAULT_CONFIG.lightAngle;
  return resolved;
}

// src/core/optics.ts
var PROFILE_SAMPLES = 512;
var profiles = /* @__PURE__ */ new Map();
function refractionProfile(thickness, bezel, ior) {
  const ratio = thickness / Math.max(bezel, 1e-3);
  const key = `${ratio}|${ior}`;
  const hit = profiles.get(key);
  if (hit) {
    profiles.delete(key);
    profiles.set(key, hit);
    return hit;
  }
  const lut = new Float32Array(PROFILE_SAMPLES + 1);
  if (ior <= 1 || thickness <= 0) return lut;
  const eta = 1 / Math.max(1, ior);
  let peak = 0;
  for (let i = 1; i < PROFILE_SAMPLES; i++) {
    const s = i / PROFILE_SAMPLES;
    const root = Math.sqrt(s * (2 - s));
    const slope = ratio * (1 - s) / root;
    const cosI = 1 / Math.sqrt(1 + slope * slope);
    const sinI = slope * cosI;
    const k = eta * cosI - Math.sqrt(Math.max(0, 1 - eta * eta * sinI * sinI));
    const displacement = root * Math.abs(k * sinI / (-eta + k * cosI));
    lut[i] = displacement;
    peak = Math.max(peak, displacement);
  }
  if (peak > 0) for (let i = 1; i < PROFILE_SAMPLES; i++) lut[i] /= peak;
  if (profiles.size >= 32) profiles.delete(profiles.keys().next().value);
  profiles.set(key, lut);
  return lut;
}
function rasterizeLens(g) {
  const { width: w, height: h, mapWidth: mw, mapHeight: mh } = g;
  const sx = mw / w, sy = mh / h;
  const padX = Math.ceil(g.padding * sx), padY = Math.ceil(g.padding * sy);
  const bw = mw + 2 * padX, bh = mh + 2 * padY;
  const data = new Uint8ClampedArray(bw * bh * 4);
  const littleEndian = new Uint8Array(new Uint32Array([1]).buffer)[0] === 1;
  new Uint32Array(data.buffer).fill(littleEndian ? 4278222976 : 2155872511);
  const radius = Math.min(g.radius, w / 2, h / 2);
  const bezel = Math.min(g.bezel, w / 2, h / 2);
  const profile = refractionProfile(g.thickness, bezel, g.ior);
  const band = Math.max(radius, bezel) + 1 / Math.min(sx, sy);
  const qw = Math.ceil(mw / 2), qh = Math.ceil(mh / 2);
  let pixelsComputed = 0;
  const put = (x, y, dx, dy) => {
    const i = ((y + padY) * bw + x + padX) * 4;
    data[i] = 128 + dx;
    data[i + 1] = 128 + dy;
  };
  for (let y = 0; y < qh; y++) {
    const ey = (y + 0.5) / sy;
    const limit = ey <= bezel ? qw : Math.min(qw, Math.ceil((ey <= band ? band : bezel + 1 / sx) * sx));
    for (let x = 0; x < limit; x++) {
      const ex = (x + 0.5) / sx;
      const qx = radius - ex, qy = radius - ey;
      let distance, nx, ny;
      if (qx > 0 && qy > 0) {
        const length = Math.hypot(qx, qy);
        distance = radius - length;
        nx = qx / length;
        ny = qy / length;
      } else if (qx > qy) {
        distance = ex;
        nx = 1;
        ny = 0;
      } else {
        distance = ey;
        nx = 0;
        ny = 1;
      }
      if (distance <= 0 || distance >= bezel) continue;
      pixelsComputed++;
      const f = distance / bezel * PROFILE_SAMPLES;
      const i = Math.min(PROFILE_SAMPLES - 1, Math.floor(f));
      const displacement = profile[i] + (profile[i + 1] - profile[i]) * (f - i);
      const dx = x === mw - 1 - x ? 0 : Math.round(nx * displacement * 127);
      const dy = y === mh - 1 - y ? 0 : Math.round(ny * displacement * 127);
      put(x, y, dx, dy);
      put(mw - 1 - x, y, -dx, dy);
      put(x, mh - 1 - y, dx, -dy);
      put(mw - 1 - x, mh - 1 - y, -dx, -dy);
    }
  }
  return { data, width: bw, height: bh, padX, padY, mapWidth: mw, mapHeight: mh, pixelsComputed };
}
function usesDispersion(strength, chroma, blur, quality, mode = "auto") {
  if (chroma <= 0.01 || quality === "low") return false;
  return mode === "exact" || 0.24 * chroma * strength >= 0.2 * blur;
}
function filterPadding(blur) {
  return Math.ceil((2.5 * blur + 2) / 8) * 8;
}

// src/core/engine.ts
var DARK_TINT = "20, 24, 34";
var LUMA_LIGHT = 0.8;
var LUMA_DARK = 0.1;
var uid = 0;
var mapCache = /* @__PURE__ */ new Map();
var cacheHits = 0;
var mapsGenerated = 0;
function releaseMap(key) {
  const entry = mapCache.get(key);
  if (!entry) return;
  entry.refs--;
  if (entry.refs === 0 && entry.map) {
    URL.revokeObjectURL(entry.map.url);
    mapCache.delete(key);
  }
}
function acquireLensMap(w, h, radius, bezel, thickness, ior, resCap, blur) {
  const rho = Math.min(1, resCap / Math.max(w, h));
  const mw = Math.max(4, Math.round(w * rho)), mh = Math.max(4, Math.round(h * rho));
  const padding = filterPadding(blur);
  const key = [w, h, mw, mh, radius, bezel, thickness, ior, padding].join("|");
  let entry = mapCache.get(key);
  if (entry) {
    entry.refs++;
    cacheHits++;
  } else {
    const t0 = performance.now();
    const raster = rasterizeLens({ width: w, height: h, radius, bezel, thickness, ior, mapWidth: mw, mapHeight: mh, padding });
    const canvas = document.createElement("canvas");
    canvas.width = raster.width;
    canvas.height = raster.height;
    const ctx = canvas.getContext("2d");
    if (!ctx) return Promise.reject(new Error("Canvas 2D unavailable"));
    ctx.putImageData(new ImageData(raster.data, raster.width, raster.height), 0, 0);
    const genMs = performance.now() - t0, encodeStart = performance.now();
    mapsGenerated++;
    entry = { refs: 1, promise: new Promise((resolve, reject) => {
      canvas.toBlob((blob) => {
        if (!blob) {
          reject(new Error("Unable to encode refraction map"));
          return;
        }
        const { data: _data, ...dimensions } = raster;
        resolve({ ...dimensions, url: URL.createObjectURL(blob), genMs, encodeMs: performance.now() - encodeStart });
      }, "image/png");
    }) };
    const owned = entry;
    entry.promise = entry.promise.then((map) => {
      owned.map = map;
      return map;
    }, (error) => {
      if (mapCache.get(key) === owned) mapCache.delete(key);
      throw error;
    });
    mapCache.set(key, entry);
  }
  return entry.promise.then((map) => ({ map, key }));
}
var _LiquidGlassEngine = class _LiquidGlassEngine {
  constructor(element, config = {}) {
    this.ownedContent = null;
    this.rimDisk = null;
    this.sheenDisk = null;
    this.motionQuery = null;
    this.transparencyQuery = null;
    this.animations = /* @__PURE__ */ new Set();
    this.animationTimers = /* @__PURE__ */ new Set();
    this.lastLightTick = 0;
    this._lightBakes = 0;
    this._filterBuilds = 0;
    this._mapError = null;
    this.lensLayer = null;
    this.tintLayer = null;
    this.sheenLayer = null;
    this.rimLayer = null;
    this.noiseLayer = null;
    this.svgEl = null;
    this.dispNodes = [];
    this.mapRefKey = null;
    this.lensMap = null;
    this.resizeObs = null;
    this.resizeRaf = null;
    this.lastW = 0;
    this.lastH = 0;
    this.destroyed = false;
    this.rafId = null;
    this.animatingLight = false;
    this.lensBuildVersion = 0;
    this.angleVelocity = 0;
    this.currentParallaxX = 0;
    this.targetParallaxX = 0;
    this.parallaxXVelocity = 0;
    this.currentParallaxY = 0;
    this.targetParallaxY = 0;
    this.parallaxYVelocity = 0;
    this.currentHoverGlow = 0;
    this.targetHoverGlow = 0;
    this.hoverGlowVelocity = 0;
    this._frameCount = 0;
    this._totalTime = 0;
    this._lastTime = 0;
    this._mapGenMs = 0;
    this._mapPixels = 0;
    this.pressHandlers = null;
    // Dark-mode: live prefers-color-scheme tracking for appearance: 'auto'
    this.schemeQuery = null;
    this.schemeListener = null;
    this.onVisibility = () => {
      if (document.hidden) this.stopLight();
    };
    this.onPreferences = () => {
      if (this.destroyed) return;
      if (this.reduceMotion()) {
        this.stopLight();
        this.cancelAnimations();
        this.currentAngle = this.targetAngle = this.cfg.lightAngle;
        this.currentHoverGlow = this.targetHoverGlow = 0;
        this.el.style.transform = this.originalStyle.get("transform")[0];
        this.el.style.visibility = this.originalStyle.get("visibility")[0];
      }
      this.syncPointerListeners();
      this.queueRebuild();
      this.updateTint();
      this.bakeLight();
      this.updateRings();
    };
    /* ─────────────────── LAYER 0: LENS (backdrop) ─────────────────── */
    this.lensFilterRef = null;
    this.el = element;
    this.overrides = { ...config };
    this.cfg = resolveGlassConfig(this.overrides);
    this.originalStyle = new Map(["position", "border-radius", "overflow", "box-shadow", "transform", "opacity", "animation", "transition", "visibility"].map((key) => [key, [element.style.getPropertyValue(key), element.style.getPropertyPriority(key)]]));
    this.id = `ql${++uid}`;
    this.currentAngle = this.cfg.lightAngle;
    this.targetAngle = this.cfg.lightAngle;
    _LiquidGlassEngine._registry.add(this);
    if (typeof globalThis !== "undefined" && !globalThis.__QUICK_LIQUID__) {
      globalThis.__QUICK_LIQUID__ = { metrics: () => _LiquidGlassEngine.collectMetrics() };
    }
    this.mount();
  }
  /** Aggregate metrics across all live engines (also exposed on
      globalThis.__QUICK_LIQUID__ for tooling/debugging). */
  static collectMetrics() {
    const engines = [..._LiquidGlassEngine._registry].map((e) => ({
      id: e.id,
      size: `${e.lastW}x${e.lastH}`,
      mapGenMs: e._mapGenMs,
      mapPixelsComputed: e._mapPixels,
      mapKey: e.mapRefKey,
      quality: e.cfg.quality,
      dispTaps: e.dispNodes.length,
      mapEncodeMs: e.lensMap?.encodeMs ?? 0,
      mapBytes: e.lensMap ? e.lensMap.width * e.lensMap.height * 4 : 0,
      lightBakes: e._lightBakes,
      filterBuilds: e._filterBuilds,
      mapError: e._mapError
    }));
    return {
      engineCount: engines.length,
      uniqueMaps: mapCache.size,
      cacheHits,
      mapsGenerated,
      engines
    };
  }
  /* ─────────────────────────── MOUNT ─────────────────────────── */
  mount() {
    const el = this.el;
    if (getComputedStyle(el).position === "static") el.style.position = "relative";
    el.style.borderRadius = `${this.cfg.borderRadius}px`;
    el.style.overflow = "hidden";
    this.lastW = el.offsetWidth;
    this.lastH = el.offsetHeight;
    if (!this._contentEl()) {
      this.ownedContent = document.createElement("div");
      this.ownedContent.className = "ql-content";
      this.ownedContent.style.cssText = "position:relative;z-index:10";
      this.ownedContent.append(...Array.from(el.childNodes));
      el.append(this.ownedContent);
    }
    this.motionQuery = matchMedia("(prefers-reduced-motion: reduce)");
    this.transparencyQuery = matchMedia("(prefers-reduced-transparency: reduce)");
    this.motionQuery.addEventListener("change", this.onPreferences);
    this.transparencyQuery.addEventListener("change", this.onPreferences);
    this.createLayers();
    this.applyDepth();
    this.queueRebuild();
    this.resizeObs = new ResizeObserver(() => this.onResize());
    this.resizeObs.observe(el);
    document.addEventListener("visibilitychange", this.onVisibility);
    this.syncPointerListeners();
    this.syncSchemeListener();
  }
  /* ─────────────────── APPEARANCE (dark mode) ─────────────────── */
  isDark() {
    const a = this.cfg.appearance ?? "auto";
    if (a === "dark") return true;
    if (a === "light") return false;
    if (this.schemeQuery) return this.schemeQuery.matches;
    return typeof matchMedia === "function" && matchMedia("(prefers-color-scheme: dark)").matches;
  }
  /** Backdrop luminance 0..1 — explicit config wins, else implied by appearance. */
  backdropLuma() {
    const L = this.cfg.backdropLuminance;
    if (L !== void 0 && Number.isFinite(L)) return Math.min(1, Math.max(0, L));
    return this.isDark() ? LUMA_DARK : LUMA_LIGHT;
  }
  syncSchemeListener() {
    const wanted = (this.cfg.appearance ?? "auto") === "auto" && typeof matchMedia === "function";
    if (wanted && !this.schemeQuery) {
      this.schemeQuery = matchMedia("(prefers-color-scheme: dark)");
      this.schemeListener = () => {
        if (this.destroyed) return;
        this.updateTint();
        this.bakeLight();
        this.updateRings();
        this.applyDepth();
      };
      this.schemeQuery.addEventListener("change", this.schemeListener);
    } else if (!wanted && this.schemeQuery) {
      if (this.schemeListener) this.schemeQuery.removeEventListener("change", this.schemeListener);
      this.schemeQuery = null;
      this.schemeListener = null;
    }
  }
  reduceMotion() {
    return this.cfg.respectPreferences !== false && !!this.motionQuery?.matches;
  }
  reduceTransparency() {
    return this.cfg.respectPreferences !== false && !!this.transparencyQuery?.matches;
  }
  stopLight() {
    if (this.rafId !== null) cancelAnimationFrame(this.rafId);
    this.rafId = null;
    this.animatingLight = false;
    this.lastLightTick = 0;
    this.angleVelocity = this.parallaxXVelocity = this.parallaxYVelocity = this.hoverGlowVelocity = 0;
  }
  queueRebuild() {
    ++this.lensBuildVersion;
    if (this.resizeRaf !== null || this.destroyed) return;
    this.resizeRaf = requestAnimationFrame(() => {
      this.resizeRaf = null;
      if (this.destroyed) return;
      this.sizeLight();
      this.bakeLight();
      this.updateRings();
      void this.rebuildLensFilter().catch((error) => {
        if (this.destroyed) return;
        this._mapError = String(error);
        this.teardownFilter();
        this.updateLensStyle();
      });
    });
  }
  onResize() {
    if (this.destroyed) return;
    const w = this.el.offsetWidth, h = this.el.offsetHeight;
    if (w === this.lastW && h === this.lastH) return;
    this.lastW = w;
    this.lastH = h;
    this.queueRebuild();
  }
  createLayers() {
    [this.lensLayer, this.tintLayer, this.sheenLayer, this.rimLayer, this.noiseLayer].forEach((l) => l?.remove());
    const mk = (cls) => {
      const div = document.createElement("div");
      div.className = cls;
      div.setAttribute("aria-hidden", "true");
      Object.assign(div.style, {
        position: "absolute",
        inset: "0",
        borderRadius: "inherit",
        pointerEvents: "none"
      });
      return div;
    };
    this.lensLayer = mk("ql-lens");
    this.tintLayer = mk("ql-tint");
    this.sheenLayer = mk("ql-sheen");
    this.rimLayer = mk("ql-rim");
    this.noiseLayer = null;
    this.rimDisk = document.createElement("div");
    this.sheenDisk = document.createElement("div");
    this.rimLayer.append(this.rimDisk);
    this.sheenLayer.append(this.sheenDisk);
    const content = this._contentEl();
    for (const layer of [this.lensLayer, this.tintLayer, this.sheenLayer, this.rimLayer, this.noiseLayer]) {
      if (layer) this.el.insertBefore(layer, content);
    }
    this.updateLensStyle();
    this.updateTint();
    this.sizeLight();
    this.bakeLight();
    this.updateRings();
    this.updateNoise();
  }
  _contentEl() {
    return this.el.querySelector(":scope > .ql-content");
  }
  lensFilterString() {
    const cfg = this.cfg;
    if (this.reduceTransparency()) return "none";
    const parts = [];
    if (this.svgEl) parts.push(`url(#${this.id})`);
    if (cfg.saturation !== 1) parts.push(`saturate(${cfg.saturation})`);
    if (cfg.blur > 0) parts.push(`blur(${cfg.blur}px)`);
    return parts.join(" ") || "none";
  }
  /**
   * CHROMIUM QUIRK: if backdrop-filter is first applied WITHOUT a url()
   * reference and the url() is added later on the same element, the SVG
   * filter part stays permanently inert (blur/saturate still apply). The
   * url() must be present the moment the element first gets composited.
   * Therefore: whenever the url() reference part changes, we swap in a
   * brand-new lens node with the final filter string already set.
   */
  updateLensStyle() {
    if (!this.lensLayer) return;
    const bdf = this.lensFilterString();
    const ref = this.svgEl && !this.reduceTransparency() ? this.id : null;
    if (ref !== this.lensFilterRef) {
      const fresh = document.createElement("div");
      fresh.className = "ql-lens";
      fresh.setAttribute("aria-hidden", "true");
      Object.assign(fresh.style, {
        position: "absolute",
        inset: "0",
        borderRadius: "inherit",
        pointerEvents: "none"
      });
      fresh.style.backdropFilter = bdf;
      fresh.style.WebkitBackdropFilter = bdf;
      this.lensLayer.replaceWith(fresh);
      this.lensLayer = fresh;
      this.lensFilterRef = ref;
    } else {
      this.lensLayer.style.backdropFilter = bdf;
      this.lensLayer.style.WebkitBackdropFilter = bdf;
    }
  }
  /* ─────────────────── LAYER 1: TINT ─────────────────── */
  updateTint() {
    if (!this.tintLayer) return;
    const cfg = this.cfg;
    const autoDark = this.isDark() && cfg.tint.replace(/\s/g, "") === DEFAULT_CONFIG.tint.replace(/\s/g, "");
    const tint = autoDark ? DARK_TINT : cfg.tint;
    const op = this.reduceTransparency() ? 1 : cfg.tintOpacity * (cfg.tintStrength ?? 1) * (autoDark ? 1.75 : 1);
    if (op <= 0) {
      this.tintLayer.style.background = "none";
      return;
    }
    this.tintLayer.style.background = [
      `linear-gradient(180deg,
        rgba(${tint}, ${(this.reduceTransparency() ? 1 : op * 1.2).toFixed(4)}) 0%,
        rgba(${tint}, ${(this.reduceTransparency() ? 1 : op * 0.85).toFixed(4)}) 100%)`
    ].join(", ");
    this.tintLayer.style.mixBlendMode = cfg.adaptiveTint && !this.reduceTransparency() ? "overlay" : "normal";
  }
  /* ─────────────── LAYERS 2+3: CONIC LIGHT RINGS ───────────────
     The Apple signature: the rim catches light in TWO lobes — at
     the light angle and its mirror (glass reflects on the near and
     far bezel). Implemented as conic gradients masked to rings:
       .ql-rim   — crisp ~1.3px ring, strong lobes
       .ql-sheen — bezel-band-wide ring, soft lobes + dark flanks   */
  ringMask(padPx) {
    return {
      boxSizing: "border-box",
      padding: `${padPx}px`,
      maskImage: "linear-gradient(#000 0 0), linear-gradient(#000 0 0)",
      maskClip: "content-box, border-box",
      maskComposite: "exclude"
    };
  }
  conicStops(angleDeg, power, peakA, baseA, darkA) {
    const stops = [];
    const STEP = 4;
    for (let a = 0; a <= 360; a += STEP) {
      const rel = (a - angleDeg) * Math.PI / 180;
      const c = Math.abs(Math.cos(rel));
      const sn = Math.abs(Math.sin(rel));
      const white = baseA + peakA * Math.pow(c, power);
      const dark = darkA * sn * sn;
      const net = white - dark;
      const col = net >= 0 ? `rgba(255,255,255,${net.toFixed(4)})` : `rgba(10,14,22,${(-net).toFixed(4)})`;
      stops.push(`${col} ${a}deg`);
    }
    return `conic-gradient(from 0deg at 50% 50%, ${stops.join(", ")})`;
  }
  sizeLight() {
    const diameter = Math.ceil(Math.hypot(this.lastW, this.lastH)) + 4;
    const bezel = Math.min(this.cfg.bezelWidth, this.lastW / 2, this.lastH / 2);
    if (this.rimLayer) Object.assign(this.rimLayer.style, this.ringMask(1), { overflow: "hidden" });
    if (this.sheenLayer) Object.assign(this.sheenLayer.style, this.ringMask(Math.max(2, bezel * 0.45)), { overflow: "hidden", filter: "none" });
    for (const disk of [this.rimDisk, this.sheenDisk]) if (disk) {
      Object.assign(disk.style, { position: "absolute", width: diameter + "px", height: diameter + "px", left: "50%", top: "50%" });
    }
  }
  /** Rotation-equivariant lighting: bake gradients only when the material changes. */
  bakeLight() {
    this._lightBakes++;
    const cfg = this.cfg, L = this.backdropLuma();
    const p = cfg.fresnelPower ?? 2.2;
    if (this.rimDisk) {
      const hi = cfg.edgeHighlight * (0.65 + 0.44 * L);
      this.rimDisk.style.background = this.conicStops(0, p, hi * 0.86, hi * 0.075, hi * 0.035);
    }
    if (this.sheenDisk) {
      const sp = cfg.specularStrength * (0.4 + 0.65 * L);
      this.sheenDisk.style.background = this.conicStops(0, p, sp * 0.23, 0, sp * 0.16);
    }
  }
  updateRings() {
    const rot = `translate(-50%,-50%) rotate(${this.currentAngle}deg)`;
    for (const disk of [this.rimDisk, this.sheenDisk]) if (disk) disk.style.transform = rot;
    const opacity = String(this.reduceTransparency() ? 0 : 0.82 + 0.18 * this.currentHoverGlow);
    if (this.rimLayer) this.rimLayer.style.opacity = opacity;
    if (this.sheenLayer) this.sheenLayer.style.opacity = opacity;
  }
  /* ─────────────────── NOISE ─────────────────── */
  updateNoise() {
    const cfg = this.cfg;
    if (cfg.noiseOpacity <= 0) {
      this.noiseLayer?.remove();
      this.noiseLayer = null;
      return;
    }
    if (!this.noiseLayer) {
      this.noiseLayer = document.createElement("div");
      this.noiseLayer.className = "ql-noise";
      this.noiseLayer.setAttribute("aria-hidden", "true");
      Object.assign(this.noiseLayer.style, { position: "absolute", inset: "0", borderRadius: "inherit", pointerEvents: "none" });
      this.el.insertBefore(this.noiseLayer, this._contentEl());
    }
    const noiseSvg = `data:image/svg+xml,%3Csvg viewBox='0 0 200 200' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.65' numOctaves='3' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E`;
    Object.assign(this.noiseLayer.style, {
      backgroundImage: `url("${noiseSvg}")`,
      opacity: String(cfg.noiseOpacity),
      backgroundSize: `${100 * cfg.noiseScale}px ${100 * cfg.noiseScale}px`,
      mixBlendMode: "overlay"
    });
  }
  /* ─────────────────── SHADOW ─────────────────── */
  applyDepth() {
    const e = this.cfg.elevation;
    if (e <= 0) {
      this.el.style.boxShadow = "none";
      return;
    }
    const t = Math.max(1, this.cfg.thickness / 8);
    if (this.isDark()) {
      this.el.style.boxShadow = [
        `0 0 ${(26 * t * e).toFixed(0)}px rgba(148,176,224,${(0.1 * e).toFixed(3)})`,
        `0 ${(6 * t * e).toFixed(0)}px ${(24 * t * e).toFixed(0)}px rgba(0,0,0,${(0.36 * e).toFixed(3)})`,
        `0 ${(1.5 * e).toFixed(1)}px ${(5 * e).toFixed(0)}px rgba(0,0,0,${(0.24 * e).toFixed(3)})`
      ].join(", ");
      return;
    }
    this.el.style.boxShadow = [
      `0 ${(6 * t * e).toFixed(0)}px ${(22 * t * e).toFixed(0)}px rgba(16,22,34,${(0.13 * e).toFixed(3)})`,
      `0 ${(1.5 * e).toFixed(1)}px ${(5 * e).toFixed(0)}px rgba(16,22,34,${(0.08 * e).toFixed(3)})`
    ].join(", ");
  }
  /* ─────────────────── SVG LENS FILTER ─────────────────── */
  shouldUseSVG() {
    const mode = this.cfg.refractionMode;
    if (mode === "svg") return true;
    if (mode === "css") return false;
    if (_LiquidGlassEngine._svgOk === null) {
      _LiquidGlassEngine._svgOk = this.detectSVGSupport();
    }
    return _LiquidGlassEngine._svgOk;
  }
  detectSVGSupport() {
    if (typeof document === "undefined") return false;
    try {
      const svgNS = "http://www.w3.org/2000/svg";
      const svg = document.createElementNS(svgNS, "svg");
      svg.setAttribute("width", "0");
      svg.setAttribute("height", "0");
      svg.style.cssText = "position:absolute;overflow:hidden;pointer-events:none;";
      const defs = document.createElementNS(svgNS, "defs");
      const filter = document.createElementNS(svgNS, "filter");
      filter.id = "__ql_probe__";
      defs.appendChild(filter);
      svg.appendChild(defs);
      document.body.appendChild(svg);
      const probe = document.createElement("div");
      probe.style.cssText = "position:fixed;top:0;left:0;width:1px;height:1px;backdrop-filter:url(#__ql_probe__);-webkit-backdrop-filter:url(#__ql_probe__);pointer-events:none;";
      document.body.appendChild(probe);
      const cs = getComputedStyle(probe);
      const bf = cs.backdropFilter || cs.webkitBackdropFilter || "";
      const ok = bf.includes("url(");
      probe.remove();
      svg.remove();
      return ok;
    } catch {
      return false;
    }
  }
  /** Per-channel dispersion scales. Blue refracts more than red in real
      glass; sampling is inward so blue gets the LARGER scale. */
  channelScales() {
    const M = this.cfg.ior > 1 ? 1 : 0;
    const base = 2 * this.cfg.refractionStrength * (M > 0 ? 1 : 0);
    const ca = this.cfg.chromaticAberration;
    return {
      base,
      r: base * (1 - ca * 0.1),
      g: base,
      b: base * (1 + ca * 0.14)
    };
  }
  async rebuildLensFilter() {
    const buildVersion = ++this.lensBuildVersion;
    const cfg = this.cfg;
    if (!this.shouldUseSVG() || cfg.refractionStrength <= 0 || cfg.ior <= 1 || this.reduceTransparency()) {
      this.teardownFilter();
      this.updateLensStyle();
      return;
    }
    const w = this.el.offsetWidth;
    const h = this.el.offsetHeight;
    if (w < 8 || h < 8) return;
    const resCap = cfg.quality === "low" ? 128 : cfg.quality === "medium" ? 384 : 1024;
    const radius = Math.min(cfg.borderRadius, Math.min(w, h) / 2);
    const { map, key } = await acquireLensMap(w, h, radius, cfg.bezelWidth, cfg.thickness, cfg.ior, resCap, cfg.blur);
    if (this.destroyed || buildVersion !== this.lensBuildVersion) {
      releaseMap(key);
      return;
    }
    if (this.mapRefKey === key) {
      releaseMap(key);
    } else {
      if (this.mapRefKey) releaseMap(this.mapRefKey);
      this.mapRefKey = key;
    }
    this.lensMap = map;
    this._mapGenMs = map.genMs;
    this._mapPixels = map.pixelsComputed;
    this._mapError = null;
    this.buildFilterDOM(w, h, map);
    this.lensFilterRef = "__stale__";
    this.updateLensStyle();
  }
  teardownFilter() {
    if (this.svgEl) {
      this.svgEl.remove();
      this.svgEl = null;
    }
    this.dispNodes = [];
    if (this.mapRefKey) {
      releaseMap(this.mapRefKey);
      this.mapRefKey = null;
    }
    this.lensMap = null;
  }
  buildFilterDOM(w, h, map) {
    this._filterBuilds++;
    if (this.svgEl) {
      this.svgEl.remove();
      this.svgEl = null;
    }
    this.dispNodes = [];
    const { r, g, b } = this.channelScales();
    const useCA = usesDispersion(this.cfg.refractionStrength, this.cfg.chromaticAberration, this.cfg.blur, this.cfg.quality, this.cfg.dispersionMode);
    const pad = filterPadding(this.cfg.blur);
    const px = map.padX * w / map.mapWidth, py = map.padY * h / map.mapHeight;
    const mapImage = `<feImage href="${map.url}" result="encoded" preserveAspectRatio="none" x="${-px}" y="${-py}" width="${w + 2 * px}" height="${h + 2 * py}"/>
      <feComponentTransfer in="encoded" result="map">
        <feFuncR type="linear" slope="${255 / 254}" intercept="${-1 / 254}"/>
        <feFuncG type="linear" slope="${255 / 254}" intercept="${-1 / 254}"/>
      </feComponentTransfer>`;
    let content;
    if (useCA) {
      content = `
        ${mapImage}
        <feDisplacementMap in="SourceGraphic" in2="map" scale="${r.toFixed(2)}" xChannelSelector="R" yChannelSelector="G" result="dR"/>
        <feDisplacementMap in="SourceGraphic" in2="map" scale="${g.toFixed(2)}" xChannelSelector="R" yChannelSelector="G" result="dG"/>
        <feDisplacementMap in="SourceGraphic" in2="map" scale="${b.toFixed(2)}" xChannelSelector="R" yChannelSelector="G" result="dB"/>
        <feComponentTransfer in="dR" result="cR">
          <feFuncG type="discrete" tableValues="0"/><feFuncB type="discrete" tableValues="0"/>
        </feComponentTransfer>
        <feComponentTransfer in="dG" result="cG">
          <feFuncR type="discrete" tableValues="0"/><feFuncB type="discrete" tableValues="0"/>
        </feComponentTransfer>
        <feComponentTransfer in="dB" result="cB">
          <feFuncR type="discrete" tableValues="0"/><feFuncG type="discrete" tableValues="0"/>
        </feComponentTransfer>
        <feBlend in="cR" in2="cG" mode="screen" result="rg"/>
        <feBlend in="rg" in2="cB" mode="screen"/>`;
    } else {
      content = `
        ${mapImage}
        <feDisplacementMap in="SourceGraphic" in2="map" scale="${g.toFixed(2)}" xChannelSelector="R" yChannelSelector="G"/>`;
    }
    const svgStr = `<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" style="position:absolute;overflow:hidden;pointer-events:none">
      <defs>
        <filter id="${this.id}" filterUnits="userSpaceOnUse"
          x="${-pad}" y="${-pad}" width="${w + 2 * pad}" height="${h + 2 * pad}"
          color-interpolation-filters="sRGB">${content}</filter>
      </defs>
    </svg>`;
    const div = document.createElement("div");
    div.innerHTML = svgStr.trim();
    this.svgEl = div.querySelector("svg");
    document.body.appendChild(this.svgEl);
    this.dispNodes = Array.from(this.svgEl.querySelectorAll("feDisplacementMap"));
  }
  /** Surgical update: strength/CA changes only rewrite `scale` attributes. */
  updateFilterScales() {
    if (!this.svgEl || this.dispNodes.length === 0) return;
    const { r, g, b } = this.channelScales();
    if (this.dispNodes.length === 3) {
      this.dispNodes[0].setAttribute("scale", r.toFixed(2));
      this.dispNodes[1].setAttribute("scale", g.toFixed(2));
      this.dispNodes[2].setAttribute("scale", b.toFixed(2));
    } else {
      this.dispNodes[0].setAttribute("scale", g.toFixed(2));
    }
  }
  setupPointer() {
    if (this._pointerMoveHandler) return;
    this._pointerEnterHandler = () => {
      if (this.cfg.hoverLighting) {
        this.targetHoverGlow = 1;
        this.kickLight();
      }
    };
    this._pointerMoveHandler = (e) => {
      const rect = this.el.getBoundingClientRect();
      if (!rect.width || !rect.height || e.pointerType === "touch") return;
      const px = (e.clientX - rect.left) / rect.width;
      const py = (e.clientY - rect.top) / rect.height;
      if (this.cfg.cursorTracking) {
        this.targetAngle = Math.atan2(px - 0.5, -(py - 0.5)) * (180 / Math.PI);
      }
      if (this.cfg.parallax) {
        this.targetParallaxX = (px - 0.5) * 2;
        this.targetParallaxY = (py - 0.5) * 2;
      }
      this.kickLight();
    };
    this._pointerLeaveHandler = () => {
      this.targetAngle = this.cfg.lightAngle;
      this.targetParallaxX = 0;
      this.targetParallaxY = 0;
      this.targetHoverGlow = 0;
      this.kickLight();
    };
    this.el.addEventListener("mouseenter", this._pointerEnterHandler, { passive: true });
    this.el.addEventListener("pointermove", this._pointerMoveHandler, { passive: true });
    this.el.addEventListener("pointerleave", this._pointerLeaveHandler, { passive: true });
  }
  teardownPointer() {
    if (this._pointerEnterHandler) {
      this.el.removeEventListener("mouseenter", this._pointerEnterHandler);
      this._pointerEnterHandler = void 0;
    }
    if (this._pointerMoveHandler) {
      this.el.removeEventListener("pointermove", this._pointerMoveHandler);
      this._pointerMoveHandler = void 0;
    }
    if (this._pointerLeaveHandler) {
      this.el.removeEventListener("pointerleave", this._pointerLeaveHandler);
      this._pointerLeaveHandler = void 0;
    }
  }
  kickLight() {
    if (this.animatingLight || this.destroyed || this.reduceMotion() || document.hidden) return;
    this.animatingLight = true;
    this.lastLightTick = performance.now();
    this.rafId = requestAnimationFrame(() => this.tickLight());
  }
  tickLight() {
    if (this.destroyed || this.reduceMotion() || document.hidden) {
      this.stopLight();
      return;
    }
    const t0 = performance.now();
    const dt = Math.min(0.05, Math.max(1e-3, (t0 - this.lastLightTick) / 1e3));
    this.lastLightTick = t0;
    let moving = false;
    const step = (value, target, velocity, threshold) => {
      if (Math.abs(target - value) < threshold && Math.abs(velocity) < threshold * 10) return [target, 0];
      moving = true;
      if (!this.cfg.inertia) return [target, 0];
      const omega = 22, offset = value - target;
      const c = velocity + omega * offset, decay = Math.exp(-omega * dt);
      return [target + (offset + c * dt) * decay, (velocity - omega * c * dt) * decay];
    };
    const diff = ((this.targetAngle - this.currentAngle + 180) % 360 + 360) % 360 - 180;
    [this.currentAngle, this.angleVelocity] = step(this.currentAngle, this.currentAngle + diff, this.angleVelocity, 0.08);
    [this.currentHoverGlow, this.hoverGlowVelocity] = step(this.currentHoverGlow, this.targetHoverGlow, this.hoverGlowVelocity, 2e-3);
    if (this.cfg.parallax) {
      [this.currentParallaxX, this.parallaxXVelocity] = step(this.currentParallaxX, this.targetParallaxX, this.parallaxXVelocity, 1e-3);
      [this.currentParallaxY, this.parallaxYVelocity] = step(this.currentParallaxY, this.targetParallaxY, this.parallaxYVelocity, 1e-3);
      this.el.style.transform = this.currentParallaxX || this.currentParallaxY ? `perspective(1000px) rotateY(${this.currentParallaxX * 5}deg) rotateX(${-this.currentParallaxY * 5}deg)` : this.originalStyle.get("transform")[0];
    }
    this.updateRings();
    this._lastTime = performance.now() - t0;
    this._totalTime += this._lastTime;
    this._frameCount++;
    if (moving) this.rafId = requestAnimationFrame(() => this.tickLight());
    else this.stopLight();
  }
  /* ─────────────────── ANIMATIONS (public) ─────────────────── */
  cancelAnimations() {
    for (const timer of this.animationTimers) clearTimeout(timer);
    this.animationTimers.clear();
    for (const animation of this.animations) animation.cancel();
    this.animations.clear();
  }
  async play(keyframes, duration) {
    if (this.destroyed || this.reduceMotion()) return false;
    this.cancelAnimations();
    const animation = this.el.animate(keyframes, { duration, easing: "cubic-bezier(.2,.8,.2,1)" });
    this.animations.add(animation);
    try {
      await animation.finished;
      return true;
    } catch {
      return false;
    } finally {
      this.animations.delete(animation);
    }
  }
  enableLiquidPress(config) {
    this.disableLiquidPress();
    const scale = config?.scale ?? 0.96, squish = config?.squish ?? 0.018;
    let pressed = false;
    const onDown = (event) => {
      if (event.button !== 0 || this.destroyed || this.reduceMotion()) return;
      pressed = true;
      this.cancelAnimations();
      this.el.style.transform = `scale(${1 + squish}, ${scale})`;
    };
    const onUp = () => {
      if (!pressed || this.destroyed) return;
      pressed = false;
      const from = this.el.style.transform;
      this.el.style.transform = this.originalStyle.get("transform")[0];
      void this.play([{ transform: from }, { transform: "scale(.99,1.015)", offset: 0.5 }, { transform: "scale(1)" }], 380);
    };
    this.pressHandlers = { down: onDown, up: onUp, leave: onUp, reset: () => {
      if (pressed) this.el.style.transform = this.originalStyle.get("transform")[0];
      pressed = false;
    } };
    this.el.addEventListener("pointerdown", onDown, { passive: true });
    this.el.addEventListener("pointerup", onUp, { passive: true });
    this.el.addEventListener("pointerleave", onUp, { passive: true });
    this.el.addEventListener("pointercancel", onUp, { passive: true });
  }
  disableLiquidPress() {
    if (!this.pressHandlers) return;
    this.pressHandlers.reset();
    this.el.removeEventListener("pointerdown", this.pressHandlers.down);
    this.el.removeEventListener("pointerup", this.pressHandlers.up);
    this.el.removeEventListener("pointerleave", this.pressHandlers.leave);
    this.el.removeEventListener("pointercancel", this.pressHandlers.up);
    this.pressHandlers = null;
  }
  animateIn(delay = 0) {
    if (this.destroyed) return;
    this.cancelAnimations();
    this.el.style.visibility = this.originalStyle.get("visibility")[0];
    if (this.reduceMotion()) return;
    const start = () => {
      if (this.destroyed) return;
      void this.play([{ transform: "scale(.94)" }, { transform: "scale(1.012)", offset: 0.65 }, { transform: "scale(1)" }], 420);
    };
    if (delay > 0) {
      const timer = setTimeout(() => {
        this.animationTimers.delete(timer);
        start();
      }, delay);
      this.animationTimers.add(timer);
    } else start();
  }
  async animateOut() {
    if (this.destroyed) return;
    const completed = this.reduceMotion() || await this.play([{ transform: "scale(1)" }, { transform: "scale(.94)" }], 180);
    if (completed && !this.destroyed) this.el.style.visibility = "hidden";
  }
  jiggle(intensity = 1) {
    const i = Math.max(0, Math.min(3, intensity));
    void this.play([
      { transform: "scale(1)" },
      { transform: `scale(${1 + 0.025 * i},${1 - 0.018 * i})`, offset: 0.2 },
      { transform: `scale(${1 - 0.016 * i},${1 + 0.012 * i})`, offset: 0.5 },
      { transform: "scale(1)" }
    ], 480);
  }
  getElement() {
    return this.el;
  }
  /* ─────────────────── PUBLIC API ─────────────────── */
  getPerformanceMetrics() {
    return {
      avgFrameTime: this._frameCount > 0 ? this._totalTime / this._frameCount : 0,
      lastFrameTime: this._lastTime,
      frameCount: this._frameCount,
      quality: this.cfg.quality,
      mapGenMs: this._mapGenMs,
      mapPixelsComputed: this._mapPixels,
      mapEncodeMs: this.lensMap?.encodeMs ?? 0,
      displacementTaps: this.dispNodes.length,
      lightBakes: this._lightBakes,
      filterBuilds: this._filterBuilds,
      mapError: this._mapError
    };
  }
  /** Patch explicit overrides. Pass undefined to remove an override. */
  updateConfig(config) {
    this.setConfig({ ...this.overrides, ...config });
  }
  /** Replace the declaration (used by React); omitted keys revert to the preset/default. */
  setConfig(config) {
    if (this.destroyed) return;
    const oldCfg = this.cfg;
    this.overrides = { ...config };
    const cfg = this.cfg = resolveGlassConfig(this.overrides);
    const changed = (...keys) => keys.some((k) => oldCfg[k] !== cfg[k]);
    if (!Object.keys({ ...oldCfg, ...cfg }).some((k) => oldCfg[k] !== cfg[k])) return;
    const graph = (c) => usesDispersion(c.refractionStrength, c.chromaticAberration, c.blur, c.quality, c.dispersionMode);
    const geometryChanged = changed("borderRadius", "bezelWidth", "thickness", "ior", "quality", "refractionMode", "respectPreferences") || oldCfg.refractionStrength <= 0 !== cfg.refractionStrength <= 0 || filterPadding(oldCfg.blur) !== filterPadding(cfg.blur) || graph(oldCfg) !== graph(cfg);
    if (changed("borderRadius")) this.el.style.borderRadius = cfg.borderRadius + "px";
    if (geometryChanged) this.queueRebuild();
    else if (changed("refractionStrength", "chromaticAberration")) this.updateFilterScales();
    if (changed("lightAngle")) {
      this.targetAngle = cfg.lightAngle;
      this.currentAngle = cfg.lightAngle;
      this.angleVelocity = 0;
      this.updateRings();
    }
    if (changed("blur", "saturation", "respectPreferences")) this.updateLensStyle();
    if (changed("tint", "tintOpacity", "tintStrength", "adaptiveTint", "appearance", "respectPreferences")) this.updateTint();
    if (changed("edgeHighlight", "specularStrength", "fresnelPower", "appearance", "backdropLuminance", "hoverLighting", "respectPreferences")) {
      this.bakeLight();
      this.updateRings();
    }
    if (changed("noiseOpacity", "noiseScale")) this.updateNoise();
    if (changed("elevation", "thickness", "appearance")) this.applyDepth();
    if (changed("cursorTracking", "parallax", "hoverLighting", "respectPreferences")) {
      this.targetHoverGlow = 0;
      if (!cfg.parallax) this.el.style.transform = this.originalStyle.get("transform")[0];
      this.syncPointerListeners();
    }
    if (changed("appearance")) this.syncSchemeListener();
    if (changed("respectPreferences")) this.onPreferences();
  }
  getConfig() {
    return { ...this.cfg };
  }
  syncPointerListeners() {
    if (!this.reduceMotion() && (this.cfg.cursorTracking || this.cfg.hoverLighting || this.cfg.parallax)) {
      this.setupPointer();
    } else {
      this.teardownPointer();
      this.stopLight();
    }
  }
  destroy() {
    if (this.destroyed) return;
    this.destroyed = true;
    this.cancelAnimations();
    this.disableLiquidPress();
    document.removeEventListener("visibilitychange", this.onVisibility);
    this.motionQuery?.removeEventListener("change", this.onPreferences);
    this.transparencyQuery?.removeEventListener("change", this.onPreferences);
    _LiquidGlassEngine._registry.delete(this);
    this.lensBuildVersion++;
    if (this.rafId) cancelAnimationFrame(this.rafId);
    if (this.resizeRaf) cancelAnimationFrame(this.resizeRaf);
    this.resizeObs?.disconnect();
    this.teardownFilter();
    [this.lensLayer, this.tintLayer, this.sheenLayer, this.rimLayer, this.noiseLayer].forEach((l) => l?.remove());
    this.lensLayer = this.tintLayer = this.sheenLayer = this.rimLayer = this.noiseLayer = null;
    this.teardownPointer();
    if (this.schemeQuery && this.schemeListener) {
      this.schemeQuery.removeEventListener("change", this.schemeListener);
    }
    this.schemeQuery = null;
    this.schemeListener = null;
    if (this.ownedContent) {
      this.ownedContent.replaceWith(...Array.from(this.ownedContent.childNodes));
      this.ownedContent = null;
    }
    for (const [property, [value, priority]] of this.originalStyle) {
      if (value) this.el.style.setProperty(property, value, priority);
      else this.el.style.removeProperty(property);
    }
  }
};
_LiquidGlassEngine._svgOk = null;
_LiquidGlassEngine._registry = /* @__PURE__ */ new Set();
var LiquidGlassEngine = _LiquidGlassEngine;

export {
  DEFAULT_CONFIG,
  MATERIAL_PRESETS,
  LiquidGlassEngine
};
//# sourceMappingURL=chunk-4TLGP4GF.mjs.map