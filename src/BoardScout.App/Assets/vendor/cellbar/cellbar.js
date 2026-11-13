/* Cell bar: the whole drive as a grid of small cells, coloured by what fills it.
 *
 *   const bar = CellBar.create(canvas, { colors: ["#4f8cff", ...] });   // one colour per bucket, in order
 *   bar.begin(totalBytes);          // a scan starts: empty bar for a drive of this size
 *   bar.progress(bytesPerBucket);   // while scanning: new cells take the colour of what was just found
 *   bar.finish(bytesPerBucket, usedBytes);  // done: cells sort into blocks, largest first; used space nobody read is dim grey
 *   bar.clear();  bar.destroy();
 *
 * It draws only while the canvas is on screen and the page is visible, shimmers at ~11 fps when idle, and stops by
 * itself once the canvas leaves the page.
 */
(function (root) {
  "use strict";

  const CELL = 11, GAP = 3, ROWS = 6, PITCH = CELL + GAP;
  const UNACC = [96, 108, 128];

  const hex = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
  const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
  const ease = (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

  function create(canvas, options) {
    const opts = options || {};
    const colors = (opts.colors || []).map(hex).concat([UNACC]); // the last entry is "used, but nobody could read it"
    const N = colors.length - 1; // real buckets
    const reduceMotion = opts.reduceMotion != null ? opts.reduceMotion : matchMedia("(prefers-reduced-motion: reduce)").matches;
    const g = canvas.getContext("2d");
    let cells = [], cols = 0, dpr = 1, assigned = 0, flashUntil = 0, total = 1, scanning = false, lastDraw = 0, dead = false;
    let bytes = new Array(N).fill(0), last = new Array(N).fill(0);

    const colMajor = (i) => Math.floor(i / ROWS) + (i % ROWS) * cols;
    const sum = (a) => a.reduce((x, y) => x + y, 0);
    const per = () => total / Math.max(1, cells.length);

    function layout() {
      dpr = Math.min(2, window.devicePixelRatio || 1);
      const w = canvas.clientWidth || 600, h = ROWS * PITCH - GAP + 4;
      canvas.style.height = h + "px";
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      const nc = Math.max(8, Math.floor((w + GAP) / PITCH));
      if (nc !== cols) {
        cols = nc;
        cells = Array.from({ length: cols * ROWS }, (_, i) => ({ from: -1, to: -1, t0: 0, seed: Math.abs(Math.sin(i * 12.9898) * 43758.5453) % 1 }));
        assigned = 0;
        if (finished) settle(true);
        else if (scanning) assign(true);
      }
      draw(performance.now());
    }

    /* while scanning, new cells take the colour of what was just found, so the bar fills with a mix */
    function assign(rebuild) {
      if (rebuild) { assigned = 0; last = new Array(N).fill(0); cells.forEach((c) => { c.from = c.to = -1; }); }
      const want = Math.min(cells.length, Math.floor(sum(bytes) / per()));
      if (want <= assigned) return;
      const delta = bytes.map((b, i) => Math.max(0, b - last[i]));
      let w = delta, s = sum(delta);
      if (s <= 0) { w = bytes; s = sum(bytes) || 1; }
      const now = performance.now();
      for (let i = assigned; i < want; i++) {
        let r = ((i * 0.6180339887) % 1) * s, k = 0;
        for (; k < N - 1; k++) { r -= w[k]; if (r <= 0) break; }
        const c = cells[colMajor(i)];
        c.from = c.to = k; c.t0 = now;
      }
      assigned = want;
      flashUntil = now + 300;
      last = bytes.slice();
    }

    /* done: one block per bucket, largest first, then the used space nobody could read, then free */
    let finished = false, usedBytes = 0;
    function settle(instant) {
      const p = per();
      const order = bytes.map((_, i) => i).sort((a, b) => bytes[b] - bytes[a]);
      const usedCells = Math.min(cells.length, Math.round(usedBytes / p));
      const seq = [];
      let cum = 0;
      for (const i of order) {
        cum += bytes[i];
        const upto = Math.min(usedCells, Math.round(cum / p));
        while (seq.length < upto) seq.push(i);
      }
      while (seq.length < usedCells) seq.push(N);
      const now = performance.now();
      for (let i = 0; i < cells.length; i++) {
        const c = cells[colMajor(i)], col = Math.floor(i / ROWS);
        c.from = c.to; c.to = i < seq.length ? seq[i] : -1;
        c.t0 = instant || reduceMotion ? now - 5000 : now + col * 12;
      }
      assigned = cells.length;
    }

    function rr(x, y) { g.beginPath(); g.roundRect(x, y, CELL, CELL, 2.5); g.fill(); }

    function draw(now) {
      const w = canvas.width / dpr, h = canvas.height / dpr;
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, w, h);
      const x0 = (w - (cols * PITCH - GAP)) / 2;
      for (let i = 0; i < cells.length; i++) {
        const col = Math.floor(i / ROWS), row = i % ROWS, c = cells[row * cols + col];
        const x = x0 + col * PITCH, y = 2 + row * PITCH;
        const p = c.t0 > now ? 0 : Math.min(1, (now - c.t0) / 450);
        const A = c.from >= 0 ? colors[c.from] : null, B = c.to >= 0 ? colors[c.to] : null;
        let rgb = null, a = 1;
        if (A && B) rgb = mix(A, B, ease(p));
        else if (B) { rgb = B; a = ease(p); }
        else if (A) { rgb = A; a = 1 - ease(p); }
        if (!rgb) { g.fillStyle = "rgba(255,255,255,0.055)"; rr(x, y); continue; }
        const tw = reduceMotion ? 0.9 : 0.8 + 0.2 * Math.sin(now * 0.0016 + c.seed * 6.283);
        const behind = assigned - (col * ROWS + row);
        const boost = scanning && now < flashUntil && behind >= 0 && behind < 24 ? 0.45 : 0;
        const unacc = c.to === N && c.from === N;
        const col3 = `${rgb[0] | 0},${rgb[1] | 0},${rgb[2] | 0}`;
        g.fillStyle = `rgba(${col3},${Math.min(1, (unacc ? 0.32 : tw) * a + boost)})`;
        if (boost > 0 || (!unacc && tw > 0.97)) { g.shadowColor = `rgba(${col3},0.9)`; g.shadowBlur = 8; }
        rr(x, y);
        g.shadowBlur = 0;
      }
    }

    function frame(now) {
      if (dead) return;
      if (!canvas.isConnected) { destroy(); return; }
      requestAnimationFrame(frame);
      if (document.hidden || !canvas.clientWidth) return;
      const busy = scanning || cells.some((c) => c.t0 > 0 && now - c.t0 < 600);
      if (!busy && now - lastDraw < (reduceMotion ? 1e9 : 90)) return;
      lastDraw = now;
      draw(now);
    }

    function destroy() { dead = true; ro.disconnect(); }
    const ro = new ResizeObserver(layout);
    ro.observe(canvas);
    layout();
    requestAnimationFrame(frame);

    return {
      begin(totalBytes) {
        total = Math.max(1, totalBytes); scanning = true; finished = false; bytes = new Array(N).fill(0); last = new Array(N).fill(0);
        assigned = 0; cells.forEach((c) => { c.from = c.to = -1; c.t0 = 0; });
      },
      progress(b) { bytes = b.slice(0, N); assign(false); },
      finish(b, used) { bytes = b.slice(0, N); usedBytes = used; scanning = false; finished = true; settle(false); },
      stop() { scanning = false; },
      clear() { scanning = false; finished = false; assigned = 0; bytes = new Array(N).fill(0); cells.forEach((c) => { c.from = c.to = -1; c.t0 = 0; }); },
      resize: layout,
      destroy,
    };
  }

  root.CellBar = { create };
})(typeof window !== "undefined" ? window : globalThis);
