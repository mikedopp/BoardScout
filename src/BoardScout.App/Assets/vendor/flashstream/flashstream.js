// FlashStream v1.1.0 — SSD/NVMe activity as light streaks through a NAND die grid. No dependencies.
// Built from FlashStream/src/flashStream.ts (F:/mikedopp/Drop/FlashStream); do not edit copies.
(function () {
'use strict';
/**
 * FlashStream: an SSD/NVMe drive drawn as a tilted grid of NAND dies with light
 * streaks moving between the controller, the dies and the PC. Every motion
 * follows the kernel's disk counters:
 *   streak count  ← operations per second (1:1 when quiet, log-compressed when busy)
 *   streak speed  ← throughput
 *   streak length ← average I/O size (4 KB spark … 1 MB ribbon)
 *   orbit beads   ← queue depth
 *   controller    ← busy %
 * Which die a streak lands on is random: Windows does not report it, and the
 * drive's controller remaps addresses internally. Kept free of imports so the
 * node tests can load it directly.
 *
 * FlashStream 1.1 · source of truth: F:\mikedopp\Drop\FlashStream\src\flashStream.ts
 * (copies: DedupApp src/lib/flashStream.ts, BoardScout Assets/vendor/flashstream/flashstream.js).
 */
const VERSION = '1.1.0';
/** Logical channel count: NVMe controllers usually run 8, SATA SSDs 4, USB/SD flash 2. */
function lanesFor(mediaType) {
    if (/nvme/i.test(mediaType))
        return 8;
    if (/ssd/i.test(mediaType))
        return 4;
    return 2;
}
/** Streaks spawned per second for a measured operation rate. */
function streakRate(opsPerSecond) {
    if (!(opsPerSecond > 0))
        return 0;
    return opsPerSecond < 3 ? opsPerSecond : Math.min(60, 3 + 12 * Math.log10(opsPerSecond / 3));
}
/** Streak speed in px/s at the 190 px reference height. */
function streakSpeed(bytesPerSecond) {
    return 60 + 48 * Math.log10(1 + Math.max(0, bytesPerSecond) / 1e6);
}
/** Streak tail length in px at the 190 px reference height, from the average I/O size. */
function streakTail(bytesPerSecond, opsPerSecond) {
    const avg = opsPerSecond > 0 ? bytesPerSecond / opsPerSecond : 4096;
    return Math.max(6, Math.min(150, 7 + 19 * Math.log2(Math.max(1, avg / 4096))));
}
const rgba = (c, a) => `rgba(${c[0]},${c[1]},${c[2]},${a})`;
const rand = (a, b) => a + Math.random() * (b - a);
function createFlashStream(canvas, options) {
    const o = {
        ways: 4, read: [88, 166, 255], write: [245, 158, 11], ink: [139, 148, 158], ok: [34, 197, 94],
        ...options,
    };
    const ctx = canvas.getContext('2d');
    if (!ctx)
        return { update() { }, setScan() { }, destroy() { } };
    const reduced = typeof matchMedia !== 'undefined' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    let still = reduced;
    const N = o.lanes * o.ways;
    const glow = new Float32Array(N);
    const kind = new Uint8Array(N); // 0 read, 1 write
    const streaks = [];
    const motes = Array.from({ length: 54 }, () => ({
        x: rand(-1.3, 1.3), z: rand(-0.05, 1.2), y: rand(0, 0.6), v: rand(0.012, 0.045), ph: rand(0, 6.28), r: rand(0.5, 1.3),
    }));
    let sample = { readBps: 0, writeBps: 0, readOps: 0, writeOps: 0, queueDepth: 0, busy: 0 };
    let scan = null;
    let W = 0, H = o.height, raf = 0, last = performance.now(), visible = true, orbit = 0, gone = 0, destroyed = false;
    const acc = { read: 0, write: 0 };
    // ── Geometry: a tilted board under a perspective camera ──────────────────
    let G;
    const path = (pts) => {
        const cum = [0];
        for (let k = 1; k < pts.length; k++)
            cum.push(cum[k - 1] + Math.hypot(pts[k][0] - pts[k - 1][0], pts[k][1] - pts[k - 1][1]));
        return { pts, cum, len: cum[cum.length - 1] };
    };
    function layout() {
        const dpr = window.devicePixelRatio || 1;
        W = canvas.clientWidth;
        H = o.height;
        canvas.width = Math.round(W * dpr);
        canvas.height = Math.round(H * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        const d0 = 1.4, depth = 1.6;
        const halfNear = Math.min(W * 0.3, H * 1.25);
        const cx = 14 + halfNear, fx = halfNear * d0 / 1.12;
        const A = -0.36 * H, B = 1.76 * H;
        const P = (x, y, z) => { const D = d0 + z * depth; return [cx + x * fx / D, A + B * (1 - y) / D]; };
        const laneX = (i) => -0.86 + 1.72 * (i + 0.5) / o.lanes;
        const wayZ = (j) => 0.36 + j * (0.68 / Math.max(1, o.ways - 1));
        const lanePaths = [], laneBack = [];
        for (let i = 0; i < o.lanes; i++)
            for (let j = 0; j < o.ways; j++) {
                const pts = [P(0, 0.006, 0.17), P(laneX(i), 0.006, 0.27), P(laneX(i), 0.006, wayZ(j))];
                lanePaths[i * o.ways + j] = path(pts);
                laneBack[i * o.ways + j] = path(pts.slice().reverse());
            }
        // Host link: a bundle of strands arcing from the controller to the PC port.
        const start = P(0.22, 0.01, 0.1), port = [W - 18, H * 0.4];
        const strands = [], strandsBack = [];
        const c1 = [start[0] + (port[0] - start[0]) * 0.3, start[1] + 6];
        const c2 = [start[0] + (port[0] - start[0]) * 0.68, port[1] - 4];
        for (let s = -2; s <= 2; s++) {
            const pts = [];
            for (let k = 0; k <= 32; k++) {
                const t = k / 32, u = 1 - t;
                const x = u * u * u * start[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t * t * t * port[0];
                const y = u * u * u * start[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t * t * t * port[1];
                pts.push([x, y + s * (1.2 + 4.2 * u * Math.sin(Math.PI * Math.min(1, t * 1.6 + 0.2)))]);
            }
            strands.push(path(pts));
            strandsBack.push(path(pts.slice().reverse()));
        }
        G = { P, laneX, wayZ, lanePaths, laneBack, strands, strandsBack, port, scale: H / 190 };
        streaks.length = 0; // in-flight streaks hold paths from the old geometry
    }
    function at(p, d) {
        if (d <= 0)
            return p.pts[0];
        if (d >= p.len)
            return p.pts[p.pts.length - 1];
        let k = 1;
        while (p.cum[k] < d)
            k++;
        const f = (d - p.cum[k - 1]) / (p.cum[k] - p.cum[k - 1] || 1), a = p.pts[k - 1], b = p.pts[k];
        return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f];
    }
    function spawn(dir) {
        if (streaks.length > 420)
            return;
        const bps = dir === 'read' ? sample.readBps : sample.writeBps;
        const ops = dir === 'read' ? sample.readOps : sample.writeOps;
        const speed = streakSpeed(bps) * G.scale * rand(0.85, 1.15);
        const tail = streakTail(bps, ops) * G.scale * rand(0.8, 1.2);
        const col = dir === 'read' ? o.read : o.write;
        const die = (Math.random() * N) | 0;
        const strand = G.strands[(Math.random() * G.strands.length) | 0];
        const strandBack = G.strandsBack[G.strands.indexOf(strand)];
        const light = (a) => { glow[die] = Math.max(glow[die], a); kind[die] = dir === 'read' ? 0 : 1; };
        // The host bus is the fastest leg, so link streaks run quicker and longer.
        if (dir === 'read') {
            light(0.7); // the die senses the page, then the data travels out
            streaks.push({ p: G.laneBack[die], s: 0, speed, tail, col,
                arrive: () => streaks.push({ p: strand, s: 0, speed: speed * 2.4, tail: tail * 1.8, col }) });
        }
        else {
            streaks.push({ p: strandBack, s: 0, speed: speed * 2.4, tail: tail * 1.8, col,
                arrive: () => streaks.push({ p: G.lanePaths[die], s: 0, speed, tail, col, arrive: () => light(1) }) });
        }
    }
    function frame(now) {
        raf = requestAnimationFrame(frame);
        // A host that rebuilds its DOM just drops the canvas; clean up once it has stayed gone.
        if (!canvas.isConnected) {
            if (++gone > 30)
                destroy();
            return;
        }
        gone = 0;
        const wasStill = still;
        still = reduced || (o.motion ? !o.motion() : false);
        if (still && !wasStill)
            streaks.length = 0;
        // ~30 fps while moving, ~4 fps for a still picture, nothing while off screen.
        if (!visible || document.hidden || now - last < (still ? 250 : 30))
            return;
        const dt = Math.min(0.1, (now - last) / 1000);
        last = now;
        if (o.source)
            sample = o.source();
        const c = ctx;
        const s = sample, total = s.readBps + s.writeBps;
        const intensity = Math.min(1, Math.log10(1 + total / 1e5) / 3.6);
        const { P } = G;
        c.clearRect(0, 0, W, H);
        // Floor grid, fading into the distance.
        c.lineWidth = 1;
        for (let x = -1.5; x <= 1.501; x += 0.125) {
            const a = 0.055 * (1 - Math.abs(x) / 1.65) * (1 + intensity * 0.6);
            const p0 = P(x, 0, -0.08), p1 = P(x, 0, 1.28);
            const g = c.createLinearGradient(p0[0], p0[1], p1[0], p1[1]);
            g.addColorStop(0, rgba(o.ink, a));
            g.addColorStop(1, rgba(o.ink, 0));
            c.strokeStyle = g;
            c.beginPath();
            c.moveTo(p0[0], p0[1]);
            c.lineTo(p1[0], p1[1]);
            c.stroke();
        }
        for (let z = -0.05; z <= 1.25; z += 0.1) {
            const p0 = P(-1.5, 0, z), p1 = P(1.5, 0, z);
            c.strokeStyle = rgba(o.ink, 0.06 * (1 - z / 1.35) * (1 + intensity * 0.6));
            c.beginPath();
            c.moveTo(p0[0], p0[1]);
            c.lineTo(p1[0], p1[1]);
            c.stroke();
        }
        const quad = (x0, z0, x1, z1) => {
            const a = P(x0, 0, z0), b = P(x1, 0, z0), d = P(x1, 0, z1), e = P(x0, 0, z1);
            c.beginPath();
            c.moveTo(a[0], a[1]);
            c.lineTo(b[0], b[1]);
            c.lineTo(d[0], d[1]);
            c.lineTo(e[0], e[1]);
            c.closePath();
        };
        // Board and channel lanes.
        quad(-1, -0.02, 1, 1.12);
        c.fillStyle = 'rgba(255,255,255,0.018)';
        c.fill();
        c.strokeStyle = rgba(o.ink, 0.28);
        c.stroke();
        c.strokeStyle = rgba(o.ink, 0.13);
        for (let i = 0; i < o.lanes; i++) {
            const p = G.lanePaths[i * o.ways + o.ways - 1].pts;
            c.beginPath();
            c.moveTo(p[0][0], p[0][1]);
            c.lineTo(p[1][0], p[1][1]);
            c.lineTo(p[2][0], p[2][1]);
            c.stroke();
        }
        // NAND dies: flash and a light pillar when an operation lands.
        const hw = 0.86 / o.lanes * 0.72, hd = 0.075;
        for (let i = 0; i < o.lanes; i++)
            for (let j = 0; j < o.ways; j++) {
                const k = i * o.ways + j, x = G.laneX(i), z = G.wayZ(j);
                quad(x - hw, z - hd, x + hw, z + hd);
                c.fillStyle = 'rgba(12,16,22,0.85)';
                c.fill();
                if (scan != null && j * o.lanes + i < scan * N) {
                    c.fillStyle = rgba(o.ok, 0.13);
                    c.fill();
                }
                c.strokeStyle = rgba(o.ink, 0.3);
                c.stroke();
                const gl = glow[k];
                if (gl > 0.02) {
                    const col = kind[k] ? o.write : o.read;
                    c.globalCompositeOperation = 'lighter';
                    c.fillStyle = rgba(col, gl * 0.55);
                    c.fill();
                    c.strokeStyle = rgba(col, gl);
                    c.stroke();
                    const b = P(x, 0, z), t = P(x, 0.06 + 0.3 * gl, z);
                    const g = c.createLinearGradient(b[0], b[1], t[0], t[1]);
                    g.addColorStop(0, rgba(col, gl * 0.9));
                    g.addColorStop(1, rgba(col, 0));
                    c.strokeStyle = g;
                    c.lineWidth = 1.3;
                    c.beginPath();
                    c.moveTo(b[0], b[1]);
                    c.lineTo(t[0], t[1]);
                    c.stroke();
                    c.lineWidth = 1;
                    c.globalCompositeOperation = 'source-over';
                    glow[k] = still ? gl : gl * Math.exp(-dt * 2.6);
                }
            }
        // Controller, lit by busy time.
        const dom = s.writeBps > s.readBps ? o.write : o.read;
        quad(-0.2, 0.03, 0.2, 0.17);
        c.fillStyle = '#151b24';
        c.fill();
        c.strokeStyle = rgba(o.ink, 0.45);
        c.stroke();
        const busy = Math.min(1, (s.busy || 0) / 100);
        if (busy > 0.01 || total > 1024) {
            quad(-0.12, 0.065, 0.12, 0.135);
            c.globalCompositeOperation = 'lighter';
            c.fillStyle = rgba(dom, 0.18 + busy * 0.6);
            c.fill();
            c.globalCompositeOperation = 'source-over';
        }
        // Host link and PC port.
        c.strokeStyle = rgba(o.ink, 0.14 + intensity * 0.1);
        for (const st of G.strands) {
            c.beginPath();
            st.pts.forEach((p, k) => (k ? c.lineTo(p[0], p[1]) : c.moveTo(p[0], p[1])));
            c.stroke();
        }
        c.fillStyle = rgba(o.ink, 0.6);
        c.beginPath();
        c.arc(G.port[0], G.port[1], 4.5, 0, Math.PI * 2);
        c.fill();
        c.font = '10px Segoe UI, sans-serif';
        c.textAlign = 'right';
        c.fillStyle = rgba(o.ink, 0.85);
        c.fillText('PC', G.port[0] - 8, G.port[1] - 8);
        // Queue depth: beads circling the controller on a dotted orbit.
        const ring = (k) => { const a = k / 64 * Math.PI * 2; return P(Math.cos(a) * 0.34, 0.03, 0.1 + Math.sin(a) * 0.12); };
        c.fillStyle = rgba(o.ink, 0.22);
        for (let k = 0; k < 64; k += 2) {
            const p = ring(k);
            c.fillRect(p[0] - 0.6, p[1] - 0.6, 1.2, 1.2);
        }
        const q = Math.min(24, Math.round(s.queueDepth || 0));
        if (!still)
            orbit += dt * (0.5 + intensity * 1.8);
        for (let b = 0; b < q; b++) {
            const p = ring(((orbit / (Math.PI * 2) + b / q) % 1) * 64);
            c.fillStyle = rgba(dom, 0.95);
            c.beginPath();
            c.arc(p[0], p[1], 1.7, 0, Math.PI * 2);
            c.fill();
            c.fillStyle = rgba(dom, 0.2);
            c.beginPath();
            c.arc(p[0], p[1], 4, 0, Math.PI * 2);
            c.fill();
        }
        // Floating motes, drifting faster when the drive is busy.
        for (const m of motes) {
            if (!still) {
                m.y += m.v * dt * (1 + intensity * 2.5);
                m.ph += dt * 0.7;
                if (m.y > 0.62) {
                    m.y = 0;
                    m.x = rand(-1.3, 1.3);
                    m.z = rand(-0.05, 1.2);
                }
            }
            const p = P(m.x + Math.sin(m.ph) * 0.02, m.y, m.z);
            const a = 0.32 * (1 - m.z / 1.4) * Math.sin(Math.PI * m.y / 0.62) * (0.6 + intensity * 0.6);
            c.fillStyle = `rgba(210,220,232,${a})`;
            c.beginPath();
            c.arc(p[0], p[1], m.r * G.scale, 0, Math.PI * 2);
            c.fill();
        }
        // Spawn and draw streaks. Reduced motion: no travel, dies lit by load instead.
        if (!still) {
            acc.read += streakRate(s.readOps) * dt;
            acc.write += streakRate(s.writeOps) * dt;
            while (acc.read >= 1) {
                acc.read--;
                spawn('read');
            }
            while (acc.write >= 1) {
                acc.write--;
                spawn('write');
            }
        }
        else {
            for (let k = 0; k < N; k++) {
                glow[k] = total > 1024 && (k * 7919) % 11 < intensity * 11 ? 0.6 : 0;
                kind[k] = s.writeBps > s.readBps ? 1 : 0;
            }
        }
        c.globalCompositeOperation = 'lighter';
        c.lineCap = 'round';
        for (let n = streaks.length - 1; n >= 0; n--) {
            const k = streaks[n];
            const prev = k.s;
            k.s += k.speed * dt;
            if (prev < k.p.len && k.s >= k.p.len)
                k.arrive?.();
            if (k.s - k.tail > k.p.len) {
                streaks.splice(n, 1);
                continue;
            }
            const head = Math.min(k.s, k.p.len), from = Math.max(0, k.s - k.tail), SEG = 9;
            for (let g = 0; g < SEG; g++) {
                const a = at(k.p, from + (head - from) * g / SEG), b = at(k.p, from + (head - from) * (g + 1) / SEG), f = (g + 1) / SEG;
                if (f > 0.7) {
                    c.strokeStyle = rgba(k.col, 0.1 * f);
                    c.lineWidth = 4 * G.scale * f;
                    c.beginPath();
                    c.moveTo(a[0], a[1]);
                    c.lineTo(b[0], b[1]);
                    c.stroke();
                }
                c.strokeStyle = rgba(k.col, 0.75 * f * f);
                c.lineWidth = 0.6 + 1.2 * f;
                c.beginPath();
                c.moveTo(a[0], a[1]);
                c.lineTo(b[0], b[1]);
                c.stroke();
            }
            if (k.s <= k.p.len) {
                const h = at(k.p, k.s);
                c.fillStyle = rgba(k.col, 0.16);
                c.beginPath();
                c.arc(h[0], h[1], 4 * G.scale, 0, Math.PI * 2);
                c.fill();
                c.fillStyle = 'rgba(255,255,255,0.85)';
                c.beginPath();
                c.arc(h[0], h[1], 1.25 * G.scale, 0, Math.PI * 2);
                c.fill();
            }
        }
        c.globalCompositeOperation = 'source-over';
        c.lineCap = 'butt';
    }
    function destroy() {
        if (destroyed)
            return;
        destroyed = true;
        cancelAnimationFrame(raf);
        resize.disconnect();
        seen?.disconnect();
    }
    layout();
    const resize = new ResizeObserver(layout);
    resize.observe(canvas);
    const seen = typeof IntersectionObserver !== 'undefined'
        ? new IntersectionObserver(entries => { visible = entries[entries.length - 1].isIntersecting; })
        : null;
    seen?.observe(canvas);
    raf = requestAnimationFrame(frame);
    return {
        update(next) { sample = next; },
        setScan(fraction) { scan = fraction; },
        destroy,
    };
}

window.FlashStream = { VERSION, lanesFor, streakRate, streakSpeed, streakTail, createFlashStream };
})();
