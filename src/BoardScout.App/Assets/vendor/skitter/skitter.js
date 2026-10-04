// Skitter v2.1.0 — procedural crawlers: IK spiders that walk the page and hunt problems (plus reading, webs,
// hide-and-peek, link crawling and a scraping octopus, all off here). MIT.
// Built from github.com/mikedopp/Skitter (dist/skitter.js); do not edit here.
"use strict";
var SkitterLib = (() => {
  var __defProp = Object.defineProperty;
  var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
  var __getOwnPropNames = Object.getOwnPropertyNames;
  var __hasOwnProp = Object.prototype.hasOwnProperty;
  var __export = (target, all) => {
    for (var name in all)
      __defProp(target, name, { get: all[name], enumerable: true });
  };
  var __copyProps = (to, from, except, desc) => {
    if (from && typeof from === "object" || typeof from === "function") {
      for (let key of __getOwnPropNames(from))
        if (!__hasOwnProp.call(to, key) && key !== except)
          __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
    }
    return to;
  };
  var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

  // src/skitter.ts
  var skitter_exports = {};
  __export(skitter_exports, {
    DEFAULT_FOOD: () => DEFAULT_FOOD,
    DEFAULT_PREY: () => DEFAULT_PREY,
    Skitter: () => Skitter
  });

  // src/text.ts
  var MARK_COLOR = {
    word: "#9fb3d1",
    lnk: "#3fd0ff",
    ent: "#ff4fd8",
    num: "#ffc94d",
    date: "#ffc94d",
    val: "#5cff8a",
    net: "#5cffc8",
    img: "#9dff5c",
    title: "#ffb347",
    hop: "#5cff8a",
    doi: "#9b6bff",
    pmid: "#ff3d8b",
    isbn: "#3fd0ff",
    quote: "#b6f03c",
    url: "#3fd0ff",
    mail: "#ff9a3d"
  };
  var KIND_CODE = {
    word: "TXT",
    lnk: "LNK",
    ent: "ENT",
    num: "NUM",
    date: "DATE",
    val: "VAL",
    net: "NET",
    img: "IMG",
    title: "DOC",
    hop: "HOP",
    doi: "DOI",
    pmid: "PMID",
    isbn: "ISBN",
    quote: "TITLE",
    url: "URL",
    mail: "MAIL"
  };
  var READ_BLOCKS = "p, li, dd, dt, blockquote, figcaption, td, th, h2, h3, h4, pre, label, [data-crawl-read]";
  var SKIP = "sup, script, style, noscript, textarea, .skitter-sr, .skitter-ch, .mw-editsection, [data-skitter-ignore]";
  var MONTHS = /^(January|February|March|April|May|June|July|August|September|October|November|December)$/;
  var STOP = /* @__PURE__ */ new Set([
    "I",
    "The",
    "A",
    "An",
    "In",
    "On",
    "At",
    "It",
    "This",
    "That",
    "These",
    "Those",
    "He",
    "She",
    "They",
    "We",
    "You",
    "His",
    "Her",
    "Its",
    "Their",
    "Our",
    "As",
    "For",
    "With",
    "By",
    "From",
    "But",
    "And",
    "Or",
    "If",
    "When"
  ]);
  var slug = (s, max = 16) => s.normalize("NFKD").replace(/[\u0300-\u036f]/g, "").toUpperCase().replace(/[^A-Z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, max).replace(/-+$/, "");
  var strip = (w) => w.replace(/^[("'\u201c\u2018\[\u00ab]+|[)"'\u201d\u2019\],.;:!?\u00bb]+$/g, "");
  function isLeafBlock(el) {
    return !el.querySelector(READ_BLOCKS) && !el.closest(SKIP);
  }
  function textNodes(block) {
    const out = [];
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT, {
      acceptNode: (n) => {
        const p = n.parentElement;
        if (!p || p.closest(SKIP)) return NodeFilter.FILTER_REJECT;
        return /\S/.test(n.nodeValue || "") ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
      }
    });
    for (let n = walker.nextNode(); n && out.length < 400; n = walker.nextNode()) out.push(n);
    return out;
  }
  function classify(core, prev) {
    if (!core || !/[\p{L}\p{N}]/u.test(core)) return null;
    if (/^[$\u20ac\u00a3\u00a5]\s?\d/.test(core)) return "val";
    if (/^(\d{1,3}\.){3}\d{1,3}(:\d+)?$/.test(core) || /^([0-9a-f]{2}[:-]){5}[0-9a-f]{2}$/i.test(core)) return "net";
    if (/^(1[0-9]{3}|20[0-9]{2})s?$/.test(core) || MONTHS.test(core) || /^\d{4}-\d{2}-\d{2}$/.test(core)) return "date";
    if (/^\d[\d.,]*(%|[kKMGT]?(B|b|bps|Hz)|ms|s|km|m|kg|x|\u00b0)?$/.test(core)) return "num";
    const sentenceStart = !prev || /[.!?:]["')\]\u201d\u2019]*$/.test(prev);
    if (/^\p{Lu}{2,}\d*$/u.test(core) && core.length <= 8) return "ent";
    if (/^\p{Lu}\p{Ll}/u.test(core) && !sentenceStart && !STOP.has(core)) return "ent";
    return "word";
  }
  function tokenize(block) {
    const words = [];
    for (const node of textNodes(block)) {
      const t = node.nodeValue || "", a = node.parentElement?.closest("a[href]") ?? null;
      const re = /\S+/g;
      for (let m = re.exec(t); m && words.length < 500; m = re.exec(t)) words.push({ node, start: m.index, end: m.index + m[0].length, text: m[0], a });
    }
    const out = [];
    const mk = (i2, j, kind, label) => {
      const r = document.createRange();
      r.setStart(words[i2].node, words[i2].start);
      r.setEnd(words[j].node, words[j].end);
      out.push({ range: r, kind, label });
    };
    let i = 0;
    while (i < words.length) {
      const w = words[i];
      if (w.a) {
        let j2 = i;
        while (j2 + 1 < words.length && words[j2 + 1].a === w.a) j2++;
        mk(i, j2, "lnk", (w.a.textContent || "").trim().replace(/\s+/g, " "));
        i = j2 + 1;
        continue;
      }
      const kind = classify(strip(w.text), i ? words[i - 1].text : "");
      if (!kind) {
        i++;
        continue;
      }
      let j = i;
      if (kind === "ent" || kind === "date" && MONTHS.test(strip(w.text))) {
        while (j + 1 < words.length && !words[j + 1].a && !/[.;:!?)]$/.test(words[j].text)) {
          const nx = strip(words[j + 1].text);
          if (kind === "ent" && /^\p{Lu}/u.test(nx) && !MONTHS.test(nx) && !/,$/.test(words[j].text)) j++;
          else if (kind === "date" && /^\d{1,4}(st|nd|rd|th)?$/.test(nx)) j++;
          else break;
        }
      }
      const label = words.slice(i, j + 1).map((x) => x.text).join(" ");
      mk(i, j, kind, strip(label));
      i = j + 1;
    }
    return out;
  }
  var PATTERNS = [
    // [kind, pattern, capture group holding the value (0 = whole match)]
    ["doi", /\b10\.\d{4,9}\/[^\s"<>]*[^\s"<>.,;:)\]]/g, 0],
    ["pmid", /\bPMID:?\s*(\d{5,9})\b/g, 0],
    ["isbn", /\bISBN(?:-1[03])?:?\s*((?:97[89][\s-]?)?\d[\d\s-]{7,14}[\dXx])\b/g, 0],
    ["url", /\bhttps?:\/\/[^\s<>"]+[^\s<>".,;:)\]]/g, 0],
    ["mail", /\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b/g, 0],
    ["quote", /[\u201c"]([^\u201d"\n]{12,140})[\u201d"]/g, 1],
    ["val", /[$\u20ac\u00a3\u00a5]\s?\d[\d,]*(?:\.\d+)?(?:\s?(?:[KMB]|million|billion|bn))?/g, 0],
    ["net", /\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{2,5})?\b|\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b/g, 0],
    ["date", /\b(?:January|February|March|April|May|June|July|August|September|October|November|December) \d{1,2}, \d{4}\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2} (?:January|February|March|April|May|June|July|August|September|October|November|December) \d{4}\b/g, 0]
  ];
  function harvest(block) {
    const nodes = textNodes(block);
    if (!nodes.length) return [];
    const starts = [];
    let full = "";
    for (const n of nodes) {
      starts.push(full.length);
      full += n.nodeValue || "";
    }
    if (full.length > 6e3) return [];
    const at = (pos, end) => {
      let lo = 0, hi = nodes.length - 1;
      const p = end ? pos - 1 : pos;
      while (lo < hi) {
        const mid = lo + hi + 1 >> 1;
        if (starts[mid] <= p) lo = mid;
        else hi = mid - 1;
      }
      return [nodes[lo], pos - starts[lo]];
    };
    const hits = [];
    for (const [kind, re, g] of PATTERNS) {
      re.lastIndex = 0;
      for (let m = re.exec(full); m; m = re.exec(full)) {
        if (m[0].length < 3) continue;
        const s = m.index, e = m.index + m[0].length;
        if (hits.some((h) => s < h.e && e > h.s)) continue;
        hits.push({ s, e, kind, label: (m[g] ?? m[0]).trim() });
        if (hits.length > 40) break;
      }
    }
    const out = [];
    for (const h of hits.sort((a, b) => a.s - b.s)) {
      const [sn, so] = at(h.s, false), [en, eo] = at(h.e, true);
      const r = document.createRange();
      try {
        r.setStart(sn, so);
        r.setEnd(en, eo);
      } catch {
        continue;
      }
      out.push({ range: r, kind: h.kind, label: h.label });
    }
    for (const a of block.querySelectorAll("a[href]")) {
      if (out.length > 48) break;
      const label = (a.textContent || "").trim().replace(/\s+/g, " ");
      if (label.length < 2 || a.closest(SKIP) || out.some((t) => t.range.intersectsNode(a))) continue;
      const r = document.createRange();
      r.selectNodeContents(a);
      out.push({ range: r, kind: "lnk", label });
    }
    return out;
  }
  function firstRect(r) {
    const list = r.getClientRects();
    for (const q of list) if (q.width > 0.5 && q.height > 0.5) return q;
    return null;
  }
  function lineBoxes(r) {
    const raw = [...r.getClientRects()].filter((q) => q.width > 0.5 && q.height > 0.5);
    raw.sort((a, b) => a.top - b.top || a.left - b.left);
    const out = [];
    for (const q of raw) {
      const last = out[out.length - 1];
      if (last && Math.abs(q.top - last.top) < 4 && q.left <= last.right + 4) {
        last.right = Math.max(last.right, q.right);
        last.bottom = Math.max(last.bottom, q.bottom);
        last.top = Math.min(last.top, q.top);
      } else out.push({ left: q.left, top: q.top, right: q.right, bottom: q.bottom });
    }
    return out;
  }
  function imageCaption(el) {
    const img = el;
    const cap = img.getAttribute?.("alt") || el.getAttribute("aria-label") || el.getAttribute("title") || el.closest("figure")?.querySelector("figcaption")?.textContent || "";
    if (cap.trim()) return cap.trim().replace(/\s+/g, " ").slice(0, 70);
    const src = img.currentSrc || img.src || "";
    try {
      const file = decodeURIComponent(new URL(src, location.href).pathname.split("/").pop() || "");
      return file.replace(/^\d+px-/, "").replace(/\.[a-z0-9]+$/i, "").replace(/[_-]+/g, " ").slice(0, 70);
    } catch {
      return "";
    }
  }

  // src/octopus.ts
  var N = 18;
  var TAU = Math.PI * 2;
  var rnd = (a, b) => a + Math.random() * (b - a);
  var dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  var angDiff = (a, b) => Math.atan2(Math.sin(a - b), Math.cos(a - b));
  var Octopus = class {
    constructor(w, h) {
      this.vel = { x: 0, y: 0 };
      this.heading = -Math.PI / 2;
      this.arms = [];
      this.loot = [];
      this.pulse = rnd(0, TAU);
      this.glow = 0;
      this.goalAt = 0;
      this.patch = null;
      this.patchAt = 0;
      this.open = 0;
      this.held = false;
      this.wobble = rnd(0, 10);
      this.taken = 0;
      this.size = rnd(0.9, 1.15);
      this.hue = 190;
      this.pos = { x: rnd(w * 0.3, w * 0.7), y: h + 120 };
      this.goal = { x: w / 2, y: h / 2 };
      const seg = 11.5 * this.size;
      for (let i = 0; i < 8; i++) {
        const root = Math.PI + (i - 3.5) * 0.36;
        const dir = Math.PI + (i - 3.5) * 0.62;
        const beads = [];
        for (let b = 0; b < N; b++) beads.push({ x: this.pos.x, y: this.pos.y + b * seg, px: this.pos.x, py: this.pos.y + b * seg });
        this.arms.push({ root, dir, beads, seg, state: "idle", tip: { ...this.pos }, item: null, link: null, t: 0, phase: rnd(0, TAU), cool: rnd(0.3, 1.5) });
      }
    }
    get reach() {
      return (N - 1) * this.arms[0].seg;
    }
    toWorld(lx, ly) {
      const c = Math.cos(this.heading), s = Math.sin(this.heading);
      return { x: this.pos.x + lx * c - ly * s, y: this.pos.y + lx * s + ly * c };
    }
    /** Where an arm is attached: on the rim of the mantle's rear. */
    base(a) {
      const L = 33 * this.size, W = 21 * this.size;
      return this.toWorld(Math.cos(a.root) * L * 0.8, Math.sin(a.root) * W * 0.9);
    }
    /** Let go of everything (picked up, page swapped, removed). */
    releaseAll() {
      for (const a of this.arms) {
        if (a.item && !a.item.taken) a.item.claimed = false;
        a.item = null;
        a.link = null;
        a.state = "idle";
        a.cool = rnd(0.2, 0.8);
      }
    }
    update(dt, now, host) {
      this.glow = Math.max(0, this.glow - dt * 2.5);
      if (!this.held) this.swim(dt, now, host);
      for (const a of this.arms) this.arm(a, dt, now, host);
      this.haul(dt, host);
    }
    swim(dt, now, host) {
      const hop = host.hopTarget(this);
      let goal = null;
      if (hop && hop.isConnected) {
        const r = hop.getBoundingClientRect();
        const c = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        goal = dist(this.pos, c) > this.reach * 0.55 ? { x: c.x, y: c.y + this.reach * 0.35 } : null;
        if (!goal) this.vel.x *= 1 - Math.min(1, dt * 3), this.vel.y *= 1 - Math.min(1, dt * 3);
      } else {
        if (now > this.patchAt) {
          this.patchAt = now + 500;
          const pts = [];
          for (const it of host.items()) {
            if (it.taken || it.claimed) continue;
            const c = host.center(it);
            if (c && c.y > -20 && c.y < host.h + 20) pts.push(c);
          }
          this.open = pts.length;
          let best = null, bestS = Infinity;
          for (const c of pts) {
            let near = 0;
            for (const q of pts) if (q !== c && Math.abs(q.x - c.x) < 170 && Math.abs(q.y - c.y) < 170) near++;
            const sc = dist(c, this.pos) - near * 30;
            if (sc < bestS) {
              bestS = sc;
              best = c;
            }
          }
          this.patch = best;
        }
        if (this.patch) goal = { x: this.patch.x, y: this.patch.y + 45 };
        host.scrollWanted(this, this.open < 3, dt);
        if (!goal) {
          if (now > this.goalAt) {
            this.goal = { x: rnd(host.w * 0.2, host.w * 0.8), y: rnd(host.h * 0.3, host.h * 0.75) };
            this.goalAt = now + rnd(3e3, 6e3);
          }
          goal = this.goal;
        }
      }
      let ax = 0, ay = 0;
      if (goal) {
        const d = dist(goal, this.pos);
        if (d > 30) {
          ax += (goal.x - this.pos.x) / d * Math.min(1, d / 200);
          ay += (goal.y - this.pos.y) / d * Math.min(1, d / 200);
        }
      }
      for (const o of host.others()) {
        if (o === this) continue;
        const d = dist(o.pos, this.pos);
        if (d > 0.1 && d < 160) {
          ax += (this.pos.x - o.pos.x) / d * (160 - d) / 80;
          ay += (this.pos.y - o.pos.y) / d * (160 - d) / 80;
        }
      }
      const ptr = host.pointer();
      if (ptr.live && ptr.mode !== "off") {
        const d = dist(ptr.p, this.pos);
        if (ptr.mode === "flee" && d < 170 && d > 0.5) {
          ax += (this.pos.x - ptr.p.x) / d * 2.4 * (170 - d) / 170;
          ay += (this.pos.y - ptr.p.y) / d * 2.4 * (170 - d) / 170;
        }
        if (ptr.mode === "chase" && d > 60) {
          ax += (ptr.p.x - this.pos.x) / d * 1.4;
          ay += (ptr.p.y - this.pos.y) / d * 1.4;
        }
      }
      this.pulse += dt * 2.1;
      const thrust = Math.pow(Math.max(0, Math.sin(this.pulse)), 2);
      const acc = 260 * (0.25 + thrust);
      this.vel.x += ax * acc * dt;
      this.vel.y += ay * acc * dt;
      const drag = 1 - Math.min(1, dt * 1.6);
      this.vel.x *= drag;
      this.vel.y *= drag;
      this.vel.y += Math.sin(now / 900 + this.wobble) * 6 * dt;
      this.pos.x += this.vel.x * dt;
      this.pos.y += this.vel.y * dt;
      const m = 40;
      if (this.pos.x < m) this.vel.x += (m - this.pos.x) * dt * 8;
      if (this.pos.x > host.w - m) this.vel.x -= (this.pos.x - host.w + m) * dt * 8;
      if (this.pos.y < m) this.vel.y += (m - this.pos.y) * dt * 8;
      if (this.pos.y > host.h - m) this.vel.y -= (this.pos.y - host.h + m) * dt * 8;
      const v = Math.hypot(this.vel.x, this.vel.y);
      const want = v > 25 ? Math.atan2(this.vel.y, this.vel.x) : -Math.PI / 2 + Math.sin(now / 1400 + this.wobble) * 0.25;
      this.heading += angDiff(want, this.heading) * Math.min(1, dt * (v > 25 ? 2 : 0.8));
    }
    arm(a, dt, now, host) {
      const base = this.base(a);
      a.cool -= dt;
      const hop = host.hopTarget(this);
      if (a.state === "idle" && a.cool <= 0 && !this.held) {
        if (hop && hop.isConnected && !this.arms.some((o) => o.link)) {
          const r = hop.getBoundingClientRect(), c = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
          const best = this.arms.filter((o) => o.state === "idle").sort((p, q) => dist(this.base(p), c) - dist(this.base(q), c))[0];
          if (best === a && dist(base, c) < this.reach * 0.92) {
            a.link = hop;
            a.state = "reach";
            a.tip = { ...a.beads[N - 1] };
          }
        } else if (!hop) {
          this.pick(a, base, host);
        }
      }
      if (a.state !== "idle") {
        let c = null;
        if (a.link) {
          if (a.link.isConnected) {
            const r = a.link.getBoundingClientRect();
            c = { x: r.left + Math.min(r.width / 2, 40), y: r.top + r.height / 2 };
          }
        } else if (a.item) c = host.center(a.item);
        if (!c || dist(base, c) > this.reach * 1.02 || this.held) {
          this.letGo(a);
        } else if (a.state === "reach") {
          const d = dist(a.tip, c), step = 620 * dt;
          if (d <= step) {
            a.tip = c;
            a.state = "grip";
            a.t = a.link ? 99 : rnd(0.35, 0.75);
            if (a.link) host.hopGripped(this);
            else if (a.item) host.grip(this, a.item);
          } else {
            a.tip.x += (c.x - a.tip.x) / d * step;
            a.tip.y += (c.y - a.tip.y) / d * step;
          }
        } else {
          a.tip = c;
          a.t -= dt;
          if (a.t <= 0 && a.item) {
            const it = a.item;
            it.taken = true;
            a.item = null;
            a.state = "idle";
            a.cool = rnd(0.15, 0.5);
            host.take(this, it);
            this.loot.push({ text: lootText(it), kind: it.kind, color: MARK_COLOR[it.kind], arm: a, k: N - 1, angle: rnd(-0.5, 0.5), done: 0 });
            this.taken++;
          }
        }
      }
      this.chain(a, base, now);
    }
    /** Reach for the nearest unclaimed data in this arm's part of the sky. */
    pick(a, base, host) {
      let best = null, bestS = Infinity;
      const natural = this.heading + a.dir;
      for (const it of host.items()) {
        if (it.claimed || it.taken) continue;
        const c = host.center(it);
        if (!c) continue;
        const d = dist(c, base);
        if (d > this.reach * 0.9) continue;
        const off = Math.abs(angDiff(Math.atan2(c.y - base.y, c.x - base.x), natural));
        const s = d + off * 90;
        if (s < bestS) {
          bestS = s;
          best = it;
        }
      }
      if (!best) {
        a.cool = rnd(0.3, 0.7);
        return;
      }
      best.claimed = true;
      a.item = best;
      a.state = "reach";
      a.tip = { ...a.beads[N - 1] };
    }
    letGo(a) {
      if (a.item && !a.item.taken) a.item.claimed = false;
      a.item = null;
      a.link = null;
      a.state = "idle";
      a.cool = rnd(0.3, 0.9);
    }
    /** Bead chain: verlet with a soft pull toward a curling rest pose, or solved straight to a pinned tip. */
    chain(a, base, now) {
      const b = a.beads, seg = a.seg;
      const pinned = a.state !== "idle";
      const natural = this.heading + a.dir;
      const damp = this.held ? 0.9 : 0.86;
      for (let i = 1; i < N; i++) {
        const p = b[i];
        const vx = (p.x - p.px) * damp, vy = (p.y - p.py) * damp;
        p.px = p.x;
        p.py = p.y;
        p.x += vx;
        p.y += vy;
        if (!pinned) {
          const f = i / (N - 1);
          const curl = Math.sin(now / 900 + a.phase + f * 3.2) * 0.9 * f + Math.sin(now / 2300 + a.phase) * 0.4;
          let ang = natural, x = base.x, y = base.y;
          for (let k = 1; k <= i; k++) {
            ang += curl / N * 2.2;
            x += Math.cos(ang) * seg * 0.9;
            y += Math.sin(ang) * seg * 0.9;
          }
          const pull = this.held ? 0.01 : 0.045;
          p.x += (x - p.x) * pull;
          p.y += (y - p.y) * pull;
        }
      }
      b[0].x = base.x;
      b[0].y = base.y;
      b[0].px = base.x;
      b[0].py = base.y;
      if (pinned) {
        const tip = a.tip;
        for (let it = 0; it < 3; it++) {
          b[N - 1].x = tip.x;
          b[N - 1].y = tip.y;
          for (let i = N - 2; i >= 0; i--) fix(b[i], b[i + 1], seg);
          b[0].x = base.x;
          b[0].y = base.y;
          for (let i = 1; i < N; i++) fix(b[i], b[i - 1], seg);
        }
        for (let i = 1; i < N - 1; i++) {
          const f = i / (N - 1), w = Math.sin(now / 140 + a.phase + i * 0.7) * 1.4 * Math.sin(f * Math.PI);
          const dx = b[i + 1].x - b[i - 1].x, dy = b[i + 1].y - b[i - 1].y, l = Math.hypot(dx, dy) || 1;
          b[i].x += -dy / l * w;
          b[i].y += dx / l * w;
        }
      } else {
        for (let it = 0; it < 4; it++) {
          for (let i = 1; i < N; i++) fix(b[i], b[i - 1], seg);
        }
      }
    }
    /** Loot rides down the arm into the mantle, then is absorbed. */
    haul(dt, host) {
      for (const l of this.loot) {
        if (l.k > 0) l.k = Math.max(0, l.k - dt * 15);
        else l.done += dt * 3;
        l.angle *= 1 - Math.min(1, dt * 0.8);
      }
      const before = this.loot.length;
      this.loot = this.loot.filter((l) => {
        if (l.done < 1) return true;
        host.absorb(this, l.kind);
        return false;
      });
      if (this.loot.length < before) this.glow = 1;
    }
    // ---------- Drawing ----------
    draw(c, now, mono) {
      const k = this.size;
      for (const a of this.arms) {
        const b = a.beads;
        c.strokeStyle = "rgba(255, 236, 160, 0.45)";
        c.lineWidth = 1;
        c.beginPath();
        c.moveTo(b[0].x, b[0].y);
        for (let i = 1; i < N; i++) c.lineTo(b[i].x, b[i].y);
        c.stroke();
        for (let i = N - 1; i >= 0; i--) {
          const f = i / (N - 1), r = (3.3 - f * 2.1) * k;
          c.fillStyle = i % 2 ? "#ffffff" : "#ffd84d";
          c.strokeStyle = "rgba(20, 16, 4, 0.75)";
          c.lineWidth = 0.8;
          c.beginPath();
          c.arc(b[i].x, b[i].y, r, 0, TAU);
          c.fill();
          c.stroke();
        }
        if (a.state === "grip") {
          const t = b[N - 1];
          c.strokeStyle = a.link ? "#5cff8a" : a.item ? MARK_COLOR[a.item.kind] : "#fff";
          c.lineWidth = 1.2;
          c.beginPath();
          c.arc(t.x, t.y, 5 + Math.sin(now / 90) * 1.5, 0, TAU);
          c.stroke();
        }
      }
      const thrust = Math.pow(Math.max(0, Math.sin(this.pulse)), 2);
      const L = 36 * k * (1 + thrust * 0.06), W = 23 * k * (1 - thrust * 0.1);
      const line = `hsl(${this.hue}, 100%, 62%)`;
      c.save();
      c.translate(this.pos.x, this.pos.y);
      c.rotate(this.heading);
      c.shadowColor = line;
      c.shadowBlur = 10 + this.glow * 14;
      c.fillStyle = `hsla(${this.hue}, 100%, 55%, ${0.13 + this.glow * 0.15})`;
      c.strokeStyle = line;
      c.lineWidth = 1.6;
      c.beginPath();
      c.ellipse(6 * k, 0, L, W, 0, 0, TAU);
      c.fill();
      c.stroke();
      c.shadowBlur = 0;
      c.globalAlpha = 0.38;
      c.lineWidth = 0.8;
      c.beginPath();
      for (const f of [0.35, 0.65]) {
        c.moveTo(6 * k + L * f + 4, 0);
        c.ellipse(6 * k, 0, L * f + 4, W, 0, 0, TAU);
      }
      for (const f of [-0.55, 0, 0.55]) {
        c.moveTo(6 * k - L, 0);
        c.quadraticCurveTo(6 * k, W * f * 1.9, 6 * k + L, 0);
      }
      c.stroke();
      c.globalAlpha = 1;
      const dia = (x, y, r, col) => {
        c.fillStyle = col;
        c.beginPath();
        c.moveTo(x + r, y);
        c.lineTo(x, y + r);
        c.lineTo(x - r, y);
        c.lineTo(x, y - r);
        c.closePath();
        c.fill();
      };
      c.shadowColor = "#b36bff";
      c.shadowBlur = 6;
      dia(-13 * k, -10 * k, 4.6 * k, "#9b6bff");
      dia(-13 * k, 10 * k, 4.6 * k, "#9b6bff");
      const beat = 1 + Math.max(0, Math.sin(now / 220)) * 0.15 + this.glow * 0.5;
      c.shadowColor = "#ff3d8b";
      c.shadowBlur = 8 + this.glow * 12;
      dia(6 * k, 0, 7 * k * beat, "#ff3d6e");
      c.restore();
      c.font = `700 10px ${mono}`;
      c.textBaseline = "middle";
      c.textAlign = "center";
      for (const l of this.loot) {
        const b = l.arm.beads, i = Math.floor(l.k), f = l.k - i;
        const p0 = b[Math.min(N - 1, i)], p1 = b[Math.min(N - 1, i + 1)];
        let x = p0.x + (p1.x - p0.x) * f, y = p0.y + (p1.y - p0.y) * f;
        let scale = 0.55 + 0.45 * (l.k / (N - 1));
        if (l.k <= 0) {
          x += (this.pos.x - x) * l.done;
          y += (this.pos.y - y) * l.done;
          scale *= 1 - l.done * 0.9;
        }
        const tw = c.measureText(l.text).width + 8;
        c.save();
        c.translate(x, y);
        c.rotate(l.angle + Math.atan2(p1.y - p0.y, p1.x - p0.x) * 0.15);
        c.scale(scale, scale);
        c.globalAlpha = Math.max(0, 0.95 - (l.k <= 0 ? l.done * 0.6 : 0));
        c.fillStyle = l.color;
        c.fillRect(-tw / 2, -7, tw, 14);
        c.fillStyle = "#0b0f1a";
        c.fillText(l.text, 0, 0.5);
        c.restore();
      }
      c.globalAlpha = 1;
    }
    /** Is this point on the mantle (for picking up)? */
    hit(p) {
      return dist(p, this.pos) < 40 * this.size;
    }
  };
  function fix(p, q, seg) {
    const dx = p.x - q.x, dy = p.y - q.y, d = Math.hypot(dx, dy) || 1e-3;
    p.x = q.x + dx / d * seg;
    p.y = q.y + dy / d * seg;
  }
  function lootText(it) {
    const t = it.label.replace(/\s+/g, " ");
    return t.length > 30 ? t.slice(0, 29) + "\u2026" : t;
  }

  // src/skitter.ts
  var EAT_RATE = 14;
  var EAT_MAX = 28;
  var EAT_TEXT_LIMIT = 240;
  var DEFAULT_FOOD = "a, button, .btn, .badge, .pill, .chip, h1, h2, h3, td, th, label, code, [data-crawl]";
  var DEFAULT_PREY = ".warn, .bad, .danger, .banner-error, .errbar, .offline, [data-crawl-priority]";
  var PALETTE = [
    { leg: "#ff4f7b", joint: "#5cff8a", body: "#4d7cff", tag: "#ff2bd6" },
    { leg: "#ff7a45", joint: "#5cff8a", body: "#3fd0ff", tag: "#ff6a3d" },
    { leg: "#c86bff", joint: "#ffe14d", body: "#4dffd2", tag: "#7b5cff" },
    { leg: "#2ee6c5", joint: "#ff4fa3", body: "#5a8bff", tag: "#2b7bff" }
  ];
  var HUES = [42, 350, 190, 275, 95, 18];
  var MONO = "ui-monospace, SFMono-Regular, Consolas, monospace";
  var IGNORE = "[data-skitter-ignore]";
  var IMGS = "img, video, svg[role=img], canvas[data-crawl]";
  var COVER = "img, video, figure, table, .card, .tile, .infobox, [data-crawl-hide]";
  var LEAVES = "span, div, strong, b, em, small, time, output, data";
  var SANS = "system-ui, 'Segoe UI', sans-serif";
  var TAU2 = Math.PI * 2;
  var HIP = [[8.6, 4], [6.2, 5.3], [3.4, 5.6], [0.6, 4.7]];
  var DWELL = { word: 0.02, lnk: 0.55, ent: 0.4, num: 0.3, date: 0.3, val: 0.35, net: 0.4, title: 0.6 };
  var SKITTER_CSS = "html.skitter-grab,html.skitter-grab *{cursor:grab!important}html.skitter-grabbing,html.skitter-grabbing *{cursor:grabbing!important;user-select:none!important}.skitter-ch{transition:opacity .14s ease}.skitter-sr{position:absolute!important;width:1px!important;height:1px!important;overflow:hidden!important;clip:rect(0 0 0 0)!important;white-space:nowrap!important;margin:-1px!important;padding:0!important;border:0!important}";
  var rnd2 = (a, b) => a + Math.random() * (b - a);
  var clamp01 = (x) => x > 0 ? x < 1 ? x : 1 : 0;
  var dist2 = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  var ZERO = () => ({ x: 0, y: 0 });
  var hueColors = (h) => ({
    leg: `hsl(${h}, 95%, 62%)`,
    joint: `hsl(${h}, 100%, 82%)`,
    body: `hsl(${h}, 95%, 62%)`,
    tag: `hsl(${h}, 80%, 42%)`
  });
  var onPop = () => location.reload();
  var Skitter = class {
    constructor(opts) {
      this.spiders = [];
      this.octopi = [];
      this.count = 0;
      this.rects = [];
      this.imgs = [];
      this.rectsAt = 0;
      this.raf = 0;
      this.last = 0;
      this.mouse = { x: -1e4, y: -1e4 };
      this.mouseAt = 0;
      this.hold = null;
      this.strands = [];
      this.orbs = [];
      this.meals = /* @__PURE__ */ new Map();
      this.crumbs = [];
      this.bitten = /* @__PURE__ */ new Map();
      this.marks = [];
      this.readDone = /* @__PURE__ */ new WeakMap();
      this.scannedImgs = /* @__PURE__ */ new WeakSet();
      this.readLinks = /* @__PURE__ */ new WeakSet();
      this.items = [];
      this.itemsAt = 0;
      this.harvested = /* @__PURE__ */ new WeakSet();
      this.centers = /* @__PURE__ */ new Map();
      this.scraped = [];
      this.scrapedKeys = /* @__PURE__ */ new Set();
      this.hop = null;
      this.hopAt = 0;
      this.trail = [];
      this.visited = /* @__PURE__ */ new Set();
      this.pushed = false;
      this.fade = null;
      this.scrollAcc = 0;
      this.stats = { words: 0, ent: 0, lnk: 0, img: 0 };
      this.tally = /* @__PURE__ */ new Map();
      this.dead = false;
      this.w = 0;
      this.h = 0;
      this.onVisibility = () => {
        if (document.hidden) cancelAnimationFrame(this.raf);
        else this.start();
      };
      this.onPointer = (e) => {
        const now = performance.now();
        this.mouse = { x: e.clientX, y: e.clientY };
        this.mouseAt = now;
        const h = this.hold;
        if (h && e.pointerId === h.id) {
          const dt = Math.max(1e-3, (now - h.at) / 1e3);
          const v = { x: (e.clientX - h.last.x) / dt, y: (e.clientY - h.last.y) / dt };
          h.vel = { x: h.vel.x * 0.6 + v.x * 0.4, y: h.vel.y * 0.6 + v.y * 0.4 };
          h.last = { ...this.mouse };
          h.at = now;
          const body = h.s ?? h.o;
          body.pos = { x: e.clientX + h.off.x, y: e.clientY + h.off.y };
          return;
        }
        if (this.grab) this.setCursor(this.bodyAt(this.mouse) ? "skitter-grab" : "");
      };
      this.onPointerOut = (e) => {
        if (!e.relatedTarget && !this.hold) this.mouse = { x: -1e4, y: -1e4 };
      };
      this.onDown = (e) => {
        if (!this.grab || this.hold || e.button > 0) return;
        const p = { x: e.clientX, y: e.clientY };
        const s = this.spiderAt(p), o = s ? void 0 : this.octopi.find((x) => x.hit(p));
        if (!s && !o) return;
        e.preventDefault();
        e.stopPropagation();
        window.addEventListener("click", this.swallow, { capture: true, once: true });
        if (this.hop && (this.hop.who === s || this.hop.who === o) && this.hop.phase !== "swap") this.abortHop();
        if (s) {
          if (s.haul) this.dropHaul(s);
          s.held = true;
          s.air = null;
          s.silk = null;
          s.chainLeft = 0;
          s.chew = 0;
          s.target = null;
          s.reading = null;
          s.spinOrb = null;
          s.lurk = null;
        } else if (o) {
          o.held = true;
          o.releaseAll();
        }
        const body = s ?? o;
        this.hold = { s: s ?? null, o: o ?? null, id: e.pointerId, off: { x: body.pos.x - p.x, y: body.pos.y - p.y }, vel: ZERO(), at: performance.now(), last: p };
        this.setCursor("skitter-grabbing");
      };
      this.swallow = (e) => {
        e.preventDefault();
        e.stopPropagation();
      };
      this.onUp = (e) => {
        if (!this.hold || e.pointerId !== this.hold.id) return;
        e.stopPropagation();
        this.release();
        setTimeout(() => window.removeEventListener("click", this.swallow, { capture: true }), 0);
      };
      this.resize = () => {
        const dpr = Math.min(2, window.devicePixelRatio || 1);
        this.w = window.innerWidth;
        this.h = window.innerHeight;
        this.canvas.width = Math.round(this.w * dpr);
        this.canvas.height = Math.round(this.h * dpr);
        this.canvas.style.setProperty("width", `${this.w}px`, "important");
        this.canvas.style.setProperty("height", `${this.h}px`, "important");
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      };
      this.frame = (now) => {
        this.raf = requestAnimationFrame(this.frame);
        const dt = Math.max(0, Math.min(0.05, (now - this.last) / 1e3));
        this.last = now;
        try {
          if (!this.canvas.isConnected) document.documentElement.appendChild(this.canvas);
          this.refreshRects(now);
          this.centers.clear();
          for (const [el, until] of this.bitten) if (now > until || !el.isConnected) this.unbite(el);
          if (this.creature === "octopus") {
            this.refreshItems(now);
            for (const o of this.octopi) {
              try {
                o.update(dt, now, this.octoHost);
              } catch (e) {
                this.report(e);
                o.releaseAll();
              }
            }
          } else {
            for (const s of this.spiders) s.munch = false;
            for (const s of this.spiders) {
              try {
                this.update(s, dt, now);
              } catch (e) {
                this.report(e);
                this.recover(s);
              }
            }
          }
          this.tickHop(dt, now);
          this.maybeHop(now);
          this.tickMeals(now, dt);
        } catch (e) {
          this.report(e);
        }
        try {
          this.draw(now);
        } catch (e) {
          this.report(e);
          this.ctx.setTransform(1, 0, 0, 1, 0, 0);
          this.resize();
        }
      };
      this.reported = false;
      this.octoHost = /* @__PURE__ */ (() => {
        const self = this;
        return {
          get w() {
            return self.w;
          },
          get h() {
            return self.h;
          },
          items: () => self.items,
          center: (it) => {
            if (self.centers.has(it)) return self.centers.get(it);
            const q = firstRect(it.range);
            const c = q ? { x: q.left + Math.min(q.width, 160) / 2, y: q.top + q.height / 2 } : null;
            self.centers.set(it, c);
            return c;
          },
          grip: (_o, it) => {
            const label = it.label.length > 26 ? it.label.slice(0, 25) + "\u2026" : it.label;
            self.addMark({ kind: it.kind, range: it.range, tag: `${KIND_CODE[it.kind]} ${label}`, color: MARK_COLOR[it.kind], life: 16e3, solid: true });
          },
          take: (_o, it) => self.record(it),
          absorb: (o) => {
            for (let k = 0; k < 6; k++) {
              const a = rnd2(0, TAU2), v = rnd2(30, 90), life = rnd2(0.3, 0.6);
              self.crumbs.push({ x: o.pos.x, y: o.pos.y, vx: Math.cos(a) * v, vy: Math.sin(a) * v, life, max: life, size: rnd2(1.2, 2.2), color: "#ff8ab3" });
            }
          },
          pointer: () => ({ p: self.mouse, mode: self.mouseMode, live: self.mouse.x > -1e3 && performance.now() - self.mouseAt < 5e3 }),
          hopTarget: (o) => self.hop && self.hop.who === o && self.hop.phase !== "swap" ? self.hop.el : null,
          hopGripped: () => self.hopGrip(),
          scrollWanted: (o, idle, dt) => {
            if (!self.scrollOn || !idle || self.hop || o !== self.octopi[0]) {
              self.scrollAcc = 0;
              return;
            }
            const max = document.documentElement.scrollHeight - window.innerHeight;
            if (window.scrollY >= max - 2) {
              if (self.crawlOn) self.hopAt = Math.min(self.hopAt, performance.now() + 1500);
              return;
            }
            self.scrollAcc += 70 * dt;
            if (self.scrollAcc >= 1) {
              const px = Math.floor(self.scrollAcc);
              self.scrollAcc -= px;
              window.scrollBy(0, px);
            }
          },
          others: () => self.octopi
        };
      })();
      this.food = opts.food ?? DEFAULT_FOOD;
      this.prey = opts.prey ?? DEFAULT_PREY;
      this.label = opts.label ?? ((el) => (el.innerText || el.textContent || "").trim().split("\n")[0]);
      this.creature = opts.creature ?? "spider";
      this.look = opts.look ?? "real";
      this.mouseMode = opts.mouse ?? "flee";
      this.jump = !!opts.jump;
      this.grab = !!opts.grab;
      this.eat = !!opts.eat;
      this.fight = !!opts.fight;
      this.acrobat = !!opts.acrobat;
      this.carry = !!opts.carry;
      this.readOn = !!opts.read;
      this.crawlOn = !!opts.crawl;
      this.websOn = !!opts.webs;
      this.lurkOn = !!opts.lurk;
      this.scrollOn = !!opts.scroll;
      this.hudAuto = opts.hud === void 0;
      this.hudOn = opts.hud ?? this.crawlOn;
      this.crawlRoot = opts.crawlRoot ?? "body";
      this.hopEvery = Math.max(4, opts.hopEvery ?? 18);
      this.onHop = opts.onHop;
      this.history = opts.history ?? true;
      this.hopAt = performance.now() + this.hopEvery * 1e3 * 0.7;
      this.visited.add(location.href.split("#")[0]);
      if (!document.getElementById("skitter-css")) {
        const st = document.createElement("style");
        st.id = "skitter-css";
        st.textContent = SKITTER_CSS;
        document.head.appendChild(st);
      }
      this.canvas = document.createElement("canvas");
      this.canvas.className = "crawler-canvas";
      this.canvas.setAttribute("aria-hidden", "true");
      const css = {
        position: "fixed",
        inset: "0",
        left: "0",
        top: "0",
        margin: "0",
        padding: "0",
        border: "0",
        display: "block",
        visibility: "visible",
        opacity: "1",
        transform: "none",
        filter: "none",
        "max-width": "none",
        "max-height": "none",
        background: "transparent",
        "pointer-events": "none",
        "z-index": String(opts.zIndex ?? 9e3)
      };
      for (const [k, v] of Object.entries(css)) this.canvas.style.setProperty(k, v, "important");
      this.ctx = this.canvas.getContext("2d");
      document.documentElement.appendChild(this.canvas);
      this.resize();
      window.addEventListener("resize", this.resize);
      window.addEventListener("pointermove", this.onPointer, { passive: true });
      window.addEventListener("pointerout", this.onPointerOut);
      window.addEventListener("pointerdown", this.onDown, { capture: true });
      window.addEventListener("pointerup", this.onUp, { capture: true });
      window.addEventListener("pointercancel", this.onUp, { capture: true });
      document.addEventListener("visibilitychange", this.onVisibility);
      this.setCount(opts.count);
      this.start();
    }
    setCount(n) {
      this.count = Math.max(0, n);
      if (this.creature === "octopus") {
        while (this.octopi.length < this.count) this.octopi.push(new Octopus(this.w, this.h));
        for (const o of this.octopi.slice(this.count)) {
          o.releaseAll();
          if (this.hop?.who === o) this.abortHop();
        }
        if (this.hold?.o && this.octopi.indexOf(this.hold.o) >= this.count) this.release();
        this.octopi.length = this.count;
        return;
      }
      while (this.spiders.length < this.count) this.spiders.push(this.spawn());
      if (this.hold?.s && this.spiders.indexOf(this.hold.s) >= this.count) this.release();
      if (this.hop && this.spiders.indexOf(this.hop.who) >= this.count) this.abortHop();
      this.spiders.length = this.count;
    }
    /** Swap spiders for an octopus or back, keeping the count. */
    setCreature(c) {
      if (c === this.creature) return;
      this.release();
      this.abortHop();
      for (const s of this.spiders) if (s.haul) this.dropHaul(s);
      this.spiders = [];
      this.octopi = [];
      this.items = [];
      this.harvested = /* @__PURE__ */ new WeakSet();
      this.creature = c;
      this.setCount(this.count);
    }
    /** Change how spiders are drawn: "real" or "neon". */
    setLook(look) {
      this.look = look;
      this.spiders.forEach((s, i) => {
        s.colors = look === "neon" ? PALETTE[i % PALETTE.length] : hueColors(s.hue);
      });
    }
    /** Change how creatures react to the pointer without restarting. */
    setMouse(mode) {
      this.mouseMode = mode;
    }
    setJump(on) {
      this.jump = on;
    }
    setGrab(on) {
      this.grab = on;
      if (!on) {
        this.release();
        this.setCursor("");
      }
    }
    setEat(on) {
      this.eat = on;
      if (!on) this.restoreAll();
    }
    setCarry(on) {
      this.carry = on;
      if (!on) {
        for (const s of this.spiders) if (s.haul) this.dropHaul(s);
      }
    }
    setAcrobat(on) {
      this.acrobat = on;
      if (!on) for (const s of this.spiders) {
        if (s.silk) this.dropSilk(s);
        s.chainLeft = 0;
      }
    }
    setFight(on) {
      this.fight = on;
      if (!on) for (const s of this.spiders) s.duel = null;
    }
    setLurk(on) {
      this.lurkOn = on;
      if (!on) {
        for (const s of this.spiders) if (s.lurk) this.unlurk(s);
      }
    }
    setWebs(on) {
      this.websOn = on;
      if (!on) {
        this.strands = [];
        this.orbs = [];
        for (const s of this.spiders) {
          s.lastAnchor = null;
          s.recent = [];
          s.spinOrb = null;
        }
      }
    }
    setRead(on) {
      this.readOn = on;
      if (!on) {
        for (const s of this.spiders) {
          s.reading = null;
          s.tether = null;
        }
        if (this.creature === "spider") this.marks = this.marks.filter((m) => m.kind === "hop" || m.kind === "title");
      }
    }
    setCrawl(on) {
      this.crawlOn = on;
      if (this.hudAuto) this.hudOn = on;
      if (on) this.hopAt = performance.now() + this.hopEvery * 1e3 * 0.5;
      else this.abortHop();
    }
    setScroll(on) {
      this.scrollOn = on;
    }
    setHud(on) {
      this.hudOn = on;
      this.hudAuto = false;
    }
    /** Everything the octopus has scraped so far, across hops. */
    getScraped() {
      return this.scraped.map((x) => ({ ...x }));
    }
    clearScraped() {
      this.scraped = [];
      this.scrapedKeys.clear();
      this.tally.clear();
    }
    /** Pages visited while crawling, first to last: the crawl's sitemap. */
    getTrail() {
      if (!this.trail.length) this.trail.push({ url: location.href.split("#")[0], title: this.pageTitle() });
      return this.trail.map((x) => ({ ...x }));
    }
    destroy() {
      this.dead = true;
      cancelAnimationFrame(this.raf);
      window.removeEventListener("resize", this.resize);
      window.removeEventListener("pointermove", this.onPointer);
      window.removeEventListener("pointerout", this.onPointerOut);
      window.removeEventListener("pointerdown", this.onDown, { capture: true });
      window.removeEventListener("pointerup", this.onUp, { capture: true });
      window.removeEventListener("pointercancel", this.onUp, { capture: true });
      document.removeEventListener("visibilitychange", this.onVisibility);
      this.setCursor("");
      this.restoreAll();
      for (const el of this.bitten.keys()) this.unbite(el);
      this.canvas.remove();
    }
    // ---------- Eating ----------
    /** Wrap the element's letters in spans and plan which ones this spider eats. Returns how many. */
    startMeal(s, el) {
      const old = this.meals.get(el);
      if (old) {
        if (old.spider && old.spider !== s) return 0;
        old.eaten -= old.restored;
        old.restored = 0;
        const more = Math.min(EAT_MAX, old.chars.length - old.eaten);
        if (more <= 0) return 0;
        old.budget = old.eaten + more;
        old.spider = s;
        old.next = 0;
        return more;
      }
      if (el.isContentEditable || el.closest("input, textarea, select, [contenteditable]")) return 0;
      const texts = [];
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
        acceptNode: (n) => {
          const p = n.parentElement;
          if (!p || /^(SCRIPT|STYLE|NOSCRIPT)$/.test(p.tagName) || p.classList.contains("skitter-sr")) return NodeFilter.FILTER_REJECT;
          return /\S/.test(n.nodeValue || "") ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
        }
      });
      for (let n = walker.nextNode(); n; n = walker.nextNode()) texts.push(n);
      const full = texts.map((t) => t.nodeValue).join("");
      if (!texts.length || full.length > EAT_TEXT_LIMIT) return 0;
      const sr = document.createElement("span");
      sr.className = "skitter-sr";
      sr.textContent = (el.textContent || "").trim();
      const records = [], chars = [];
      for (const t of texts) {
        const frag = document.createDocumentFragment(), nodes = [];
        for (const ch of t.nodeValue || "") {
          let node;
          if (/\s/.test(ch)) node = document.createTextNode(ch);
          else {
            const sp = document.createElement("span");
            sp.className = "skitter-ch";
            sp.setAttribute("aria-hidden", "true");
            sp.textContent = ch;
            chars.push(sp);
            node = sp;
          }
          nodes.push(node);
          frag.appendChild(node);
        }
        t.parentNode.replaceChild(frag, t);
        records.push({ orig: t, nodes });
      }
      el.insertBefore(sr, el.firstChild);
      const mouth = { x: s.pos.x + Math.cos(s.heading) * 14 * s.size, y: s.pos.y + Math.sin(s.heading) * 14 * s.size };
      const order = chars.map((c, i) => {
        const r = c.getBoundingClientRect();
        return { i, d: Math.hypot(r.left + r.width / 2 - mouth.x, r.top + r.height / 2 - mouth.y) };
      }).sort((a, b) => a.d - b.d).map((o) => o.i);
      const budget = Math.max(1, Math.min(EAT_MAX, Math.ceil(chars.length * 0.6)));
      this.meals.set(el, {
        el,
        sr,
        records,
        chars,
        order,
        budget,
        eaten: 0,
        restored: 0,
        next: 0,
        regrowAt: 0,
        spider: s,
        color: getComputedStyle(el).color || "#fff"
      });
      return budget;
    }
    unwrap(m) {
      m.sr.remove();
      for (const r of m.records) {
        const first = r.nodes[0];
        if (first && first.parentNode) {
          first.parentNode.insertBefore(r.orig, first);
          for (const n of r.nodes) n.remove();
        }
      }
    }
    restoreAll() {
      for (const m of this.meals.values()) this.unwrap(m);
      this.meals.clear();
    }
    crumble(span, color, s) {
      const r = span.getBoundingClientRect();
      const away = s.heading + Math.PI;
      for (let k = 0; k < 4; k++) {
        const a = away + rnd2(-1.2, 1.2), sp = rnd2(30, 110), life = rnd2(0.5, 0.95);
        this.crumbs.push({
          x: r.left + Math.random() * r.width,
          y: r.top + Math.random() * r.height,
          vx: Math.cos(a) * sp,
          vy: Math.sin(a) * sp - rnd2(40, 110),
          life,
          max: life,
          size: rnd2(1.4, 2.8),
          color
        });
      }
      if (this.crumbs.length > 500) this.crumbs.splice(0, this.crumbs.length - 500);
    }
    tickMeals(now, dt) {
      for (const m of [...this.meals.values()]) {
        if (!m.el.isConnected) {
          this.meals.delete(m.el);
          continue;
        }
        const s = m.spider;
        if (s) {
          const still = this.spiders.includes(s) && s.target === m.el && s.chew > 0 && !s.held && !s.air && !s.duel;
          if (!still || m.eaten >= m.budget) {
            m.spider = null;
            m.regrowAt = now + 2500;
            m.next = 0;
          } else {
            s.munch = true;
            if (now >= m.next) {
              const sp = m.chars[m.order[m.eaten++]];
              sp.style.opacity = "0";
              this.crumble(sp, m.color, s);
              m.next = now + 1e3 / EAT_RATE;
            }
          }
        } else if (m.regrowAt === Infinity) {
          if (!this.spiders.some((o) => o.haul?.meal === m)) m.regrowAt = now + 1e3;
        } else if (now >= m.regrowAt && !this.claimant(m.el)) {
          if (m.restored < m.eaten) {
            if (now >= m.next) {
              m.chars[m.order[m.eaten - 1 - m.restored++]].style.opacity = "";
              m.next = now + 70;
            }
          } else if (now >= m.next + 250) {
            this.unwrap(m);
            this.meals.delete(m.el);
          }
        }
      }
      for (const c of this.crumbs) {
        c.vy += 700 * dt;
        c.x += c.vx * dt;
        c.y += c.vy * dt;
        c.life -= dt;
      }
      this.crumbs = this.crumbs.filter((c) => c.life > 0);
    }
    /** A spider currently heading for or sitting on this element. */
    claimant(el) {
      return this.spiders.find((o) => o.target === el) || null;
    }
    start() {
      cancelAnimationFrame(this.raf);
      this.last = performance.now();
      this.raf = requestAnimationFrame(this.frame);
    }
    spiderAt(p) {
      let best = null, bestD = Infinity;
      for (const s of this.spiders) {
        const d = Math.hypot(p.x - s.pos.x, p.y - (s.pos.y - s.lift));
        if (d < 20 * s.size && d < bestD) {
          best = s;
          bestD = d;
        }
      }
      return best;
    }
    bodyAt(p) {
      return this.spiderAt(p) || this.octopi.find((o) => o.hit(p)) || null;
    }
    /** Let go: a fast flick throws the creature, a gentle release drops it in place. */
    release() {
      const h = this.hold;
      if (!h) return;
      this.hold = null;
      const stale = performance.now() - h.at > 80;
      const v = stale ? ZERO() : h.vel;
      if (h.o) {
        h.o.held = false;
        h.o.vel = { x: v.x * 0.7, y: v.y * 0.7 };
      } else if (h.s) {
        const s = h.s;
        s.held = false;
        const speed = Math.hypot(v.x, v.y);
        if (speed > 450) this.leap(s, { x: s.pos.x + v.x * 0.3, y: s.pos.y + v.y * 0.3 }, 0.2);
        else s.air = { from: { ...s.pos }, to: { ...s.pos }, t: 0, dur: 0.16, h: 16 };
        s.cool = rnd2(0.8, 1.6);
      }
      this.setCursor(this.grab && this.bodyAt(this.mouse) ? "skitter-grab" : "");
    }
    setCursor(cls) {
      const root = document.documentElement;
      root.classList.toggle("skitter-grab", cls === "skitter-grab");
      root.classList.toggle("skitter-grabbing", cls === "skitter-grabbing");
    }
    /** Jump to `to` (kept on screen). `minDur` lets a fling feel quicker than a pounce. */
    leap(s, to, minDur = 0.28) {
      const t = this.w > 48 && this.h > 48 ? { x: Math.max(24, Math.min(this.w - 24, to.x)), y: Math.max(24, Math.min(this.h - 24, to.y)) } : { ...to };
      const d = dist2(s.pos, t);
      s.air = { from: { ...s.pos }, to: t, t: 0, dur: minDur + d / 900, h: Math.min(110, 18 + d * 0.35) };
      if (d > 4) s.heading = Math.atan2(t.y - s.pos.y, t.x - s.pos.x);
      s.vel = ZERO();
    }
    /** Put every foot straight down, gripping whatever is under it. */
    replant(s) {
      for (const l of s.legs) {
        const g = this.grip(this.restPoint(s, l));
        l.foot = g.p;
        l.from = { ...g.p };
        l.to = { ...g.p };
        l.grip = g.g;
        l.toGrip = g.g;
        l.t = 1;
      }
    }
    spawn() {
      const size = rnd2(0.75, 1.25);
      const edge = Math.floor(Math.random() * 4);
      const pos = edge === 0 ? { x: rnd2(0, this.w), y: -40 } : edge === 1 ? { x: this.w + 40, y: rnd2(0, this.h) } : edge === 2 ? { x: rnd2(0, this.w), y: this.h + 40 } : { x: -40, y: rnd2(0, this.h) };
      const heading = Math.atan2(this.h / 2 - pos.y, this.w / 2 - pos.x);
      const legs = [];
      const angles = [0.55, 1.15, 1.85, 2.5];
      for (let i = 0; i < 8; i++) {
        const side = i % 2 === 0 ? 1 : -1;
        const pair = i >> 1;
        const reach = (pair === 0 || pair === 3 ? 62 : 52) * size;
        const leg = {
          side,
          angle: angles[pair] * side,
          reach,
          upper: reach * rnd2(0.62, 0.72),
          lower: reach * rnd2(0.62, 0.72),
          group: (pair + (side > 0 ? 0 : 1)) % 2,
          foot: { ...pos },
          grip: { el: null, dx: 0, dy: 0 },
          from: { ...pos },
          to: { ...pos },
          toGrip: { el: null, dx: 0, dy: 0 },
          t: 1
        };
        legs.push(leg);
      }
      const hue = HUES[Math.floor(Math.random() * HUES.length)] + rnd2(-8, 8);
      const s = {
        pos,
        vel: ZERO(),
        heading,
        size,
        speed: rnd2(70, 120),
        hue,
        colors: this.look === "neon" ? PALETTE[Math.floor(Math.random() * PALETTE.length)] : hueColors(hue),
        legs,
        target: null,
        targetPoint: { x: this.w / 2, y: this.h / 2 },
        chew: 0,
        rest: rnd2(0, 1),
        torn: null,
        wobble: rnd2(0, 10),
        air: null,
        lift: 0,
        held: false,
        cool: rnd2(0.5, 2),
        munch: false,
        duel: null,
        silk: null,
        chainLeft: 0,
        haul: null,
        reading: null,
        tether: null,
        lastAnchor: null,
        recent: [],
        spinOrb: null,
        lurk: null
      };
      for (const l of legs) {
        l.foot = this.restPoint(s, l);
        l.from = { ...l.foot };
        l.to = { ...l.foot };
      }
      return s;
    }
    refreshRects(now) {
      if (now - this.rectsAt < 600) return;
      this.rectsAt = now;
      const out = [];
      const extra = this.readOn ? ", p, li, dd, figcaption, img" : this.websOn ? ", img" : "";
      const sel = `${this.food}, ${this.prey}${extra}`;
      const all = document.querySelectorAll(sel);
      for (const el of all) {
        if (out.length > 600) break;
        if (el.closest(IGNORE)) continue;
        const r = el.getBoundingClientRect();
        if (r.width < 6 || r.height < 6 || r.bottom < 0 || r.top > this.h || r.right < 0 || r.left > this.w) continue;
        if (r.width * r.height > this.w * this.h * 0.25) continue;
        out.push({ el, r, prey: el.matches(this.prey) });
      }
      this.rects = out;
      if ((this.readOn || this.websOn) && this.creature === "spider") {
        const im = [];
        for (const el of document.querySelectorAll("img, video, svg[role=img], canvas[data-crawl]")) {
          if (im.length > 80) break;
          const r = el.getBoundingClientRect();
          if (r.width < 48 || r.height < 40 || r.bottom < 0 || r.top > this.h || r.right < 0 || r.left > this.w) continue;
          if (r.width * r.height > this.w * this.h * 0.5 || el === this.canvas || el.closest(IGNORE)) continue;
          im.push({ el, r });
        }
        this.imgs = im;
      }
    }
    restPoint(s, l) {
      const a = s.heading + l.angle;
      return { x: s.pos.x + Math.cos(a) * l.reach, y: s.pos.y + Math.sin(a) * l.reach };
    }
    /** Snap a desired foothold onto the nearest element edge so feet grip the UI. */
    grip(p) {
      let best = null;
      let bestD = 34;
      for (const it of this.rects) {
        const cx = Math.max(it.r.left, Math.min(p.x, it.r.right));
        const cy = Math.max(it.r.top, Math.min(p.y, it.r.bottom));
        const d = Math.hypot(cx - p.x, cy - p.y);
        if (d < bestD) {
          bestD = d;
          best = it;
        }
      }
      if (!best) return { p, g: { el: null, dx: 0, dy: 0 } };
      const x = Math.max(best.r.left + 1, Math.min(p.x, best.r.right - 1));
      const y = Math.max(best.r.top + 1, Math.min(p.y, best.r.bottom - 1));
      return { p: { x, y }, g: { el: best.el, dx: x - best.r.left, dy: y - best.r.top } };
    }
    pickTarget(s) {
      if (this.fight && Math.random() < 0.35) {
        const rival = this.spiders.filter((o) => o !== s && o.target && o.chew > 0.8 && !o.duel && !o.held && !o.air && dist2(o.pos, s.pos) < 520).sort((a, b) => dist2(a.pos, s.pos) - dist2(b.pos, s.pos))[0];
        if (rival && rival.target && !this.spiders.some((o) => o !== s && o !== rival && o.target === rival.target)) {
          s.target = rival.target;
          s.targetPoint = { ...rival.pos };
          return;
        }
      }
      if (this.websOn && Math.random() < 0.18) {
        const im = this.imgs.find((it) => it.el.isConnected && !this.orbAlive(it.el) && !this.isClaimed(it.el, s));
        if (im) {
          s.target = im.el;
          s.targetPoint = { x: im.r.left + im.r.width / 2, y: im.r.top + im.r.height / 2 };
          return;
        }
      }
      const prey = this.rects.filter((r) => r.prey && !this.isClaimed(r.el, s));
      const pool = prey.length && Math.random() < (this.carry ? 0.55 : 0.8) ? prey : this.rects.filter((r) => !this.isClaimed(r.el, s));
      if (!pool.length) {
        s.target = null;
        s.targetPoint = { x: rnd2(60, this.w - 60), y: rnd2(60, this.h - 60) };
        return;
      }
      const pick = pool.map((r) => ({ r, d: dist2(s.pos, { x: r.r.left + r.r.width / 2, y: r.r.top + r.r.height / 2 }) + rnd2(0, 900) })).sort((a, b) => a.d - b.d)[0].r;
      s.target = pick.el;
      s.targetPoint = { x: pick.r.left + pick.r.width / 2, y: pick.r.top + pick.r.height / 2 };
    }
    isClaimed(el, self) {
      return this.spiders.some((o) => o !== self && o.target === el);
    }
    /** Returns how long to chew when eating sets the pace, otherwise null. */
    bite(s, el, prey) {
      const h = el;
      h.style.setProperty("--crawler-color", prey ? "#ff3b5c" : s.colors.tag);
      h.classList.add("crawler-bitten");
      if (prey) h.classList.add("crawler-prey");
      this.bitten.set(el, performance.now() + (prey ? 9e3 : 4500));
      if (this.websOn) {
        if (el.matches(IMGS) && !this.orbAlive(el)) {
          this.startOrb(s, el);
          return 0.05;
        }
        this.link(s, { range: null, el, fx: rnd2(0.15, 0.85), fy: rnd2(0.2, 0.85) });
      }
      if (this.carry && !prey && Math.random() < 0.6) {
        const text2 = this.label(el).trim().slice(0, 42);
        if (text2) {
          this.snatch(s, h, text2);
          return 0.35;
        }
      }
      if (this.eat && !prey) {
        const n = this.startMeal(s, h);
        if (n) return Math.min(6, n / EAT_RATE + 0.6);
      }
      const text = this.label(el).trim().slice(0, 42);
      if (!prey && text && Math.random() < 0.55) {
        this.ctx.font = `600 13px ${MONO}`;
        s.torn = { text, w: this.ctx.measureText(text).width + 14, color: s.colors.tag, angle: rnd2(-0.6, 0.6), pos: { ...s.pos }, life: rnd2(4, 7) };
      }
      return null;
    }
    unbite(el) {
      const h = el;
      h.classList.remove("crawler-bitten", "crawler-prey");
      h.style.removeProperty("--crawler-color");
      this.bitten.delete(el);
    }
    report(e) {
      if (this.reported) return;
      this.reported = true;
      console.warn("Skitter: recovered from an error (shown once).", e);
    }
    /** Put a spider that hit an error back on its feet somewhere sensible. */
    recover(s) {
      s.silk = null;
      s.air = null;
      s.duel = null;
      s.held = false;
      s.lift = 0;
      s.chainLeft = 0;
      s.target = null;
      s.chew = 0;
      s.reading = null;
      s.spinOrb = null;
      s.lurk = null;
      if (this.hop?.who === s) this.abortHop();
      if (!Number.isFinite(s.pos.x) || !Number.isFinite(s.pos.y)) s.pos = { x: this.w / 2, y: this.h / 2 };
      this.replant(s);
    }
    update(s, dt, now) {
      this.dragLabels(s, dt, now);
      this.tetherFollow(s);
      if (s.held) {
        this.dangle(s, now);
        return;
      }
      if (s.air) {
        this.fly(s, dt);
        return;
      }
      if (s.silk) {
        this.swing(s, dt, now);
        return;
      }
      if (s.duel) {
        this.brawl(s, dt);
        return;
      }
      s.cool -= dt;
      const hopping = this.hop?.who === s;
      if (this.fight && s.target && s.chew <= 0 && !hopping) {
        const owner = this.spiders.find((o) => o !== s && o.target === s.target && o.chew > 0 && !o.duel && !o.held && !o.air);
        if (owner && dist2(s.pos, owner.pos) < 48) {
          this.startDuel(s, owner);
          return;
        }
      }
      let shift = { x: 0, y: 0 }, n = 0;
      for (const l of s.legs) {
        if (l.t < 1 || !l.grip.el) continue;
        if (!l.grip.el.isConnected) {
          l.grip.el = null;
          continue;
        }
        const r = l.grip.el.getBoundingClientRect();
        const np = { x: r.left + l.grip.dx, y: r.top + l.grip.dy };
        shift.x += np.x - l.foot.x;
        shift.y += np.y - l.foot.y;
        n++;
        l.foot = np;
      }
      if (n) {
        s.pos.x += shift.x / n;
        s.pos.y += shift.y / n;
        s.targetPoint.x += shift.x / n;
        s.targetPoint.y += shift.y / n;
      }
      if (s.target && s.target.isConnected && s.chew <= 0) {
        const r = s.target.getBoundingClientRect();
        s.targetPoint = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
      }
      const toT = { x: s.targetPoint.x - s.pos.x, y: s.targetPoint.y - s.pos.y };
      const d = Math.hypot(toT.x, toT.y);
      let desired = ZERO();
      const md = dist2(s.pos, this.mouse);
      const mouseLive = this.mouse.x > -1e3 && now - this.mouseAt < 5e3;
      const chasing = this.mouseMode === "chase" && mouseLive && !hopping;
      if (this.acrobat && !chasing && !hopping && !s.lurk && !s.spinOrb && !s.reading && !s.haul && s.chew <= 0 && s.cool <= 0) {
        if (s.target && d > 170 && Math.random() < dt * 0.9) {
          if (this.startSilk(s, "swing")) {
            s.chainLeft = Math.random() < 0.45 ? 1 + Math.floor(Math.random() * 2) : 0;
            return;
          }
        } else if (!s.target && s.rest > 0 && Math.random() < dt * 0.35) {
          if (this.startSilk(s, "hang")) return;
        }
      }
      if (hopping) {
        desired = this.hopStep(s, now);
      } else if (chasing) {
        s.chew = 0;
        s.target = null;
        s.rest = 0.6;
        s.reading = null;
        s.spinOrb = null;
        if (s.lurk) this.unlurk(s);
        const ring = (30 + s.wobble % 3 * 9) * s.size, ang = s.wobble * 2.4;
        const spot = { x: this.mouse.x + Math.cos(ang) * ring, y: this.mouse.y + Math.sin(ang) * ring };
        const sd = dist2(s.pos, spot);
        if (this.jump && s.cool <= 0 && md > 70 && md < 280 && Math.random() < dt * 1.6) {
          this.leap(s, spot);
          s.cool = rnd2(1.2, 2.6);
          return;
        }
        if (sd > 6) {
          const a = Math.atan2(spot.y - s.pos.y, spot.x - s.pos.x) + Math.sin(now / 700 + s.wobble) * 0.2;
          const sp = s.speed * 1.5 * Math.min(1, sd / 90 + 0.2);
          desired = { x: Math.cos(a) * sp, y: Math.sin(a) * sp };
        }
      } else if (s.lurk) {
        desired = this.lurkStep(s, dt, now);
        if (!s.lurk || s.air) {
          if (s.air) return;
        }
      } else if (s.spinOrb) {
        desired = this.orbStep(s, now);
      } else if (s.haul && s.chew <= 0) {
        desired = this.haulStep(s, now);
      } else if (s.reading && s.chew <= 0) {
        desired = this.readStep(s, dt, now);
      } else if (s.chew > 0) {
        s.chew -= dt;
        if (s.chew <= 0) {
          s.target = null;
          s.rest = rnd2(0.3, 1.2);
        }
      } else if (s.rest > 0) {
        s.rest -= dt;
        if (s.rest <= 0) {
          const preyUp = this.rects.some((r) => r.prey);
          if (this.lurkOn && Math.random() < 0.4 && this.startLurk(s)) {
          } else if (!(this.readOn && Math.random() < (preyUp ? 0.45 : 0.8) && this.startReading(s))) this.pickTarget(s);
        }
      } else if (d < 14) {
        const entry = s.target ? this.rects.find((r) => r.el === s.target) : void 0;
        const meal = s.target ? this.bite(s, s.target, !!entry?.prey) : null;
        s.chew = meal ?? (entry?.prey ? rnd2(5, 8) : rnd2(1.2, 2.8));
      } else {
        const wob = Math.sin(now / 900 + s.wobble) * 0.35;
        const a = Math.atan2(toT.y, toT.x) + wob;
        const sp = s.speed * Math.min(1, d / 120 + 0.25);
        desired = { x: Math.cos(a) * sp, y: Math.sin(a) * sp };
      }
      if (s.air) return;
      const hidden = !!s.lurk && s.lurk.phase !== "go";
      if (this.mouseMode === "flee" && md < 110 && md > 0.5 && !hidden && !(hopping && this.hop.phase !== "go")) {
        if (this.jump && s.cool <= 0 && md < 80) {
          const k2 = rnd2(140, 210) / md;
          this.leap(s, { x: s.pos.x + (s.pos.x - this.mouse.x) * k2, y: s.pos.y + (s.pos.y - this.mouse.y) * k2 });
          s.cool = rnd2(1.5, 3);
          s.chew = 0;
          return;
        }
        const k = (110 - md) / 110;
        desired.x += (s.pos.x - this.mouse.x) / md * 420 * k;
        desired.y += (s.pos.y - this.mouse.y) / md * 420 * k;
        if (s.chew > 0) s.chew = Math.min(s.chew, 0.2);
      }
      for (const o of this.spiders) {
        if (hidden) break;
        if (o === s || o.held || o.air) continue;
        const dx = s.pos.x - o.pos.x, dy = s.pos.y - o.pos.y, d2 = dx * dx + dy * dy, min = 34 * (s.size + o.size) / 2;
        if (d2 > 0.01 && d2 < min * min) {
          const dd = Math.sqrt(d2), k = (min - dd) / min * 160;
          desired.x += dx / dd * k;
          desired.y += dy / dd * k;
        }
      }
      s.vel.x += (desired.x - s.vel.x) * Math.min(1, dt * 4);
      s.vel.y += (desired.y - s.vel.y) * Math.min(1, dt * 4);
      s.pos.x += s.vel.x * dt;
      s.pos.y += s.vel.y * dt;
      const v = Math.hypot(s.vel.x, s.vel.y);
      if (v > 8 || chasing && md > 1) {
        const want = v > 8 ? Math.atan2(s.vel.y, s.vel.x) : Math.atan2(this.mouse.y - s.pos.y, this.mouse.x - s.pos.x);
        let da = want - s.heading;
        da = Math.atan2(Math.sin(da), Math.cos(da));
        s.heading += da * Math.min(1, dt * 6);
      }
      this.stepLegs(s, dt, v);
      if (s.lurk && s.lurk.phase !== "go") this.lurkFeet(s);
      if (s.pos.x < -300 || s.pos.y < -300 || s.pos.x > this.w + 300 || s.pos.y > this.h + 300) {
        if (s.haul) this.dropHaul(s);
        if (hopping) this.abortHop();
        Object.assign(s, this.spawn());
      }
    }
    /** Gait: a group of legs may only lift while the other group is planted. */
    stepLegs(s, dt, v) {
      const stepDur = Math.max(0.09, 0.2 - v / 1600);
      const busy = [0, 0];
      for (const l of s.legs) if (l.t < 1) busy[l.group]++;
      for (const l of s.legs) {
        if (l.t < 1) {
          l.t = Math.min(1, l.t + dt / stepDur);
          const e = l.t < 0.5 ? 2 * l.t * l.t : 1 - Math.pow(-2 * l.t + 2, 2) / 2;
          l.foot = { x: l.from.x + (l.to.x - l.from.x) * e, y: l.from.y + (l.to.y - l.from.y) * e };
          if (l.t === 1) l.grip = l.toGrip;
          continue;
        }
        const rest = this.restPoint(s, l);
        const off = dist2(rest, l.foot);
        const maxLen = l.upper + l.lower;
        if (off > l.reach * 0.55 && busy[1 - l.group] === 0 || dist2(s.pos, l.foot) > maxLen * 0.98) {
          const lead = { x: rest.x + s.vel.x * stepDur * 1.6, y: rest.y + s.vel.y * stepDur * 1.6 };
          const g = this.grip(lead);
          l.from = { ...l.foot };
          l.to = g.p;
          l.toGrip = g.g;
          l.t = 0;
          busy[l.group]++;
        }
      }
    }
    walkTo(s, goal, k, now) {
      const d = dist2(s.pos, goal);
      const a = Math.atan2(goal.y - s.pos.y, goal.x - s.pos.x) + Math.sin(now / 900 + s.wobble) * 0.15 * Math.min(1, d / 120);
      const sp = s.speed * k * Math.min(1, d / 60 + 0.3);
      return { x: Math.cos(a) * sp, y: Math.sin(a) * sp };
    }
    // ---------- Reading ----------
    /** Pick a paragraph near the spider and start reading it from its first word on screen. */
    startReading(s) {
      const now = performance.now();
      const h1 = this.titleEl();
      if (h1 && !this.readDone.has(h1)) {
        const r = h1.getBoundingClientRect();
        this.readDone.set(h1, now);
        if (r.bottom > 20 && r.top < this.h - 20 && r.width > 0) {
          const range = document.createRange();
          range.selectNodeContents(h1);
          const title = (h1.textContent || "").trim().replace(/\s+/g, " ");
          s.reading = { block: h1, tokens: [{ range, kind: "title", label: title }], i: 0, end: 1, at: -1, dwell: 0, scan: null };
          s.target = null;
          s.chew = 0;
          return true;
        }
      }
      const cands = [];
      let seen = 0;
      for (const el of document.querySelectorAll(READ_BLOCKS)) {
        if (++seen > 1500) break;
        if (el.closest(IGNORE)) continue;
        const r = el.getBoundingClientRect();
        if (r.width < 30 || r.height < 8 || r.bottom < 30 || r.top > this.h - 30 || r.right < 0 || r.left > this.w) continue;
        if ((this.readDone.get(el) ?? -1e9) > now - 6e4) continue;
        if (this.spiders.some((o) => o !== s && o.reading?.block === el)) continue;
        if (!isLeafBlock(el)) continue;
        const penalty = el.closest("nav, header, footer, aside, [role=navigation], .navbox") ? 700 : 0;
        const cx = Math.max(r.left, Math.min(s.pos.x, r.right)), cy = Math.max(r.top, Math.min(s.pos.y, r.bottom));
        cands.push({ el, d: Math.hypot(cx - s.pos.x, cy - s.pos.y) + rnd2(0, 380) + penalty });
      }
      cands.sort((a, b) => a.d - b.d);
      for (const b of cands.slice(0, 6)) {
        this.readDone.set(b.el, now);
        const tokens = tokenize(b.el);
        const i = tokens.findIndex((t) => {
          const q = firstRect(t.range);
          return !!q && q.top > 20 && q.bottom < this.h - 20;
        });
        if (i < 0 || tokens.length - i < 2) continue;
        s.reading = { block: b.el, tokens, i, end: Math.min(tokens.length, i + Math.floor(rnd2(28, 70))), at: -1, dwell: 0, scan: null };
        s.target = null;
        s.chew = 0;
        if (!s.tether || dist2(s.tether.p, s.pos) > 900) s.tether = this.tetherAt(s.pos);
        return true;
      }
      return false;
    }
    endReading(s) {
      s.reading = null;
      s.rest = rnd2(0.4, 1.2);
    }
    /** Walk word to word; linger on the interesting ones; detour to scan pictures on the way. */
    readStep(s, dt, now) {
      const R = s.reading;
      if (!R.block.isConnected) {
        this.endReading(s);
        return ZERO();
      }
      if (R.scan) {
        const sc = R.scan;
        const r = sc.el.isConnected ? sc.el.getBoundingClientRect() : null;
        if (!r || r.bottom < 0 || r.top > this.h) {
          R.scan = null;
          return ZERO();
        }
        const goal2 = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        s.targetPoint = goal2;
        if (sc.at < 0) {
          if (dist2(s.pos, goal2) > 16) return this.walkTo(s, goal2, 1.15, now);
          sc.at = now;
          this.scannedImgs.add(sc.el);
          const cap = imageCaption(sc.el);
          const img = sc.el;
          const dims = img.naturalWidth ? `${img.naturalWidth}\xD7${img.naturalHeight}` : `${Math.round(r.width)}\xD7${Math.round(r.height)}`;
          this.addMark({ kind: "img", el: sc.el, tag: `IMG-${slug(cap || "IMAGE")}`, caption: `IMG \xB7 ${dims}${cap ? " \xB7 " + cap : ""}`, color: MARK_COLOR.img, life: 12e3 });
          this.stats.img++;
          if (this.websOn && !this.orbAlive(sc.el)) {
            R.scan = null;
            this.startOrb(s, sc.el);
            return ZERO();
          }
        }
        sc.dwell -= dt;
        if (sc.dwell <= 0) R.scan = null;
        return { x: (goal2.x - s.pos.x) * 3, y: (goal2.y - s.pos.y) * 3 };
      }
      if (R.i >= R.end || R.i >= R.tokens.length) {
        this.endReading(s);
        return ZERO();
      }
      const tok = R.tokens[R.i];
      const q = firstRect(tok.range);
      if (!q || q.bottom < 0) {
        R.i++;
        R.at = -1;
        return ZERO();
      }
      if (q.top > this.h - 10) {
        this.endReading(s);
        return ZERO();
      }
      const goal = { x: q.left + Math.min(q.width, 140) / 2, y: q.top + q.height / 2 };
      s.targetPoint = goal;
      const d = dist2(s.pos, goal);
      if (R.at < 0) {
        if (d > 12) return this.walkTo(s, goal, 1.05, now);
        R.at = now;
        R.dwell = DWELL[tok.kind] ?? 0.3;
        this.markToken(s, tok);
      }
      R.dwell -= dt;
      if (R.dwell <= 0) {
        R.i++;
        R.at = -1;
        if (Math.random() < 0.3) {
          const im = this.imgs.find((it) => !this.scannedImgs.has(it.el) && it.el.isConnected && dist2({ x: it.r.left + it.r.width / 2, y: it.r.top + it.r.height / 2 }, s.pos) < 340 && !this.spiders.some((o) => o.reading?.scan?.el === it.el));
          if (im) {
            R.scan = { el: im.el, at: -1, dwell: rnd2(2, 2.8) };
            return ZERO();
          }
        }
        const nx = R.tokens[R.i] && firstRect(R.tokens[R.i].range);
        if (nx && nx.top > q.bottom - 3 && q.left - nx.left > 120) {
          this.leap(s, { x: nx.left + Math.min(nx.width, 140) / 2, y: nx.top + nx.height / 2 }, 0.18);
          return ZERO();
        }
      }
      return { x: (goal.x - s.pos.x) * 4, y: (goal.y - s.pos.y) * 4 };
    }
    markToken(s, tok) {
      this.stats.words++;
      const k = tok.kind;
      let tag = "", life = 14e3;
      if (k === "word") life = 4500;
      else if (k === "lnk") {
        tag = `LNK-${slug(tok.label) || "LINK"}`;
        this.stats.lnk++;
        const a = (tok.range.startContainer.parentElement || null)?.closest("a[href]");
        if (a) this.readLinks.add(a);
      } else if (k === "title") {
        life = 3e4;
      } else {
        this.stats.ent++;
        if (k === "net") tag = `NET-${tok.label}`;
        else if (k === "val") tag = `VAL-${slug(tok.label)}`;
        else if (k === "date") tag = Math.random() < 0.5 ? `DATE-${slug(tok.label)}` : "";
        else if (k === "ent") tag = Math.random() < 0.45 ? `ENT-${slug(tok.label)}` : "";
      }
      const color = k === "word" ? s.colors.joint : k === "title" ? `hsl(${s.hue}, 95%, 60%)` : MARK_COLOR[k];
      this.addMark({ kind: k, range: tok.range, tag, color, life });
      if (k !== "word") this.link(s, { range: tok.range, el: null, fx: rnd2(0.2, 0.8), fy: rnd2(0.35, 0.95) });
    }
    // ---------- Lurking ----------
    /** Pick something nearby to hide behind, and the spot on its edge to peek from (corners are favorites). */
    startLurk(s) {
      const cands = [];
      let seen = 0;
      for (const el2 of document.querySelectorAll(COVER)) {
        if (++seen > 400) break;
        if (el2.closest(IGNORE) || this.spiders.some((o) => o !== s && o.lurk?.el === el2)) continue;
        const r = el2.getBoundingClientRect();
        if (r.width < 80 || r.height < 50 || r.bottom < 40 || r.top > this.h - 40 || r.right < 0 || r.left > this.w) continue;
        if (r.width * r.height > this.w * this.h * 0.4) continue;
        cands.push({ el: el2, d: dist2(s.pos, { x: r.left + r.width / 2, y: r.top + r.height / 2 }) + rnd2(0, 500) });
      }
      if (!cands.length) return false;
      const el = cands.sort((a, b) => a.d - b.d)[0].el;
      s.lurk = { el, side: Math.floor(Math.random() * 4), along: this.peekSpot(), phase: "go", t: 0, hold: 0, peek: 0, peeks: 0 };
      s.reading = null;
      s.target = null;
      s.chew = 0;
      return true;
    }
    peekSpot() {
      const r = Math.random();
      return r < 0.35 ? rnd2(0.04, 0.1) : r < 0.7 ? rnd2(0.9, 0.96) : rnd2(0.25, 0.75);
    }
    unlurk(s) {
      s.lurk = null;
      s.rest = rnd2(0.4, 1.2);
    }
    /** The edge point it peeks from, the outward normal, and the direction along the edge. */
    lurkEdge(L, r) {
      const len = L.side % 2 ? r.height : r.width, a = Math.max(Math.min(0.5, 30 / len), Math.min(1 - Math.min(0.5, 30 / len), L.along));
      switch (L.side) {
        case 0:
          return { p: { x: r.left + r.width * a, y: r.top }, n: { x: 0, y: -1 }, t: { x: 1, y: 0 } };
        case 1:
          return { p: { x: r.right, y: r.top + r.height * a }, n: { x: 1, y: 0 }, t: { x: 0, y: 1 } };
        case 2:
          return { p: { x: r.left + r.width * a, y: r.bottom }, n: { x: 0, y: 1 }, t: { x: 1, y: 0 } };
        default:
          return { p: { x: r.left, y: r.top + r.height * a }, n: { x: -1, y: 0 }, t: { x: 0, y: 1 } };
      }
    }
    /** Sneak behind it, wait, peek out (and back) a few times; jump out if the pointer comes close. */
    lurkStep(s, dt, now) {
      const L = s.lurk;
      const r = L.el.isConnected ? L.el.getBoundingClientRect() : null;
      if (!r || r.bottom < 0 || r.top > this.h || r.width < 20) {
        this.unlurk(s);
        return ZERO();
      }
      const k = s.size;
      const e = this.lurkEdge(L, r);
      const at = (pk) => {
        const off = (-24 + 31 * pk) * k;
        return { x: e.p.x + e.n.x * off, y: e.p.y + e.n.y * off };
      };
      const face = Math.atan2(e.n.y, e.n.x);
      const inside = (q) => q.x > r.left && q.x < r.right && q.y > r.top && q.y < r.bottom;
      if (L.phase === "go") {
        const goal2 = at(0);
        s.targetPoint = goal2;
        if (dist2(s.pos, goal2) > 10) return this.walkTo(s, goal2, 1.25, now);
        L.phase = "hide";
        L.hold = rnd2(1.2, 3.2);
        L.peek = 0;
      }
      const mouseLive = this.mouse.x > -1e3 && now - this.mouseAt < 5e3;
      const md = dist2(at(1), this.mouse);
      if (mouseLive && this.mouseMode !== "off" && md < 240 && md > 30 && !inside(this.mouse) && (L.phase === "peek" || L.peek > 0.2 || md < 150)) {
        const toward = Math.atan2(this.mouse.y - s.pos.y, this.mouse.x - s.pos.x);
        s.lurk = null;
        s.heading = toward;
        this.leap(s, { x: this.mouse.x - Math.cos(toward) * 26, y: this.mouse.y - Math.sin(toward) * 26 }, 0.16);
        s.cool = rnd2(1.5, 3);
        s.rest = rnd2(0.6, 1.4);
        return ZERO();
      }
      L.t += dt;
      L.hold -= dt;
      if (L.phase === "hide") {
        L.peek += (0 - L.peek) * Math.min(1, dt * 6);
        if (L.hold <= 0) {
          if (L.peeks >= 3 + Math.floor(Math.random() * 2)) {
            s.lurk = null;
            s.rest = 0;
            this.pickTarget(s);
            return ZERO();
          }
          if (L.peeks > 0 && Math.random() < 0.45) {
            L.side = Math.floor(Math.random() * 4);
            L.along = this.peekSpot();
          }
          L.phase = "peek";
          L.hold = rnd2(0.9, 2.4);
          L.peeks++;
        }
      } else if (L.phase === "peek") {
        L.peek += (1 - L.peek) * Math.min(1, dt * 3.2);
        if (L.hold <= 0) {
          L.phase = "back";
          L.hold = 0.35;
        }
      } else if (L.phase === "back") {
        L.peek += (0 - L.peek) * Math.min(1, dt * 9);
        if (L.hold <= 0) {
          L.phase = "hide";
          L.hold = rnd2(1.2, 3.5);
        }
      }
      const goal = at(L.peek);
      const sway = L.phase === "peek" ? Math.sin(now / 260 + s.wobble) * 2.5 * k : 0;
      goal.x += e.t.x * sway;
      goal.y += e.t.y * sway;
      s.pos.x += (goal.x - s.pos.x) * Math.min(1, dt * 8);
      s.pos.y += (goal.y - s.pos.y) * Math.min(1, dt * 8);
      let da = face - s.heading;
      da = Math.atan2(Math.sin(da), Math.cos(da));
      s.heading += da * Math.min(1, dt * 6);
      s.vel = ZERO();
      if (L.phase !== "hide") s.munch = L.peek > 0.6 && Math.sin(now / 400 + s.wobble) > 0.6;
      return ZERO();
    }
    /** While hidden, the front legs hook around the edge it's peeking over; the rest stay tucked behind. */
    lurkFeet(s) {
      const L = s.lurk;
      const r = L.el.getBoundingClientRect();
      const e = this.lurkEdge(L, r), k = s.size;
      s.legs.forEach((l, i) => {
        l.grip = { el: null, dx: 0, dy: 0 };
        l.t = 1;
        const pair = i >> 1;
        if (pair < 2 && L.peek > 0.15) {
          const spread = (pair === 0 ? 9 : 22) * k * (i % 2 ? -1 : 1);
          const out = (pair === 0 ? 9 : 4) * k * L.peek;
          const want = { x: e.p.x + e.t.x * spread + e.n.x * out, y: e.p.y + e.t.y * spread + e.n.y * out };
          l.foot.x += (want.x - l.foot.x) * 0.35;
          l.foot.y += (want.y - l.foot.y) * 0.35;
        } else {
          const rest = this.restPoint(s, l), tuck = { x: s.pos.x + (rest.x - s.pos.x) * 0.45, y: s.pos.y + (rest.y - s.pos.y) * 0.45 };
          l.foot.x += (tuck.x - l.foot.x) * 0.2;
          l.foot.y += (tuck.y - l.foot.y) * 0.2;
        }
      });
    }
    // ---------- Webs ----------
    anchorPoint(a) {
      let q = null;
      if (a.range) q = firstRect(a.range);
      else if (a.el && a.el.isConnected) {
        const r = a.el.getBoundingClientRect();
        if (r.width > 0 || r.height > 0) q = r;
      }
      return q ? { x: q.left + q.width * a.fx, y: q.top + q.height * a.fy } : null;
    }
    /** Fix the silk it's been paying out to this spot; now and then add a long strand back to an older stop. */
    link(s, an) {
      if (!this.websOn) return;
      const p = this.anchorPoint(an);
      if (!p) return;
      const now = performance.now();
      const add = (a) => {
        const q = this.anchorPoint(a);
        if (!q) return;
        const d = dist2(p, q);
        if (d < 8 || d > 900) return;
        this.strands.push({ a, b: an, born: now, life: rnd2(22e3, 32e3), sag: Math.min(34, d * 0.07) * rnd2(0.5, 1), pa: null, pb: null, at: 0, sx: 0, sy: 0 });
      };
      if (s.lastAnchor) add(s.lastAnchor);
      if (s.recent.length > 2 && Math.random() < 0.35) add(s.recent[Math.floor(Math.random() * (s.recent.length - 1))]);
      s.recent.push(an);
      if (s.recent.length > 10) s.recent.shift();
      s.lastAnchor = an;
      if (this.strands.length > 240) this.strands.splice(0, this.strands.length - 240);
    }
    orbAlive(el) {
      const now = performance.now();
      return this.orbs.some((o) => o.el === el && now - o.born < o.life);
    }
    /** Take over a picture: a frame, spokes and a capture spiral across the whole image, bridged back to the text. */
    startOrb(s, el) {
      const n = 13 + Math.floor(Math.random() * 6);
      const spokes = [];
      for (let i = 0; i < n; i++) spokes.push(i / n * TAU2 + rnd2(-0.1, 0.1) - Math.PI / 2);
      const orb = {
        el,
        born: performance.now(),
        build: rnd2(4, 5),
        life: rnd2(3e4, 4e4),
        hub: { x: rnd2(0.42, 0.58), y: rnd2(0.38, 0.55) },
        spokes,
        reach: spokes.map(() => rnd2(0.86, 0.97)),
        rings: 7,
        bridges: s.recent.slice(-3),
        color: s.colors.joint,
        tag: `IMG-${slug(imageCaption(el) || "IMAGE")} CAPTURED`
      };
      this.orbs.push(orb);
      if (this.orbs.length > 12) this.orbs.shift();
      s.spinOrb = orb;
      s.target = el;
      s.chew = 0;
      this.link(s, { range: null, el, fx: orb.hub.x, fy: orb.hub.y });
    }
    /** An orb's hub and spoke ends in screen space, for the picture's current box. */
    orbGeom(o, r) {
      const hub = { x: r.left + r.width * o.hub.x, y: r.top + r.height * o.hub.y }, m = 3;
      const ends = o.spokes.map((a, i) => {
        const dx = Math.cos(a), dy = Math.sin(a);
        const tx = dx > 1e-6 ? (r.right - m - hub.x) / dx : dx < -1e-6 ? (r.left + m - hub.x) / dx : Infinity;
        const ty = dy > 1e-6 ? (r.bottom - m - hub.y) / dy : dy < -1e-6 ? (r.top + m - hub.y) / dy : Infinity;
        const t = Math.max(0, Math.min(tx, ty)) * o.reach[i];
        return { x: hub.x + dx * t, y: hub.y + dy * t };
      });
      return { hub, ends };
    }
    /** The capture spiral, k segments in: polygonal, from the outside in, like a real orb weaver lays it. */
    spiralPoint(g, k, rings) {
      const n = g.ends.length, e = g.ends[Math.floor(k) % n];
      const f = 0.92 - 0.74 * Math.min(1, k / (rings * n));
      return { x: g.hub.x + (e.x - g.hub.x) * f, y: g.hub.y + (e.y - g.hub.y) * f };
    }
    /** Where the spinner is at build progress p: round the frame, out and back along each spoke, then the spiral. */
    orbPoint(o, g, p) {
      const n = g.ends.length;
      if (p < 0.2) return g.ends[Math.min(n - 1, Math.floor(p / 0.2 * n))];
      if (p < 0.5) {
        const f = (p - 0.2) / 0.3 * n, i = Math.min(n - 1, Math.floor(f)), t = f - i, k = t < 0.5 ? t * 2 : 2 - t * 2, e = g.ends[i];
        return { x: g.hub.x + (e.x - g.hub.x) * k, y: g.hub.y + (e.y - g.hub.y) * k };
      }
      return this.spiralPoint(g, o.rings * n * ((p - 0.5) / 0.5), o.rings);
    }
    orbStep(s, now) {
      const o = s.spinOrb;
      const r = o.el.isConnected ? o.el.getBoundingClientRect() : null;
      if (!r || r.bottom < 0 || r.top > this.h) {
        s.spinOrb = null;
        s.chew = 0;
        return ZERO();
      }
      const p = clamp01((now - o.born) / (o.build * 1e3));
      const goal = this.orbPoint(o, this.orbGeom(o, r), p);
      s.targetPoint = goal;
      if (p >= 1) {
        s.spinOrb = null;
        s.target = null;
        s.chew = 0;
        s.rest = rnd2(0.5, 1.2);
        return ZERO();
      }
      return this.walkTo(s, goal, 2.4, now);
    }
    addMark(m) {
      this.marks.push({
        kind: m.kind,
        range: m.range ?? null,
        el: m.el ?? null,
        tag: m.tag ?? "",
        caption: m.caption ?? "",
        color: m.color,
        solid: !!m.solid,
        born: performance.now(),
        life: m.life,
        boxes: [],
        at: 0,
        sx: 0,
        sy: 0
      });
      if (this.marks.length > 260) this.marks.splice(0, this.marks.length - 260);
    }
    tetherAt(p) {
      const g = this.grip(p);
      return { el: g.g.el, dx: g.g.dx, dy: g.g.dy, p: g.p };
    }
    tetherFollow(s) {
      const t = s.tether;
      if (!t || !t.el) return;
      if (!t.el.isConnected) {
        t.el = null;
        return;
      }
      const r = t.el.getBoundingClientRect();
      t.p = { x: r.left + t.dx, y: r.top + t.dy };
    }
    titleEl() {
      return document.querySelector(`${this.crawlRoot} h1`) || document.querySelector("h1");
    }
    pageTitle() {
      const t = (this.titleEl()?.textContent || document.title || location.pathname).trim().replace(/\s+/g, " ");
      return t.slice(0, 90);
    }
    // ---------- Crawling (following links) ----------
    /** A same-origin page link worth following, as an absolute URL without its #hash; else null. */
    hopUrl(a, here) {
      let u;
      try {
        u = new URL(a.getAttribute("href") || "", location.href);
      } catch {
        return null;
      }
      if (!/^https?:$/.test(u.protocol) || u.origin !== location.origin || a.hasAttribute("download")) return null;
      u.hash = "";
      if (u.href === here || this.visited.has(u.href)) return null;
      if (/\.(pdf|png|jpe?g|gif|svg|webp|avif|ico|zip|gz|7z|tar|mp4|webm|mov|mp3|wav|ogg|exe|msi|dmg|apk|json|xml|txt|csv)$/i.test(u.pathname)) return null;
      let last = "";
      try {
        last = decodeURIComponent(u.pathname.split("/").pop() || "");
      } catch {
      }
      if (/^[A-Za-z_ ]+:/.test(last)) return null;
      if (/(^|&|\?)(action|oldid|diff|printable|returnto|logout|signout)=/i.test(u.search) || /log-?out|sign-?out/i.test(u.pathname)) return null;
      return u.href;
    }
    hopLinks() {
      const out = [];
      const here = location.href.split("#")[0];
      for (const a of document.querySelectorAll("a[href]")) {
        if (out.length > 300) break;
        if (a.closest(IGNORE)) continue;
        const r = a.getBoundingClientRect();
        if (r.width < 4 || r.height < 4 || r.bottom < 40 || r.top > this.h - 30 || r.right < 0 || r.left > this.w) continue;
        const url = this.hopUrl(a, here);
        if (!url) continue;
        const prose = !!a.closest("p, li, dd, td") && !a.closest("nav, header, footer, aside, [role=navigation], .navbox, .reflist, .references, .mw-references-wrap");
        const title = (a.textContent || "").trim().replace(/\s+/g, " ") || url;
        out.push({ el: a, url, title, c: { x: r.left + Math.min(r.width / 2, 40), y: r.top + r.height / 2 }, prose });
      }
      return out;
    }
    /** Time for a hop? Pick a creature and a link it can get to. */
    maybeHop(now) {
      if (!this.crawlOn || this.hop || now < this.hopAt || document.hidden || this.hold) return;
      if (this.creature === "octopus" && this.items.some((it) => !it.taken) && now < this.hopAt + this.hopEvery * 2e3) return;
      const links = this.hopLinks();
      const movers = this.creature === "octopus" ? this.octopi.filter((o) => !o.held) : this.spiders.filter((s) => !s.held && !s.air && !s.silk && !s.duel && !s.haul);
      let best = null, bestS = Infinity;
      for (const m of movers) for (const l of links) {
        const sc = dist2(m.pos, l.c) + (l.prose ? 0 : 600) - (this.readLinks.has(l.el) ? 350 : 0) + rnd2(0, 220);
        if (sc < bestS) {
          bestS = sc;
          best = { m, l };
        }
      }
      if (!best) {
        this.hopAt = now + 4e3;
        return;
      }
      if (this.onHop && this.onHop(best.l.url, best.l.el) === false) {
        this.visited.add(best.l.url);
        this.hopAt = now + 2500;
        return;
      }
      this.hop = { who: best.m, el: best.l.el, url: best.l.url, title: best.l.title, phase: "go", t: 0, doc: void 0 };
      if (best.m instanceof Octopus) best.m.releaseAll();
      else {
        const s = best.m;
        s.reading = null;
        s.target = null;
        s.chew = 0;
        s.rest = 0;
      }
    }
    hopStep(s, now) {
      const H = this.hop;
      if (!H.el.isConnected) {
        this.abortHop();
        return ZERO();
      }
      const r = H.el.getBoundingClientRect();
      const goal = { x: r.left + Math.min(r.width / 2, 40), y: r.top + r.height / 2 };
      s.targetPoint = goal;
      if (H.phase === "go") {
        if (r.bottom < 0 || r.top > this.h || r.width === 0) {
          this.abortHop();
          return ZERO();
        }
        if (dist2(s.pos, goal) > 14) return this.walkTo(s, goal, 1.25, now);
        this.hopGrip();
      }
      s.munch = true;
      return { x: (goal.x - s.pos.x) * 4, y: (goal.y - s.pos.y) * 4 };
    }
    /** The creature has hold of the link: mark it and start fetching the page behind it. */
    hopGrip() {
      const H = this.hop;
      if (!H || H.phase !== "go") return;
      H.phase = "spin";
      H.t = 0;
      this.addMark({ kind: "hop", el: H.el, tag: `HOP \u2192 ${slug(H.title, 22) || "PAGE"}`, color: MARK_COLOR.hop, life: 4e3 });
      fetch(H.url, { credentials: "same-origin", headers: { Accept: "text/html" } }).then((r) => r.ok && /html/i.test(r.headers.get("content-type") || "") ? r.text() : null).then((t) => {
        H.doc = t ? new DOMParser().parseFromString(t, "text/html") : null;
      }).catch(() => {
        H.doc = null;
      });
    }
    tickHop(dt, now) {
      const H = this.hop;
      if (!H || H.phase !== "spin") return;
      H.t += dt;
      if (H.t > 0.9 && H.doc !== void 0) {
        if (H.doc && H.doc.querySelector(this.crawlRoot)) this.swap(H);
        else {
          this.addMark({ kind: "hop", el: H.el, tag: "DEAD LINK", color: "#ff3b5c", life: 3e3 });
          this.visited.add(H.url);
          this.abortHop(now + 3e3);
        }
      } else if (H.t > 10) {
        this.visited.add(H.url);
        this.abortHop(now + 3e3);
      }
    }
    abortHop(nextAt = performance.now() + 5e3) {
      const H = this.hop;
      if (!H) return;
      this.hop = null;
      this.hopAt = nextAt;
      if (H.who instanceof Octopus) H.who.releaseAll();
      else H.who.rest = rnd2(0.3, 0.8);
    }
    /** Fade the page out, put the fetched one in its place, and fade it back in. */
    swap(H) {
      const doc = H.doc;
      const root = document.querySelector(this.crawlRoot);
      const next = doc.querySelector(this.crawlRoot);
      if (!root || !next) {
        this.abortHop();
        return;
      }
      H.phase = "swap";
      this.fade = { t0: performance.now(), hue: H.who.hue, title: (doc.querySelector("h1")?.textContent || doc.title || H.title).trim().replace(/\s+/g, " ") };
      const prevTransition = root.style.transition, prevOpacity = root.style.opacity;
      root.style.transition = "opacity .26s ease";
      root.style.opacity = "0";
      window.setTimeout(() => {
        if (this.dead) {
          root.style.opacity = prevOpacity;
          root.style.transition = prevTransition;
          return;
        }
        try {
          for (const x of next.querySelectorAll("script")) x.remove();
          for (const x of next.querySelectorAll("[href], [src]")) {
            for (const at of ["href", "src"]) {
              const v = x.getAttribute(at);
              if (v && !/^(#|[a-z][a-z0-9+.-]*:)/i.test(v)) {
                try {
                  x.setAttribute(at, new URL(v, H.url).href);
                } catch {
                }
              }
            }
          }
          if (this.history) {
            history.pushState({ skitter: this.trail.length + 1 }, "", H.url);
            if (!this.pushed) {
              this.pushed = true;
              window.addEventListener("popstate", onPop);
            }
          }
          const have = new Set([...document.querySelectorAll('link[rel~="stylesheet"]')].map((l) => l.href));
          for (const l of doc.querySelectorAll('link[rel~="stylesheet"]')) {
            const href = new URL(l.getAttribute("href") || "", H.url).href;
            if (have.has(href)) continue;
            const n = document.createElement("link");
            n.rel = "stylesheet";
            n.href = href;
            document.head.appendChild(n);
          }
          if (doc.title) document.title = doc.title;
          if (next.className) root.className = next.className;
          root.replaceChildren(...[...next.childNodes].map((n) => document.importNode(n, true)));
          window.scrollTo(0, 0);
        } catch (e) {
          this.report(e);
        }
        requestAnimationFrame(() => {
          root.style.opacity = prevOpacity;
          window.setTimeout(() => {
            root.style.transition = prevTransition;
          }, 320);
          this.landed(H);
        });
      }, 280);
    }
    /** After a hop: forget the old page, land on the new title, and tell the host. */
    landed(H) {
      const now = performance.now();
      if (!this.trail.length) this.trail.push({ url: [...this.visited][0] ?? "", title: "start" });
      this.trail.push({ url: H.url, title: this.pageTitle() });
      this.visited.add(H.url);
      this.marks = [];
      this.items = [];
      this.strands = [];
      this.orbs = [];
      this.harvested = /* @__PURE__ */ new WeakSet();
      this.readDone = /* @__PURE__ */ new WeakMap();
      this.scannedImgs = /* @__PURE__ */ new WeakSet();
      this.readLinks = /* @__PURE__ */ new WeakSet();
      this.rectsAt = 0;
      this.refreshRects(now);
      this.hop = null;
      this.hopAt = now + this.hopEvery * 1e3 * rnd2(0.85, 1.2);
      const who = H.who;
      who.hue = (who.hue + rnd2(45, 90)) % 360;
      for (const s of this.spiders) {
        if (s.haul) this.dropHaul(s);
        s.reading = null;
        s.target = null;
        s.chew = 0;
        s.torn = null;
        s.silk = null;
        s.duel = null;
        s.tether = null;
        s.rest = rnd2(0.2, 1);
        s.lastAnchor = null;
        s.recent = [];
        s.spinOrb = null;
        s.lurk = null;
        if (!s.air && !s.held) this.replant(s);
      }
      if (this.look !== "neon" && !(who instanceof Octopus)) who.colors = hueColors(who.hue);
      for (const o of this.octopi) o.releaseAll();
      const h1 = this.titleEl();
      if (h1) {
        const r = h1.getBoundingClientRect();
        const range = document.createRange();
        range.selectNodeContents(h1);
        this.addMark({ kind: "title", range, color: `hsl(${who.hue}, 95%, 60%)`, life: 3e4 });
        this.readDone.set(h1, now);
        if (!(who instanceof Octopus) && r.width > 0) {
          const p = { x: r.left + Math.min(r.width, 260) / 2, y: r.top + r.height / 2 };
          this.leap(who, p, 0.35);
          who.tether = this.tetherAt(p);
          who.rest = 0.6;
        }
      }
      window.dispatchEvent(new CustomEvent("skitter:hop", { detail: { url: H.url, title: this.pageTitle(), hop: this.trail.length - 1 } }));
    }
    // ---------- Scraping (octopus) ----------
    refreshItems(now) {
      if (now - this.itemsAt < 700) return;
      this.itemsAt = now;
      this.items = this.items.filter((it) => !it.taken && it.block.isConnected);
      let fresh = 0, seen = 0;
      const own = `${this.food}, ${this.prey}`;
      for (let el of document.querySelectorAll(`${READ_BLOCKS}, ${own}, ${LEAVES}`)) {
        if (++seen > 4e3 || fresh > 60 || this.items.length > 400) break;
        if (this.harvested.has(el) || el === this.canvas || el.closest(IGNORE)) continue;
        const block = el.matches(READ_BLOCKS);
        if (!block && !el.matches(own)) {
          if (el.childElementCount > 0) continue;
          for (let p = el.parentElement; p && p !== document.body && p.childElementCount <= 4 && !p.matches(READ_BLOCKS); p = p.parentElement) {
            if ((p.textContent || "").trim().length > 32) break;
            el = p;
          }
          if (this.harvested.has(el)) continue;
        }
        const r = el.getBoundingClientRect();
        if (r.width < 8 || r.height < 6 || r.bottom < -60 || r.top > this.h + 60 || r.right < 0 || r.left > this.w) continue;
        if (block && !isLeafBlock(el)) continue;
        let inside = false;
        for (let p = el.parentElement; p && !inside; p = p.parentElement) inside = this.harvested.has(p);
        if (inside) continue;
        this.harvested.add(el);
        fresh++;
        const found = harvest(el);
        if (!found.length && !block && !el.closest("button, [role=button], label, input, select, textarea, nav, [role=group], [role=toolbar]")) {
          const label = (el.innerText || el.textContent || "").trim().replace(/\s+/g, " ").replace(/^\u26a0\s*/, "");
          if (label.length >= 2 && label.length <= 48 && /\p{L}|\d/u.test(label)) {
            const range = document.createRange();
            range.selectNodeContents(el);
            found.push({ range, kind: /\d/.test(label) ? "num" : "ent", label });
          }
        }
        for (const t of found) this.items.push({ ...t, block: el, claimed: false, taken: false });
      }
    }
    record(it) {
      const page = location.href.split("#")[0];
      const key = `${page}\0${it.kind}\0${it.label}`;
      this.tally.set(it.kind, (this.tally.get(it.kind) ?? 0) + 1);
      if (this.scrapedKeys.has(key) || this.scraped.length > 5e3) return;
      this.scrapedKeys.add(key);
      const item = { kind: KIND_CODE[it.kind].toLowerCase(), value: it.label, page, title: this.pageTitle(), at: (/* @__PURE__ */ new Date()).toISOString() };
      this.scraped.push(item);
      window.dispatchEvent(new CustomEvent("skitter:scrape", { detail: item }));
    }
    // ---------- Carrying food home ----------
    /** Where a spider eats what it carries: a cobweb in the bottom-right corner, each spider at its own spot. */
    nestPoint(s) {
      const n = { x: this.w - 74, y: this.h - 74 };
      if (!s) return n;
      const a = s.wobble * 2.4;
      return { x: n.x + Math.cos(a) * 26, y: n.y + Math.sin(a) * 26 };
    }
    /** Rip the word off. With eating on, its letters vanish from the page until the feast is over. */
    snatch(s, el, text) {
      let meal = null;
      if (this.eat && this.startMeal(s, el)) {
        meal = this.meals.get(el) || null;
        if (meal) {
          for (let i = meal.eaten; i < meal.chars.length; i++) {
            const sp = meal.chars[meal.order[i]];
            sp.style.opacity = "0";
            if (i % 3 === 0) this.crumble(sp, meal.color, s);
          }
          meal.eaten = meal.chars.length;
          meal.budget = meal.chars.length;
          meal.restored = 0;
          meal.spider = null;
          meal.regrowAt = Infinity;
        }
      }
      s.torn = null;
      s.haul = { text, color: s.colors.tag, angle: rnd2(-0.5, 0.5), pos: { ...s.pos }, phase: "haul", next: 0, meal };
    }
    /** Walk the food home, then eat it letter by letter. Returns the walking velocity. */
    haulStep(s, now) {
      const h = s.haul, nest = this.nestPoint(s), d = dist2(s.pos, nest);
      if (h.phase === "haul") {
        if (d < 22) {
          h.phase = "feed";
          h.next = now + 250;
          return ZERO();
        }
        const a = Math.atan2(nest.y - s.pos.y, nest.x - s.pos.x) + Math.sin(now / 600 + s.wobble) * 0.25;
        const sp = s.speed * 0.8 * Math.min(1, d / 80 + 0.3);
        return { x: Math.cos(a) * sp, y: Math.sin(a) * sp };
      }
      s.munch = true;
      if (now >= h.next) {
        h.text = h.text.replace(/\s+$/, "").slice(0, -1);
        for (let k = 0; k < 3; k++) {
          const a = rnd2(0, TAU2), v = rnd2(30, 90), life = rnd2(0.4, 0.8);
          this.crumbs.push({ x: h.pos.x + rnd2(-6, 6), y: h.pos.y, vx: Math.cos(a) * v, vy: Math.sin(a) * v - 50, life, max: life, size: rnd2(1.4, 2.6), color: h.color });
        }
        h.next = now + 110;
        if (!h.text) {
          if (h.meal) h.meal.regrowAt = now + 2500;
          s.haul = null;
          s.target = null;
          s.rest = rnd2(0.6, 1.6);
        }
      }
      return ZERO();
    }
    /** Drop the food where the spider is: it falls away, and snatched letters grow back soon. */
    dropHaul(s) {
      const h = s.haul;
      if (!h) return;
      s.haul = null;
      if (h.meal) h.meal.regrowAt = performance.now() + 1e3;
      if (h.text) {
        this.ctx.font = `600 13px ${MONO}`;
        s.torn = { text: h.text, w: this.ctx.measureText(h.text).width + 14, color: h.color, angle: h.angle, pos: h.pos, life: 1.2 };
      }
    }
    /** Torn and carried labels trail behind on their thread; food being eaten sits at the mouth. */
    dragLabels(s, dt, now) {
      const hx = Math.cos(s.heading), hy = Math.sin(s.heading);
      if (s.torn) {
        const t = s.torn;
        t.life -= dt;
        const back = { x: s.pos.x - hx * 34 * s.size, y: s.pos.y - hy * 34 * s.size };
        t.pos.x += (back.x - t.pos.x) * Math.min(1, dt * 3);
        t.pos.y += (back.y - t.pos.y) * Math.min(1, dt * 3);
        t.angle += (s.heading * 0.3 - t.angle) * dt * 0.8;
        if (t.life <= 0) s.torn = null;
      }
      if (s.haul) {
        const h = s.haul, feeding = h.phase === "feed";
        const spot = feeding ? { x: s.pos.x + hx * 22 * s.size, y: s.pos.y + hy * 22 * s.size + Math.sin(now / 45) * 1.5 } : { x: s.pos.x - hx * 38 * s.size, y: s.pos.y - hy * 38 * s.size };
        h.pos.x += (spot.x - h.pos.x) * Math.min(1, dt * (feeding ? 8 : 3));
        h.pos.y += (spot.y - h.pos.y) * Math.min(1, dt * (feeding ? 8 : 3));
        h.angle += ((feeding ? 0 : Math.sin(now / 300 + s.wobble) * 0.35) - h.angle) * Math.min(1, dt * 2);
      }
    }
    // ---------- Acrobatics ----------
    /**
     * Shoot a thread to the underside of a word. "swing": a word between here and the target (or ahead),
     * then a pendulum swing and a leap. "hang": a word just above, then dangle below it and climb back up.
     */
    startSilk(s, mode) {
      let ideal;
      if (mode === "swing") {
        const goal = s.target && s.target.isConnected ? s.targetPoint : { x: s.pos.x + Math.cos(s.heading) * 220, y: s.pos.y + Math.sin(s.heading) * 220 };
        ideal = { x: (s.pos.x + goal.x) / 2, y: Math.min(s.pos.y, goal.y) - rnd2(50, 120) };
      } else {
        ideal = { x: s.pos.x + rnd2(-40, 40), y: s.pos.y - rnd2(60, 110) };
      }
      let best = null, bestD = 140;
      for (const it of this.rects) {
        if (it.r.bottom > s.pos.y + 20) continue;
        const p = { x: Math.max(it.r.left + 3, Math.min(it.r.right - 3, ideal.x)), y: it.r.bottom - 1 };
        const len2 = dist2(p, s.pos);
        if (len2 < 45 || len2 > 260) continue;
        const d = dist2(p, ideal);
        if (d < bestD) {
          bestD = d;
          best = { el: it.el, r: it.r, p };
        }
      }
      if (!best) return false;
      const len = dist2(best.p, s.pos);
      const ang = Math.atan2(s.pos.x - best.p.x, s.pos.y - best.p.y);
      s.silk = {
        el: best.el,
        dx: best.p.x - best.r.left,
        dy: best.p.y - best.r.top,
        anchor: best.p,
        len,
        ang,
        vel: mode === "swing" ? -Math.sign(ang || 1) * rnd2(0.6, 1.4) : 0,
        t: 0,
        dur: mode === "swing" ? rnd2(0.9, 1.8) : rnd2(2.2, 4),
        mode,
        hangLen: rnd2(34, 70)
      };
      s.vel = ZERO();
      s.chew = 0;
      s.lift = 0;
      if (mode === "hang") s.target = null;
      return true;
    }
    swing(s, dt, now) {
      const k = s.silk;
      k.t += dt;
      if (k.el) {
        if (!k.el.isConnected) {
          this.dropSilk(s);
          return;
        }
        const r = k.el.getBoundingClientRect();
        k.anchor = { x: r.left + k.dx, y: r.top + k.dy };
      }
      const G = 1500;
      k.vel += (-(G / Math.max(30, k.len)) * Math.sin(k.ang) - k.vel * 0.35) * dt;
      if (k.mode === "hang") {
        const climbing = k.t > k.dur;
        const want = climbing ? 0 : k.hangLen + Math.sin(now / 260) * 4;
        k.len += (want - k.len) * Math.min(1, dt * (climbing ? 3.2 : 2));
        k.vel *= 1 - Math.min(1, dt * 2.5);
        if (climbing && k.len < 8) {
          s.silk = null;
          s.pos = { ...k.anchor };
          this.replant(s);
          s.rest = rnd2(0.2, 0.8);
          s.cool = rnd2(2, 4);
          return;
        }
      } else {
        k.vel += Math.sign(k.vel || 1) * 1.3 * dt;
        k.len += (Math.max(60, k.len * 0.92) - k.len) * dt;
      }
      k.ang += k.vel * dt;
      s.pos = { x: k.anchor.x + Math.sin(k.ang) * k.len, y: k.anchor.y + Math.cos(k.ang) * k.len };
      s.heading = Math.atan2(k.anchor.y - s.pos.y, k.anchor.x - s.pos.x);
      s.legs.forEach((l, i) => {
        const rest = this.restPoint(s, l);
        const kick = Math.sin(now / 120 + i * 1.9) * 4;
        l.foot = { x: s.pos.x + (rest.x - s.pos.x) * 0.58 + kick * 0.5, y: s.pos.y + (rest.y - s.pos.y) * 0.58 + kick };
        l.grip = { el: null, dx: 0, dy: 0 };
        l.t = 1;
      });
      if (k.mode === "swing" && k.t > k.dur && Math.abs(k.vel) > 0.6) this.letGo(s);
      else if (k.mode === "swing" && k.t > k.dur + 1.5) this.letGo(s);
    }
    /** Release the thread at full swing and fly to a word in that direction (the target if it's in reach). */
    letGo(s) {
      const k = s.silk;
      s.silk = null;
      const tv = { x: Math.cos(k.ang) * k.vel * k.len, y: -Math.sin(k.ang) * k.vel * k.len };
      const speed = Math.hypot(tv.x, tv.y) || 1;
      const dir = Math.atan2(tv.y, tv.x);
      let land = null;
      if (s.target && s.target.isConnected) {
        const d = dist2(s.pos, s.targetPoint);
        if (d > 30 && d < 380) land = { ...s.targetPoint };
      }
      if (!land) {
        let bestScore = Infinity;
        const guess = { x: s.pos.x + tv.x * 0.35, y: s.pos.y + tv.y * 0.35 };
        for (const it of this.rects) {
          const c = { x: it.r.left + it.r.width / 2, y: it.r.top + it.r.height / 2 };
          const d = dist2(c, s.pos);
          if (d < 70 || d > 340) continue;
          let da = Math.atan2(c.y - s.pos.y, c.x - s.pos.x) - dir;
          da = Math.atan2(Math.sin(da), Math.cos(da));
          if (Math.abs(da) > 1.2) continue;
          const score = dist2(c, guess) + Math.abs(da) * 60;
          if (score < bestScore) {
            bestScore = score;
            land = c;
          }
        }
      }
      this.leap(s, land ?? { x: s.pos.x + tv.x * 0.3, y: s.pos.y + tv.y * 0.3 + 40 }, Math.max(0.18, 0.4 - speed / 3e3));
    }
    /** Cut the thread and fall to whatever is below. */
    dropSilk(s) {
      s.silk = null;
      this.leap(s, { x: s.pos.x, y: s.pos.y + 30 }, 0.15);
    }
    // ---------- Fighting ----------
    startDuel(challenger, owner) {
      const el = owner.target;
      const dur = rnd2(1.4, 2.4);
      const challengerWins = Math.random() < challenger.size / (challenger.size + owner.size * 1.15);
      const prey = !!this.rects.find((r) => r.el === el)?.prey;
      challenger.duel = { foe: owner, el, t: 0, dur, win: challengerWins, lead: true, prey };
      owner.duel = { foe: challenger, el, t: 0, dur, win: !challengerWins, lead: false, prey };
      challenger.chew = 0;
      challenger.vel = ZERO();
      owner.vel = ZERO();
    }
    brawl(s, dt) {
      const d = s.duel, foe = d.foe;
      if (!this.spiders.includes(foe) || foe.held || foe.air || foe.duel?.foe !== s) {
        s.duel = null;
        s.lift = 0;
        this.replant(s);
        return;
      }
      d.t += dt;
      const ang = Math.atan2(foe.pos.y - s.pos.y, foe.pos.x - s.pos.x);
      let da = ang - s.heading;
      da = Math.atan2(Math.sin(da), Math.cos(da));
      s.heading += da * Math.min(1, dt * 10);
      const gap = 30 + Math.sin(d.t * 15 + (d.lead ? 0 : Math.PI)) * 7;
      const cur = dist2(s.pos, foe.pos);
      const push = (cur - gap) / 2 * Math.min(1, dt * 12);
      s.pos.x += Math.cos(ang) * push;
      s.pos.y += Math.sin(ang) * push;
      s.lift = 3 + Math.abs(Math.sin(d.t * 15)) * 3;
      this.stepLegs(s, dt, Math.abs(push) / Math.max(dt, 1e-3));
      s.munch = true;
      if (d.lead && Math.random() < dt * 22) {
        const mx = (s.pos.x + foe.pos.x) / 2, my = (s.pos.y + foe.pos.y) / 2 - 8;
        const colors = ["#fff6a8", "#ffd166", "#ff7a45", "#ffffff"];
        for (let k = 0; k < 3; k++) {
          const a = rnd2(0, TAU2), sp = rnd2(60, 180), life = rnd2(0.25, 0.5);
          this.crumbs.push({ x: mx, y: my, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 60, life, max: life, size: rnd2(1.2, 2.4), color: colors[k % 4] });
        }
      }
      if (d.lead && d.t >= d.dur) this.settle(s, foe);
    }
    /** The lead spider ends the fight for both. */
    settle(a, b) {
      const d = a.duel;
      const [winner, loser] = d.win ? [a, b] : [b, a];
      a.duel = null;
      b.duel = null;
      a.lift = 0;
      b.lift = 0;
      loser.target = null;
      loser.chew = 0;
      loser.spinOrb = null;
      loser.rest = rnd2(1.2, 2.2);
      loser.cool = rnd2(1, 2);
      const ang = Math.atan2(loser.pos.y - winner.pos.y, loser.pos.x - winner.pos.x) + rnd2(-0.4, 0.4);
      const k = rnd2(130, 200);
      this.leap(loser, { x: loser.pos.x + Math.cos(ang) * k, y: loser.pos.y + Math.sin(ang) * k }, 0.22);
      this.replant(winner);
      winner.target = d.el;
      const meal = this.bite(winner, d.el, d.prey);
      winner.chew = meal ?? (d.prey ? rnd2(4, 6) : rnd2(1.5, 2.8));
    }
    /** Picked up: legs hang below the body and kick. */
    dangle(s, now) {
      s.lift = 16;
      s.legs.forEach((l, i) => {
        const rest = this.restPoint(s, l);
        const kick = Math.sin(now / 85 + i * 1.7) * 6;
        l.foot = { x: s.pos.x + (rest.x - s.pos.x) * 0.55 + kick * 0.4, y: s.pos.y + (rest.y - s.pos.y) * 0.55 + 18 + kick };
        l.grip = { el: null, dx: 0, dy: 0 };
        l.t = 1;
      });
    }
    /** Mid-air: follow the arc with legs tucked, then land and grip whatever is underneath. */
    fly(s, dt) {
      const a = s.air;
      a.t = Math.min(1, a.t + dt / a.dur);
      s.pos = { x: a.from.x + (a.to.x - a.from.x) * a.t, y: a.from.y + (a.to.y - a.from.y) * a.t };
      s.lift = Math.sin(a.t * Math.PI) * a.h;
      for (const l of s.legs) {
        const rest = this.restPoint(s, l);
        l.foot = { x: s.pos.x + (rest.x - s.pos.x) * 0.5, y: s.pos.y + (rest.y - s.pos.y) * 0.5 };
        l.grip = { el: null, dx: 0, dy: 0 };
        l.t = 1;
      }
      if (a.t >= 1) {
        s.air = null;
        s.lift = 0;
        this.replant(s);
        if (s.chainLeft > 0 && this.acrobat) {
          s.chainLeft--;
          this.startSilk(s, "swing");
        }
      }
    }
    // ---------- Drawing ----------
    /** A small cobweb where food is taken to be eaten. */
    drawNest() {
      const c = this.ctx, n = this.nestPoint(), R = 58;
      c.save();
      c.strokeStyle = "rgba(225, 235, 255, 0.28)";
      c.lineWidth = 0.8;
      const spokes = 9;
      c.beginPath();
      for (let i = 0; i < spokes; i++) {
        const a = i / spokes * TAU2 + 0.2;
        c.moveTo(n.x, n.y);
        c.lineTo(n.x + Math.cos(a) * R, n.y + Math.sin(a) * R);
      }
      for (let ring = 1; ring <= 5; ring++) {
        const r = ring / 5 * R * 0.92;
        for (let i = 0; i <= spokes; i++) {
          const a = i / spokes * TAU2 + 0.2, a0 = (i - 1) / spokes * TAU2 + 0.2;
          const p = { x: n.x + Math.cos(a) * r, y: n.y + Math.sin(a) * r };
          if (i === 0) c.moveTo(p.x, p.y);
          else {
            const m = (a + a0) / 2, sag = r * 0.9;
            c.quadraticCurveTo(n.x + Math.cos(m) * sag, n.y + Math.sin(m) * sag, p.x, p.y);
          }
        }
      }
      c.stroke();
      c.restore();
    }
    knee(hip, foot, a, b, side) {
      const dx = foot.x - hip.x, dy = foot.y - hip.y;
      const d = Math.min(a + b - 0.01, Math.max(Math.abs(a - b) + 0.01, Math.hypot(dx, dy)));
      const base = Math.atan2(dy, dx);
      const cos = (a * a + d * d - b * b) / (2 * a * d);
      const ang = base - side * Math.acos(Math.max(-1, Math.min(1, cos)));
      return { x: hip.x + Math.cos(ang) * a, y: hip.y + Math.sin(ang) * a };
    }
    boxesOf(m) {
      if (m.range) return lineBoxes(m.range);
      if (m.el && m.el.isConnected) {
        const r = m.el.getBoundingClientRect();
        return r.width > 0 ? [{ left: r.left, top: r.top, right: r.right, bottom: r.bottom }] : [];
      }
      return [];
    }
    /** Strands between the places spiders have been, and orb webs over the pictures they've taken. */
    drawWebs(now) {
      const c = this.ctx, sx = window.scrollX, sy = window.scrollY;
      this.strands = this.strands.filter((k) => now - k.born < k.life);
      this.orbs = this.orbs.filter((o) => now - o.born < o.life && o.el.isConnected);
      c.save();
      c.lineWidth = 0.8;
      for (const k of this.strands) {
        if (!k.at || k.sx !== sx || k.sy !== sy || now - k.at > 300) {
          k.pa = this.anchorPoint(k.a);
          k.pb = this.anchorPoint(k.b);
          k.at = now;
          k.sx = sx;
          k.sy = sy;
        }
        if (!k.pa || !k.pb) continue;
        const age = Math.max(0, now - k.born), a = clamp01(age / 200) * clamp01((k.life - age) / 2500);
        const mx = (k.pa.x + k.pb.x) / 2, my = (k.pa.y + k.pb.y) / 2 + k.sag * 2;
        c.globalAlpha = a * 0.5;
        c.strokeStyle = "#e8eefc";
        c.beginPath();
        c.moveTo(k.pa.x, k.pa.y);
        c.quadraticCurveTo(mx, my, k.pb.x, k.pb.y);
        c.stroke();
        c.globalAlpha = a * 0.85;
        c.fillStyle = "#f4f7ff";
        c.beginPath();
        c.arc(k.pa.x, k.pa.y, 1.4, 0, TAU2);
        c.arc(k.pb.x, k.pb.y, 1.4, 0, TAU2);
        c.fill();
      }
      for (const o of this.orbs) this.drawOrb(o, now);
      c.restore();
    }
    drawOrb(o, now) {
      const c = this.ctx, r = o.el.getBoundingClientRect();
      if (r.bottom < -40 || r.top > this.h + 40 || r.width < 4 || r.height < 4) return;
      const age = Math.max(0, now - o.born), p = clamp01(age / (o.build * 1e3)), a = clamp01((o.life - age) / 2500);
      const g = this.orbGeom(o, r), n = g.ends.length;
      const nearest = (q, pts) => pts.reduce((b, x) => dist2(x, q) < dist2(b, q) ? x : b, pts[0]);
      c.globalAlpha = a * 0.36 * clamp01(p * 3);
      c.fillStyle = "#05080c";
      c.fillRect(r.left, r.top, r.width, r.height);
      c.strokeStyle = "#e8eefc";
      c.lineWidth = 0.8;
      const corners = [{ x: r.left, y: r.top }, { x: r.right, y: r.top }, { x: r.right, y: r.bottom }, { x: r.left, y: r.bottom }];
      c.globalAlpha = a * 0.55 * clamp01(p * 8);
      c.beginPath();
      for (const k of corners) {
        const e = nearest(k, g.ends);
        c.moveTo(k.x, k.y);
        c.lineTo(e.x, e.y);
      }
      if (p > 0.08) {
        for (const b of o.bridges) {
          const q = this.anchorPoint(b);
          if (!q) continue;
          const k = nearest(q, corners), sag = Math.min(40, dist2(k, q) * 0.06);
          c.moveTo(k.x, k.y);
          c.quadraticCurveTo((k.x + q.x) / 2, (k.y + q.y) / 2 + sag, q.x, q.y);
        }
      }
      c.stroke();
      const fN = p < 0.2 ? Math.ceil(p / 0.2 * n) : n;
      c.globalAlpha = a * 0.62;
      c.beginPath();
      c.moveTo(g.ends[0].x, g.ends[0].y);
      for (let i = 1; i <= fN; i++) c.lineTo(g.ends[i % n].x, g.ends[i % n].y);
      const sN = p < 0.2 ? 0 : p < 0.5 ? Math.min(n, Math.floor((p - 0.2) / 0.3 * n) + 1) : n;
      for (let i = 0; i < sN; i++) {
        c.moveTo(g.hub.x, g.hub.y);
        c.lineTo(g.ends[i].x, g.ends[i].y);
      }
      c.stroke();
      if (p > 0.5) {
        const K = Math.floor((p - 0.5) / 0.5 * o.rings * n);
        c.globalAlpha = a * 0.5;
        c.lineWidth = 0.65;
        c.beginPath();
        for (let k = 0; k <= K; k++) {
          const q = this.spiralPoint(g, k, o.rings);
          if (k) c.lineTo(q.x, q.y);
          else c.moveTo(q.x, q.y);
        }
        c.stroke();
      }
      c.globalAlpha = a * 0.75;
      c.lineWidth = 0.8;
      c.beginPath();
      c.arc(g.hub.x, g.hub.y, 3.5, 0, TAU2);
      c.moveTo(g.hub.x + 7, g.hub.y);
      c.arc(g.hub.x, g.hub.y, 7, 0, TAU2);
      c.stroke();
      if (p >= 1) {
        c.font = `600 9px ${MONO}`;
        c.textBaseline = "middle";
        c.textAlign = "left";
        c.globalAlpha = a;
        const ty = r.top - 16 < 2 ? r.bottom + 4 : r.top - 16;
        this.drawTag(o.tag, r.left, ty, o.color, false, age - o.build * 1e3);
      }
      c.globalAlpha = 1;
    }
    /** A small typed-out label: outlined while reading, filled when scraped. */
    drawTag(text, x, y, col, solid, age) {
      const c = this.ctx;
      const shown = text.slice(0, Math.max(1, Math.floor(age / 16)));
      const tw = c.measureText(shown).width + 8, th = 12;
      x = Math.max(2, Math.min(this.w - tw - 2, x));
      if (solid) {
        c.fillStyle = col;
        c.fillRect(x, y, tw, th);
        c.fillStyle = "#0b0f1a";
      } else {
        c.fillStyle = "rgba(6, 9, 16, 0.88)";
        c.fillRect(x, y, tw, th);
        c.strokeStyle = col;
        c.lineWidth = 1;
        c.strokeRect(x + 0.5, y + 0.5, tw - 1, th - 1);
        c.fillStyle = col;
      }
      c.fillText(shown, x + 4, y + th / 2 + 0.5);
    }
    brackets(l, t, r, b, len) {
      const c = this.ctx;
      c.beginPath();
      c.moveTo(l, t + len);
      c.lineTo(l, t);
      c.lineTo(l + len, t);
      c.moveTo(r - len, t);
      c.lineTo(r, t);
      c.lineTo(r, t + len);
      c.moveTo(r, b - len);
      c.lineTo(r, b);
      c.lineTo(r - len, b);
      c.moveTo(l + len, b);
      c.lineTo(l, b);
      c.lineTo(l, b - len);
      c.stroke();
    }
    drawMarks(now) {
      const c = this.ctx, sx = window.scrollX, sy = window.scrollY;
      this.marks = this.marks.filter((m) => now - m.born < m.life);
      c.font = `600 9px ${MONO}`;
      c.textBaseline = "middle";
      c.textAlign = "left";
      for (const m of this.marks) {
        if (!m.at || m.sx !== sx || m.sy !== sy || now - m.at > 350) {
          m.boxes = this.boxesOf(m);
          m.at = now;
          m.sx = sx;
          m.sy = sy;
        }
        const B = m.boxes;
        if (!B.length) continue;
        const r0 = B[0];
        if (r0.bottom < -40 || r0.top > this.h + 40) continue;
        const age = Math.max(0, now - m.born);
        const a = clamp01(age / 140) * clamp01((m.life - age) / 1400);
        c.globalAlpha = a;
        c.fillStyle = m.color;
        c.strokeStyle = m.color;
        if (m.kind === "word") {
          c.globalAlpha = a * 0.55;
          for (const b of B) c.fillRect(b.left, b.bottom - 1, b.right - b.left, 1);
        } else if (m.kind === "lnk" && !m.solid) {
          c.lineWidth = 1;
          for (const b of B) {
            c.globalAlpha = a * 0.12;
            c.fillRect(b.left - 1.5, b.top - 1, b.right - b.left + 3, b.bottom - b.top + 2);
            c.globalAlpha = a * 0.85;
            c.strokeRect(b.left - 1.5, b.top - 1, b.right - b.left + 3, b.bottom - b.top + 2);
          }
        } else if (m.kind === "img") {
          const w = r0.right - r0.left, h = r0.bottom - r0.top;
          const e = Math.min(1, age / 380), ease = 1 - Math.pow(1 - e, 3), pad = 16 * (1 - ease) + 3;
          c.lineWidth = 1.5;
          this.brackets(r0.left - pad, r0.top - pad, r0.right + pad, r0.bottom + pad, Math.min(18, w / 4, h / 4));
          if (age < 2400) {
            const yy = r0.top + age % 1200 / 1200 * h;
            c.globalAlpha = a * 0.14;
            c.fillRect(r0.left, Math.max(r0.top, yy - 18), w, Math.min(18, yy - r0.top));
            c.globalAlpha = a * 0.9;
            c.fillRect(r0.left, yy, w, 1);
            const cx = r0.left + w / 2, cy = r0.top + h / 2;
            c.globalAlpha = a * 0.6;
            c.lineWidth = 1;
            c.beginPath();
            c.moveTo(cx - 7, cy);
            c.lineTo(cx + 7, cy);
            c.moveTo(cx, cy - 7);
            c.lineTo(cx, cy + 7);
            c.stroke();
          }
          if (m.caption && age > 300) {
            const shown = m.caption.slice(0, Math.floor((age - 300) / 14));
            c.globalAlpha = a;
            const tw = Math.min(c.measureText(shown).width + 8, Math.max(w, 120));
            c.fillStyle = "rgba(6, 9, 16, 0.85)";
            c.fillRect(r0.left, r0.bottom + pad + 2, tw, 12);
            c.fillStyle = m.color;
            c.fillText(shown, r0.left + 4, r0.bottom + pad + 8.5, tw - 8);
          }
        } else if (m.kind === "hop") {
          const pulse = 0.6 + Math.sin(now / 90) * 0.4;
          c.lineWidth = 1.5;
          c.globalAlpha = a * pulse;
          for (const b of B) c.strokeRect(b.left - 3, b.top - 3, b.right - b.left + 6, b.bottom - b.top + 6);
          c.globalAlpha = a;
          this.brackets(r0.left - 7, r0.top - 7, r0.right + 7, r0.bottom + 7, 6);
        } else {
          const fill = m.kind === "title" ? 0.3 : m.solid ? 0.42 : 0.24;
          for (const b of B) {
            c.globalAlpha = a * fill;
            c.fillRect(b.left - 1, b.top, b.right - b.left + 2, b.bottom - b.top);
            c.globalAlpha = a * 0.9;
            c.fillRect(b.left - 1, b.bottom - 1, b.right - b.left + 2, 1);
          }
          if (m.solid) {
            c.globalAlpha = a * 0.8;
            c.lineWidth = 1;
            for (const b of B) c.strokeRect(b.left - 1.5, b.top - 0.5, b.right - b.left + 3, b.bottom - b.top + 1);
          }
        }
        const tagFor = m.kind === "img" || m.kind === "hop" ? m.life : 6500;
        if (m.tag && age < tagFor) {
          c.globalAlpha = a * clamp01((tagFor - age) / 600);
          const pad = m.kind === "img" ? 16 * (1 - Math.min(1, age / 380)) + 3 : m.kind === "hop" ? 7 : 0;
          let ty = r0.top - pad - 14;
          if (ty < 2) ty = r0.bottom + pad + 2;
          this.drawTag(m.tag, r0.left - pad, ty, m.color, m.solid || m.kind === "hop", age);
        }
      }
      c.globalAlpha = 1;
    }
    draw(now) {
      const c = this.ctx;
      c.clearRect(0, 0, this.w, this.h);
      c.lineCap = "round";
      if (this.carry && this.creature === "spider") this.drawNest();
      if (this.strands.length || this.orbs.length) this.drawWebs(now);
      this.drawMarks(now);
      for (const k of this.crumbs) {
        c.globalAlpha = Math.max(0, k.life / k.max);
        c.fillStyle = k.color;
        c.fillRect(k.x - k.size / 2, k.y - k.size / 2, k.size, k.size);
      }
      c.globalAlpha = 1;
      if (this.creature === "octopus") {
        for (const o of this.octopi) o.draw(c, now, MONO);
      } else {
        for (const s of this.spiders) {
          const L = s.lurk;
          const r = L && L.el.isConnected ? L.el.getBoundingClientRect() : null;
          if (r && (L.phase !== "go" || s.pos.x > r.left && s.pos.x < r.right && s.pos.y > r.top && s.pos.y < r.bottom)) {
            c.save();
            c.beginPath();
            c.rect(0, 0, this.w, this.h);
            c.rect(r.left, r.top, r.width, r.height);
            c.clip("evenodd");
            this.drawSpider(s, now);
            c.restore();
          } else this.drawSpider(s, now);
        }
      }
      this.drawFade(now);
      if (this.hudOn) this.drawHud(now);
    }
    drawSpider(s, now) {
      const c = this.ctx;
      const hx = Math.cos(s.heading), hy = Math.sin(s.heading);
      const up = 1 + s.lift / 260;
      const real = this.look !== "neon";
      const bodyLen = 26 * s.size * up, bodyW = 9 * s.size * up;
      const L = s.lift;
      const chomp = s.munch || s.duel ? Math.sin(now / 32) * 1.6 : 0;
      const bx = s.pos.x + hx * chomp, by = s.pos.y - L + hy * chomp;
      const k = s.size * up;
      const rear = real ? { x: bx - hx * 21 * k, y: by - hy * 21 * k } : { x: bx - hx * bodyLen * 0.5, y: by - hy * bodyLen * 0.5 };
      if (this.websOn && s.lastAnchor) {
        const q = this.anchorPoint(s.lastAnchor);
        if (q && dist2(q, rear) < 900) {
          c.strokeStyle = "#eef3ff";
          c.globalAlpha = 0.55;
          c.lineWidth = 0.8;
          c.beginPath();
          c.moveTo(q.x, q.y);
          c.lineTo(rear.x, rear.y);
          c.stroke();
          c.globalAlpha = 1;
        }
      }
      if (s.tether && (this.readOn || this.crawlOn)) {
        const t = s.tether.p;
        c.strokeStyle = s.colors.joint;
        c.globalAlpha = 0.32;
        c.lineWidth = 0.7;
        c.beginPath();
        c.moveTo(t.x, t.y);
        c.lineTo(rear.x, rear.y);
        c.stroke();
        c.globalAlpha = 0.6;
        c.fillStyle = s.colors.joint;
        c.beginPath();
        c.arc(t.x, t.y, 1.6, 0, TAU2);
        c.fill();
        c.globalAlpha = 1;
      }
      if (s.silk) {
        const sk = s.silk;
        c.strokeStyle = "rgba(235, 242, 255, 0.8)";
        c.lineWidth = 1;
        c.beginPath();
        c.moveTo(sk.anchor.x, sk.anchor.y);
        c.lineTo(rear.x, rear.y);
        c.stroke();
        c.fillStyle = "rgba(235, 242, 255, 0.9)";
        c.beginPath();
        c.arc(sk.anchor.x, sk.anchor.y, 2, 0, TAU2);
        c.fill();
      }
      const H = this.hop;
      if (H && H.who === s && H.phase !== "go" && H.el.isConnected) {
        const r = H.el.getBoundingClientRect();
        c.strokeStyle = MARK_COLOR.hop;
        c.lineWidth = 0.8;
        c.globalAlpha = 0.75;
        c.beginPath();
        for (const p of [[r.left, r.top], [r.right, r.top], [r.right, r.bottom], [r.left, r.bottom]]) {
          c.moveTo(bx, by);
          c.lineTo(p[0], p[1]);
        }
        c.stroke();
        c.globalAlpha = 1;
      }
      const R = s.reading;
      if (R && R.at >= 0 && !R.scan) {
        const tok = R.tokens[R.i];
        const q = tok && tok.kind !== "word" ? firstRect(tok.range) : null;
        if (q) {
          c.strokeStyle = tok.kind === "title" ? s.colors.leg : MARK_COLOR[tok.kind];
          c.globalAlpha = 0.65;
          c.lineWidth = 0.8;
          c.beginPath();
          c.moveTo(bx + hx * 12 * k, by + hy * 12 * k);
          c.lineTo(q.left, q.top);
          c.stroke();
          c.globalAlpha = 1;
        }
      }
      if (L > 0.5) {
        c.fillStyle = `rgba(0, 0, 0, ${Math.max(0.08, 0.32 - L / 400)})`;
        c.beginPath();
        c.ellipse(s.pos.x, s.pos.y + 4, (16 + L * 0.12) * s.size, (6 + L * 0.04) * s.size, 0, 0, TAU2);
        c.fill();
      }
      if (s.torn) {
        const t = s.torn, alpha = clamp01(t.life);
        c.save();
        c.globalAlpha = alpha * 0.9;
        c.strokeStyle = t.color;
        c.lineWidth = 1;
        c.beginPath();
        c.moveTo(rear.x, rear.y + L);
        c.lineTo(t.pos.x, t.pos.y);
        c.stroke();
        c.translate(t.pos.x, t.pos.y);
        c.rotate(t.angle);
        c.fillStyle = t.color;
        c.fillRect(-t.w / 2, -11, t.w, 22);
        c.fillStyle = "#fff";
        c.font = `600 13px ${MONO}`;
        c.textAlign = "center";
        c.textBaseline = "middle";
        c.fillText(t.text, 0, 1);
        c.restore();
      }
      if (s.haul && s.haul.text) {
        const h = s.haul;
        c.save();
        c.font = `600 13px ${MONO}`;
        const w = c.measureText(h.text).width + 14;
        if (h.phase === "haul") {
          c.strokeStyle = h.color;
          c.lineWidth = 1;
          c.beginPath();
          c.moveTo(rear.x, rear.y);
          c.lineTo(h.pos.x, h.pos.y);
          c.stroke();
        }
        c.translate(h.pos.x, h.pos.y - L);
        c.rotate(h.angle);
        c.fillStyle = h.color;
        c.fillRect(-w / 2, -11, w, 22);
        c.fillStyle = "#fff";
        c.textAlign = "center";
        c.textBaseline = "middle";
        c.fillText(h.text, 0, 1);
        c.restore();
      }
      if (s.chew > 0 && s.target) {
        const pulse = (Math.sin(now / 120) + 1) / 2;
        c.strokeStyle = s.colors.leg;
        c.globalAlpha = 0.35 + pulse * 0.4;
        c.lineWidth = 1;
        c.beginPath();
        c.arc(s.pos.x, s.pos.y, 18 + pulse * 10, 0, TAU2);
        c.stroke();
        c.globalAlpha = 1;
      }
      const feet = s.legs.map((l, li) => {
        const lift = l.t < 1 ? Math.sin(l.t * Math.PI) * 10 : 0;
        let foot = { x: l.foot.x, y: l.foot.y - lift - L };
        if (s.duel && li < 2) {
          const strike = Math.abs(Math.sin(now / 70 + li * 1.3));
          const r = l.reach * (0.6 + strike * 0.35);
          const sideA = s.heading + l.side * 0.45;
          foot = { x: bx + Math.cos(sideA) * r, y: by + Math.sin(sideA) * r - 8 - strike * 6 };
        }
        return foot;
      });
      if (real) {
        this.drawReal(s, now, bx, by, hx, hy, k, feet);
        return;
      }
      s.legs.forEach((l, li) => {
        const hipA = s.heading + l.side * Math.PI / 2;
        const along = (l.angle * l.side < 1.4 ? 0.3 : -0.15) * bodyLen;
        const hip = { x: bx + hx * along + Math.cos(hipA) * bodyW * 0.5, y: by + hy * along + Math.sin(hipA) * bodyW * 0.5 };
        const foot = feet[li];
        const kn = this.knee(hip, foot, l.upper, l.lower, l.side);
        c.strokeStyle = s.colors.leg;
        c.lineWidth = 1.6;
        c.beginPath();
        c.moveTo(hip.x, hip.y);
        c.lineTo(kn.x, kn.y);
        c.lineTo(foot.x, foot.y);
        c.stroke();
        c.fillStyle = s.colors.joint;
        c.beginPath();
        c.arc(kn.x, kn.y, 2.6, 0, TAU2);
        c.fill();
        c.beginPath();
        c.arc(foot.x, foot.y, l.grip.el && l.t === 1 ? 3.2 : 2.2, 0, TAU2);
        c.fill();
      });
      c.save();
      c.translate(bx, by);
      c.rotate(s.heading);
      c.strokeStyle = s.colors.body;
      c.lineWidth = 2;
      c.fillStyle = "rgba(10,14,30,0.55)";
      c.fillRect(-bodyLen / 2, -bodyW / 2, bodyLen, bodyW);
      c.strokeRect(-bodyLen / 2, -bodyW / 2, bodyLen, bodyW);
      c.fillStyle = s.colors.tag;
      c.beginPath();
      c.arc(bodyLen * 0.28, 0, 3.2 * s.size, 0, TAU2);
      c.fill();
      c.restore();
    }
    /** The anatomical spider: abdomen and carapace, eight eyes, palps and fangs, three-part legs, a faint glow. */
    drawReal(s, now, bx, by, hx, hy, k, feet) {
      const c = this.ctx, h = s.hue;
      const line = `hsl(${h}, 95%, 62%)`, bright = `hsl(${h}, 100%, 85%)`;
      const W = (lx, ly) => ({ x: bx + lx * hx - ly * hy, y: by + lx * hy + ly * hx });
      const segs = s.legs.map((l, li) => {
        const hp = HIP[li >> 1];
        const hip = W(hp[0] * k, hp[1] * k * l.side);
        const foot = feet[li];
        const kn = this.knee(hip, foot, l.upper, l.lower, l.side);
        const dx = foot.x - kn.x, dy = foot.y - kn.y, dl = Math.hypot(dx, dy) || 1, bend = 2.4 * k * l.side;
        const m = { x: kn.x + dx * 0.7 - dy / dl * bend, y: kn.y + dy * 0.7 + dx / dl * bend };
        return [hip, kn, m, foot];
      });
      c.save();
      c.lineJoin = "round";
      c.strokeStyle = line;
      c.shadowColor = line;
      c.shadowBlur = 4;
      const widths = [1.7, 1.2, 0.8];
      for (let j = 0; j < 3; j++) {
        c.lineWidth = widths[j] * Math.max(0.8, k);
        c.beginPath();
        for (const sg of segs) {
          c.moveTo(sg[j].x, sg[j].y);
          c.lineTo(sg[j + 1].x, sg[j + 1].y);
        }
        c.stroke();
      }
      c.shadowBlur = 0;
      c.fillStyle = bright;
      for (const sg of segs) {
        c.beginPath();
        c.arc(sg[1].x, sg[1].y, 1.6 * k, 0, TAU2);
        c.fill();
        c.beginPath();
        c.arc(sg[2].x, sg[2].y, 1.1 * k, 0, TAU2);
        c.fill();
      }
      c.fillStyle = line;
      s.legs.forEach((l, li) => {
        if (l.grip.el && l.t === 1) {
          const f = segs[li][3];
          c.beginPath();
          c.arc(f.x, f.y, 1.5 * k, 0, TAU2);
          c.fill();
        }
      });
      c.translate(bx, by);
      c.rotate(s.heading);
      const br = 1 + Math.sin(now / 650 + s.wobble) * 0.035;
      c.fillStyle = "rgba(0, 0, 0, 0.18)";
      c.beginPath();
      c.ellipse(-4 * k, 2.5 * k, 17 * k, 8 * k, 0, 0, TAU2);
      c.fill();
      c.shadowColor = line;
      c.shadowBlur = 8;
      c.fillStyle = "rgba(7, 9, 15, 0.88)";
      c.strokeStyle = line;
      c.lineWidth = 1.3;
      c.beginPath();
      c.ellipse(-10.5 * k, 0, 10.5 * k * br, 7.4 * k * br, 0, 0, TAU2);
      c.fill();
      c.stroke();
      c.beginPath();
      c.ellipse(4 * k, 0, 6.8 * k, 5.6 * k, 0, 0, TAU2);
      c.fill();
      c.stroke();
      c.shadowBlur = 0;
      c.globalAlpha = 0.6;
      c.lineWidth = 0.9;
      c.beginPath();
      for (let i = 0; i < 4; i++) {
        const x = -5 * k - i * 3.6 * k, wv = (4.6 - i * 0.8) * k;
        c.moveTo(x - 2.2 * k, -wv);
        c.lineTo(x, 0);
        c.lineTo(x - 2.2 * k, wv);
      }
      c.stroke();
      c.globalAlpha = 0.35;
      c.beginPath();
      c.moveTo(-3 * k, 0);
      c.lineTo(-19 * k, 0);
      c.moveTo(1.5 * k, 0);
      c.lineTo(5 * k, 0);
      c.stroke();
      c.globalAlpha = 1;
      const jaw = s.munch || s.duel ? Math.abs(Math.sin(now / 45)) * 0.8 * k : 0;
      c.lineWidth = 1.4;
      c.beginPath();
      c.moveTo(10 * k, -1.7 * k);
      c.lineTo(13.2 * k, -1.1 * k - jaw);
      c.moveTo(10 * k, 1.7 * k);
      c.lineTo(13.2 * k, 1.1 * k + jaw);
      c.stroke();
      const wig = Math.sin(now / (s.munch ? 70 : 190) + s.wobble) * 1.3 * k;
      c.lineWidth = 1.1;
      c.beginPath();
      for (const sd of [1, -1]) {
        c.moveTo(9.4 * k, 3.1 * k * sd);
        c.lineTo(13.4 * k, 5.4 * k * sd);
        c.lineTo(17 * k + wig * 0.5, (4.2 * k + wig) * sd);
      }
      c.stroke();
      c.fillStyle = bright;
      c.shadowColor = bright;
      c.shadowBlur = 4;
      for (const [ex, ey, er] of [[9.2, 1.3, 1.05], [9.2, -1.3, 1.05], [8.3, 2.9, 0.7], [8.3, -2.9, 0.7], [7.5, 1.1, 0.6], [7.5, -1.1, 0.6], [7, 2.4, 0.55], [7, -2.4, 0.55]]) {
        c.beginPath();
        c.arc(ex * k, ey * k, er * k, 0, TAU2);
        c.fill();
      }
      c.restore();
    }
    /** The moment of a hop: a scan line sweeps the screen and the next page's name flashes up. */
    drawFade(now) {
      const f = this.fade;
      if (!f) return;
      const age = Math.max(0, now - f.t0);
      if (age > 1e3) {
        this.fade = null;
        return;
      }
      const c = this.ctx, col = `hsl(${f.hue}, 100%, 60%)`;
      const y = age / 750 * this.h;
      c.save();
      c.globalAlpha = 0.5 * (1 - age / 1e3);
      c.fillStyle = col;
      c.fillRect(0, Math.max(0, y - 60), this.w, Math.min(60, y));
      c.globalAlpha = 0.9 * (1 - age / 1e3);
      c.fillRect(0, y, this.w, 2);
      c.globalAlpha = clamp01(age / 150) * (1 - age / 1e3);
      c.font = `700 22px ${MONO}`;
      c.textAlign = "center";
      c.textBaseline = "middle";
      c.fillStyle = "#fff";
      c.shadowColor = col;
      c.shadowBlur = 14;
      c.fillText(`\u2192 ${f.title.slice(0, 48)}`, this.w / 2, this.h * 0.42);
      c.restore();
    }
    fit(text, max) {
      const c = this.ctx;
      if (c.measureText(text).width <= max) return text;
      let t = text;
      while (t.length > 1 && c.measureText(t + "\u2026").width > max) t = t.slice(0, -1);
      return t + "\u2026";
    }
    /** Top-right panel: hop count, where it's been, and what it has read or scraped. */
    drawHud(now) {
      const c = this.ctx;
      if (!this.trail.length) this.trail.push({ url: location.href.split("#")[0], title: this.pageTitle() });
      const W = Math.min(270, this.w - 32), x = this.w - W - 16, y = 16, pad = 10;
      const cur = this.trail[this.trail.length - 1];
      const back = this.trail.slice(0, -1).reverse().slice(0, 3);
      const octo = this.creature === "octopus";
      let stat;
      if (octo) {
        const top = [...this.tally.entries()].sort((a, b) => b[1] - a[1]).slice(0, 3).map(([k, n]) => `${KIND_CODE[k]} ${n}`).join(" \xB7 ");
        stat = `scraped ${this.scraped.length}${top ? " \xB7 " + top : ""}`;
      } else stat = `read ${this.stats.words} \xB7 ent ${this.stats.ent} \xB7 lnk ${this.stats.lnk} \xB7 img ${this.stats.img}`;
      const H = pad * 2 + 16 + 17 + back.length * 14 + 16;
      c.save();
      c.fillStyle = "rgba(6, 9, 16, 0.82)";
      c.strokeStyle = "rgba(255, 255, 255, 0.14)";
      c.lineWidth = 1;
      c.beginPath();
      if (c.roundRect) c.roundRect(x, y, W, H, 8);
      else c.rect(x, y, W, H);
      c.fill();
      c.stroke();
      let yy = y + pad + 7;
      const blink = this.hop ? 0.5 + Math.sin(now / 80) * 0.5 : 0.65 + Math.sin(now / 400) * 0.35;
      c.globalAlpha = blink;
      c.fillStyle = "#ff3b5c";
      c.beginPath();
      c.arc(x + pad + 4, yy, 4, 0, TAU2);
      c.fill();
      c.globalAlpha = 1;
      c.font = `700 11px ${MONO}`;
      c.textBaseline = "middle";
      c.textAlign = "left";
      c.fillStyle = "#e9eef7";
      const mode = this.crawlOn ? `hop ${this.trail.length - 1}` : octo ? "scraping" : this.readOn ? "reading" : "crawling";
      c.fillText(mode, x + pad + 14, yy);
      c.textAlign = "right";
      c.fillStyle = "#8394ad";
      c.font = `600 9px ${MONO}`;
      c.fillText(this.hop ? this.hop.phase === "go" ? "SEEKING LINK" : "FOLLOWING" : octo ? "SCRAPE" : this.crawlOn ? "CRAWL" : "READ", x + W - pad, yy);
      yy += 17;
      c.textAlign = "left";
      c.font = `600 12.5px ${SANS}`;
      c.fillStyle = "#ffffff";
      c.fillText(this.fit(cur.title, W - pad * 2), x + pad, yy);
      c.font = `500 10.5px ${SANS}`;
      c.fillStyle = "#8394ad";
      for (const b of back) {
        yy += 14;
        c.fillText(this.fit(`\u2191 ${b.title}`, W - pad * 2), x + pad, yy);
      }
      yy += 16;
      c.font = `600 9.5px ${MONO}`;
      c.fillStyle = "#9fb3d1";
      c.fillText(this.fit(stat, W - pad * 2), x + pad, yy);
      c.restore();
    }
  };
  return __toCommonJS(skitter_exports);
})();
