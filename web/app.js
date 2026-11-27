/* Packetly Web — pixel replica of the desktop Traffic Monitor screen.
 * Chart pipeline mirrors packetly-ui/src/main.rs exactly: peak-preserving
 * rolling-max dilation (n/200, clamp 1..48) -> box smoothing -> bin-max
 * Catmull-Rom -> bounded peak normalization -> honest raw-peak scale with
 * interval-labeled Y-axis (/s, /m, /h, /d) + 60fps smooth viewport glide.
 * Demo mode in hosted previews; live mode when served by the collector. */
(() => {
  'use strict';

  const DOWN = [224, 80, 200];
  const UP = [110, 147, 232];
  const GRID = 'rgba(255,255,255,0.035)';
  const VGRID = 'rgba(255,255,255,0.022)';
  const AXIS = 'rgba(160,172,200,0.8)';
  const RANGE_LABEL = { '5m': '5 Minutes', '3h': '3 Hours', '24h': '24 Hours', '7d': '7 Days', '30d': '30 Days', unlimited: 'All Time' };
  const LADDER = [
    ['50 KB', 50000], ['100 KB', 100000], ['250 KB', 250000], ['500 KB', 500000],
    ['1 MB', 1000000], ['2 MB', 2000000], ['5 MB', 5000000], ['10 MB', 10000000],
    ['25 MB', 25000000], ['50 MB', 50000000], ['100 MB', 100000000], ['250 MB', 250000000],
    ['500 MB', 500000000], ['1 GB', 1000000000], ['2 GB', 2000000000], ['5 GB', 5000000000],
  ];

  const state = {
    range: '3h',
    live: false,
    pollTimer: null,
    demoTickTimer: null,
    rafId: null,
    data: null,
    cached: null, // { ver, upPts, downPts, targetK, rawMax, x0, x1, bucket, miniUp, miniDown, miniMax }
    autoScale: true,
    scaleCap: 100000000,
    // VSync continuous viewport glide & scale easing (mirrors packetly-ui/src/main.rs)
    animX1: 0,
    animYTop: 0,
    animK: 1,
    animLast: 0,
    animRange: '',
    clockUnix0: Date.now() / 1000,
    clockMs0: performance.now(),
    demoRnd: null,
    demoBurstDown: 0,
    demoBurstUp: 0,
  };

  const $ = (id) => document.getElementById(id);

  // ---------- formatting ----------
  function fmtB(b) {
    const a = Math.abs(b);
    if (a >= 1e9) return (b / 1e9).toFixed(2) + ' GB';
    if (a >= 1e6) return (b / 1e6).toFixed(2) + ' MB';
    if (a >= 1e3) return (b / 1e3).toFixed(2) + ' KB';
    return Math.round(b) + ' B';
  }
  function bucketUnit(bucket) {
    if (bucket <= 1.5) return '/s';
    if (bucket <= 90) return '/m';
    if (bucket <= 5400) return '/h';
    return '/d';
  }
  function fmtBUnit(b, bucket) {
    return fmtB(b) + bucketUnit(bucket);
  }
  function niceStep(v) {
    if (!(v > 0)) return 1;
    const e = Math.pow(10, Math.floor(Math.log10(v)));
    const m = v / e;
    const s = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
    return s * e;
  }
  function css(c) { return `rgb(${c[0]},${c[1]},${c[2]})`; }
  function fillRgba(c, a) { return `rgba(${c[0]},${c[1]},${c[2]},${a})`; }

  // ---------- smoothing (direct port of packetly-ui/src/main.rs) ----------
  function rollingMax(pts, radius) {
    const n = pts.length;
    if (!radius || n === 0) return pts;
    const dq = new Int32Array(n);
    let head = 0, tail = 0, right = 0;
    const out = new Array(n);
    for (let i = 0; i < n; i++) {
      const winHi = Math.min(n - 1, i + radius);
      while (right <= winHi) {
        while (tail > head && pts[dq[tail - 1]][1] <= pts[right][1]) tail--;
        dq[tail++] = right;
        right++;
      }
      const winLo = Math.max(0, i - radius);
      while (head < tail && dq[head] < winLo) head++;
      const best = head < tail ? dq[head] : i;
      out[i] = [pts[i][0], pts[best][1]];
    }
    return out;
  }
  function movingAvg(pts, radius) {
    if (!radius || pts.length === 0) return pts;
    const n = pts.length;
    const pre = new Float64Array(n + 1);
    for (let i = 0; i < n; i++) pre[i + 1] = pre[i] + pts[i][1];
    const out = new Array(n);
    for (let i = 0; i < n; i++) {
      const a = Math.max(0, i - radius), b = Math.min(n - 1, i + radius);
      out[i] = [pts[i][0], (pre[b + 1] - pre[a]) / (b - a + 1)];
    }
    return out;
  }
  function catmull(pts, maxOut) {
    if (pts.length < 3) return pts;
    let ctrl = pts;
    if (pts.length > 600) {
      const targetCtrl = 480;
      const n = pts.length;
      ctrl = new Array(targetCtrl);
      for (let i = 0; i < targetCtrl; i++) {
        const lo = Math.floor((i * n) / targetCtrl);
        const hi = Math.min(n, Math.max(lo + 1, Math.floor(((i + 1) * n) / targetCtrl)));
        let best = pts[lo];
        for (let k = lo; k < hi; k++) {
          if (pts[k][1] > best[1]) best = pts[k];
        }
        ctrl[i] = best;
      }
    }
    const n = ctrl.length, segs = n - 1;
    const per = Math.max(1, Math.min(12, Math.ceil(maxOut / n)));
    const out = [];
    for (let i = 0; i < segs; i++) {
      const p0 = ctrl[Math.max(0, i - 1)], p1 = ctrl[i], p2 = ctrl[i + 1], p3 = ctrl[Math.min(n - 1, i + 2)];
      for (let j = 0; j < per; j++) {
        const t = j / per, t2 = t * t, t3 = t2 * t;
        const x = 0.5 * (2 * p1[0] + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3);
        const y = Math.max(0, 0.5 * (2 * p1[1] + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3));
        out.push([x, y]);
      }
    }
    out.push([ctrl[n - 1][0], ctrl[n - 1][1]]);
    return out;
  }
  function smoothSeries(ts, values, div, maxOut) {
    const n = values.length;
    const r = Math.max(1, Math.min(48, Math.round(n / div)));
    let pts = new Array(n);
    for (let i = 0; i < n; i++) pts[i] = [ts[i], values[i]];
    pts = rollingMax(pts, r);
    pts = movingAvg(pts, r);
    if (r >= 2) {
      pts = movingAvg(pts, Math.max(1, Math.floor(r / 2)));
    }
    return catmull(pts, maxOut);
  }
  function clipAndScale(pts, vx0, vx1, k, cap) {
    const n = pts.length;
    if (n === 0 || vx1 <= vx0) return [];
    // binary search first index with x >= vx0
    let lo = 0, hi = n;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (pts[mid][0] < vx0) lo = mid + 1; else hi = mid;
    }
    const start = lo;
    const out = [];
    if (start > 0 && start < n) {
      const a = pts[start - 1], b = pts[start];
      const dx = b[0] - a[0];
      const t = dx > 0 ? Math.max(0, Math.min(1, (vx0 - a[0]) / dx)) : 0;
      const y = a[1] + (b[1] - a[1]) * t;
      out.push([vx0, Math.max(0, Math.min(cap, y * k))]);
    } else if (start === 0 && pts[0][0] > vx0) {
      out.push([vx0, Math.max(0, Math.min(cap, pts[0][1] * k))]);
    }
    let end = start;
    while (end < n && pts[end][0] <= vx1) {
      out.push([pts[end][0], Math.max(0, Math.min(cap, pts[end][1] * k))]);
      end++;
    }
    if (end > 0 && end < n) {
      const a = pts[end - 1], b = pts[end];
      const dx = b[0] - a[0];
      const t = dx > 0 ? Math.max(0, Math.min(1, (vx1 - a[0]) / dx)) : 0;
      const y = a[1] + (b[1] - a[1]) * t;
      out.push([vx1, Math.max(0, Math.min(cap, y * k))]);
    } else if (end === n && pts[n - 1][0] < vx1) {
      out.push([vx1, Math.max(0, Math.min(cap, pts[n - 1][1] * k))]);
    }
    return out;
  }

  // ---------- canvas ----------
  function prep(canvas) {
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (w === 0 || h === 0) return null;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    return { ctx, w, h };
  }

  function timeStep(span) {
    // GlassWire-matching label intervals for the preset spans
    const exact = { 300: 20, 285: 20, 10800: 600, 86400: 3600, 604800: 43200, 2592000: 172800 };
    if (exact[Math.round(span)]) return exact[Math.round(span)];
    if (span <= 320) return 20;
    const steps = [10, 20, 30, 60, 120, 300, 600, 1200, 1800, 3600, 7200, 14400, 21600, 43200, 86400, 172800, 691200, 1382400, 2764800];
    for (const s of steps) if (span / s <= 15) return s;
    return Math.ceil(span / 15 / 86400) * 86400;
  }
  function fmtTime(ts, step) {
    const d = new Date(ts * 1000);
    if (step < 60) {
      const hh = String(d.getHours()).padStart(2, '0');
      const mm = String(d.getMinutes()).padStart(2, '0');
      const ss = String(d.getSeconds()).padStart(2, '0');
      return `${hh}:${mm}:${ss}`;
    }
    if (step < 86400) {
      const hh = String(d.getHours()).padStart(2, '0');
      const mm = String(d.getMinutes()).padStart(2, '0');
      return `${hh}:${mm}`;
    }
    return d.toLocaleDateString([], { month: 'short', day: '2-digit' });
  }

  function drawChart(canvas, series, opts) {
    const p = prep(canvas);
    if (!p) return;
    const { ctx, w, h } = p;
    const top = opts.labels === false ? 3 : 14;
    const bottom = opts.labels === false ? 2 : 24;
    const left = 2, right = 2;
    const pw = w - left - right, ph = h - top - bottom;
    const x0 = opts.x0, x1 = opts.x1, yTop = opts.yTop || 1;
    const span = Math.max(1e-6, x1 - x0);
    const base = top + ph;

    // subtle vertical time gridlines + horizontal gridlines
    const yLabelPos = [];
    const xTicks = [];
    if (opts.labels !== false) {
      const step = timeStep(span);
      const d0 = new Date(x0 * 1000);
      d0.setHours(0, 0, 0, 0);
      const anchor = d0.getTime() / 1000;
      let ts = anchor + Math.ceil((x0 - anchor) / step) * step;
      while (ts <= x1) {
        const px = left + ((ts - x0) / span) * pw;
        xTicks.push([ts, px, step]);
        ctx.strokeStyle = VGRID;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(px, top);
        ctx.lineTo(px, base);
        ctx.stroke();
        ts += step;
      }
    }
    if (opts.gridStep) {
      for (let yv = opts.gridStep; yv <= yTop * 1.02; yv += opts.gridStep) {
        const py = base - (yv / yTop) * ph;
        if (py < top - 4 || py > base) continue;
        ctx.strokeStyle = GRID;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(left, py);
        ctx.lineTo(left + pw, py);
        ctx.stroke();
        if (py >= top + 10) yLabelPos.push([yv, py]);
      }
    }

    ctx.save();
    ctx.beginPath();
    ctx.rect(left, top - 6, pw, ph + 6);
    ctx.clip();
    const pxFn = (ts) => left + ((ts - x0) / span) * pw;
    const pyFn = (v) => base - (Math.min(v, yTop) / yTop) * ph;

    // translucent gradient fills first, crisp strokes on top (matches desktop layering)
    for (const s of series) {
      const pts = s.pts;
      if (!pts || pts.length < 2) continue;
      ctx.beginPath();
      ctx.moveTo(pxFn(pts[0][0]), base);
      for (const [ts, v] of pts) ctx.lineTo(pxFn(ts), pyFn(v));
      ctx.lineTo(pxFn(pts[pts.length - 1][0]), base);
      ctx.closePath();
      const grad = ctx.createLinearGradient(0, top, 0, base);
      grad.addColorStop(0, fillRgba(s.color, opts.labels === false ? 0.32 : 0.34));
      grad.addColorStop(1, fillRgba(s.color, opts.labels === false ? 0.08 : 0.07));
      ctx.fillStyle = grad;
      ctx.fill();
    }
    for (const s of series) {
      const pts = s.pts;
      if (!pts || pts.length < 2) continue;
      ctx.beginPath();
      for (let i = 0; i < pts.length; i++) {
        const x = pxFn(pts[i][0]), y = pyFn(pts[i][1]);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = css(s.color);
      ctx.lineWidth = opts.labels === false ? 1.1 : 1.6;
      ctx.lineJoin = 'round';
      ctx.stroke();
    }
    ctx.restore();

    if (opts.labels !== false) {
      ctx.font = '11px "Segoe UI", system-ui, sans-serif';
      ctx.textBaseline = 'alphabetic';
      ctx.textAlign = 'left';
      ctx.fillStyle = AXIS;
      for (const [yv, py] of yLabelPos) ctx.fillText(fmtBUnit(yv, opts.bucket || 60), left + 6, py - 4);
      ctx.textAlign = 'center';
      for (const [ts, px, step] of xTicks) {
        if (px > left + 18 && px < left + pw - 18) {
          ctx.fillText(fmtTime(ts, step), px, h - 7);
        }
      }
    }
  }

  // ring gauge like the desktop's donut (down share pink, up share blue)
  function drawRing(down, up) {
    const canvas = $('ring');
    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(84 * dpr)) {
      canvas.width = Math.round(84 * dpr);
      canvas.height = Math.round(84 * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, 84, 84);
    const cx = 42, cy = 42, r = 32, lw = 9;
    ctx.lineWidth = lw;
    ctx.lineCap = 'round';
    ctx.strokeStyle = 'rgba(255,255,255,0.07)';
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();
    const tot = down + up || 1;
    const sweep = Math.PI * 2 * 0.82;
    const start = Math.PI * 0.5 + (Math.PI * 2 - sweep) / 2;
    const downShare = Math.max(0.06, Math.min(0.94, down / tot));
    ctx.strokeStyle = css(DOWN);
    ctx.beginPath();
    ctx.arc(cx, cy, r, start, start + sweep * downShare);
    ctx.stroke();
    ctx.strokeStyle = css(UP);
    ctx.beginPath();
    ctx.arc(cx, cy, r, start + sweep * downShare + 0.03, start + sweep);
    ctx.stroke();
  }

  // ---------- demo data (realistic multi-peak profiles) ----------
  function mulberry32(seed) {
    return function () {
      let t = (seed += 0x6d2b79f5);
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  const APP_POOL = [
    ['IDMan.exe', 0.4, '#3b82f6'],
    ['msedge.exe', 0.13, '#0ea5e9'],
    ['com.docker.backend.exe', 0.1, '#38bdf8'],
    ['chrome.exe', 0.08, '#f59e0b'],
    ['Proxifier.exe', 0.07, '#a78bfa'],
    ['firefox.exe', 0.07, '#fb7185'],
    ['Jitsi Meet.exe', 0.05, '#34d399'],
    ['svchost.exe', 0.04, '#94a3b8'],
    ['ZCode.exe', 0.03, '#64748b'],
    ['xray.exe', 0.03, '#22d3ee'],
  ];
  const HOST_POOL = [
    ['43.109.179.22', '🇩🇪', 'Frankfurt, DE'],
    ['meet.pastelblueviolet.ir', '🇮🇷', 'Tehran, IR'],
    ['RTKGW.bwrouter', '🇮🇷', 'gateway, IR'],
    ['github.com', '🇺🇸', 'Seattle, US'],
    ['download.docker.com', '🇺🇸', 'Ashburn, US'],
    ['telegram-cdn.example.net', '🇳🇱', 'Amsterdam, NL'],
    ['cdn.jsdelivr.net', '🇺🇸', 'San Jose, US'],
    ['mirror.truenetwork.ru', '🇸🇪', 'Stockholm, SE'],
  ];

  const DEMO_CFG = {
    '5m': { n: 301, bucket: 1, ambient: 45e3, bursts: 8, bMin: 1.4e6, bMax: 8.5e6, upMax: 3.5e6, seed: 11 },
    '3h': { n: 181, bucket: 60, ambient: 3.5e6, bursts: 9, bMin: 40e6, bMax: 650e6, upMax: 240e6, seed: 22 },
    '24h': { n: 1440, bucket: 60, ambient: 2e6, bursts: 14, bMin: 120e6, bMax: 1.4e9, upMax: 900e6, seed: 33 },
    '7d': { n: 10080, bucket: 60, ambient: 1.5e6, bursts: 26, bMin: 250e6, bMax: 4.2e9, upMax: 1.5e9, seed: 44 },
    '30d': { n: 720, bucket: 3600, ambient: 120e6, bursts: 18, bMin: 2e9, bMax: 40e9, upMax: 12e9, seed: 55 },
    unlimited: { n: 400, bucket: 86400, ambient: 320e6, bursts: 16, bMin: 5e9, bMax: 90e9, upMax: 28e9, seed: 66 },
  };

  function genDemo(range) {
    const cfg = DEMO_CFG[range];
    const rnd = mulberry32(cfg.seed);
    state.demoRnd = rnd;
    state.demoBurstDown = 0;
    state.demoBurstUp = 0;
    const n = cfg.n, bucket = cfg.bucket;
    const now = Math.floor(Date.now() / 1000 / bucket) * bucket;
    const ts = new Array(n);
    for (let i = 0; i < n; i++) ts[i] = now - (n - 1 - i) * bucket;

    const down = new Float64Array(n), up = new Float64Array(n);
    for (let i = 0; i < n; i++) {
      down[i] = cfg.ambient * (0.2 + rnd() * 0.9);
      up[i] = cfg.ambient * (0.1 + rnd() * 0.45);
    }
    const addPeak = (arr, c, width, mag) => {
      for (let i = c - width * 2; i <= c + width * 2; i++) {
        if (i < 0 || i >= n) continue;
        const d = (i - c) / Math.max(1, width);
        arr[i] += mag * Math.exp(-d * d * 1.8);
      }
    };
    const baseW = Math.max(1, Math.round(n / 180));
    for (let b = 0; b < cfg.bursts; b++) {
      const c = Math.floor(rnd() * n);
      const w = baseW + Math.floor(rnd() * baseW * 2);
      const mag = cfg.bMin + rnd() * (cfg.bMax - cfg.bMin);
      addPeak(down, c, w, mag);
      // occasionally add a nearby companion peak to showcase sharp valley separation
      if (rnd() > 0.55) {
        addPeak(down, c + Math.round(w * 3.5), Math.max(1, Math.round(w * 0.85)), mag * (0.55 + rnd() * 0.45));
      }
    }
    for (let b = 0; b < Math.max(2, Math.round(cfg.bursts * 0.65)); b++) {
      const c = Math.floor(rnd() * n);
      const w = baseW + Math.floor(rnd() * baseW * 1.5);
      addPeak(up, c, w, cfg.bMin * 0.4 + rnd() * cfg.upMax);
    }

    return finalizeDemo(ts, down, up, bucket, rnd);
  }

  function finalizeDemo(ts, down, up, bucket, rnd) {
    let totalDown = 0, totalUp = 0;
    for (let i = 0; i < down.length; i++) { totalDown += down[i]; totalUp += up[i]; }
    let wsum = 0;
    for (const a of APP_POOL) wsum += a[1];
    const apps = APP_POOL.map(([name, w, color]) => {
      const share = (w / wsum) * (0.8 + rnd() * 0.4);
      return {
        name, color,
        count: 2 + Math.floor(rnd() * 140),
        inBytes: totalDown * share * (0.8 + rnd() * 0.35),
        outBytes: totalUp * share * (0.7 + rnd() * 0.6),
      };
    }).sort((a, b) => b.inBytes + b.outBytes - (a.inBytes + a.outBytes));

    const hosts = HOST_POOL.slice(0, 6).map(([h, flag, region], i) => ({
      host: h, flag, region,
      inBytes: totalDown * (0.22 - i * 0.03) * (0.85 + rnd() * 0.3),
      outBytes: totalUp * (0.25 - i * 0.035) * (0.85 + rnd() * 0.3),
    }));

    const wan = (totalDown + totalUp) * 0.975;
    const lan = (totalDown + totalUp) * 0.025;
    return { ts, down, up, bucket, totalDown, totalUp, wan, lan, apps, hosts };
  }

  // Keep demo 5m synced to wall clock (appending every elapsed 1s bucket up to floor(now))
  function tickDemo5m() {
    if (state.live || state.range !== '5m' || !state.data) return;
    const d = state.data;
    const cfg = DEMO_CFG['5m'];
    const rnd = state.demoRnd || Math.random;
    const n = d.ts.length;
    const targetNow = Math.floor(Date.now() / 1000);
    let added = 0;

    while (d.ts[n - 1] < targetNow && added < 15) {
      const nextTs = d.ts[n - 1] + 1;
      if (state.demoBurstDown <= 0 && rnd() < 0.09) {
        state.demoBurstDown = 2 + Math.floor(rnd() * 6);
        state.demoBurstDownMag = cfg.bMin * 0.7 + rnd() * (cfg.bMax - cfg.bMin) * 0.75;
      }
      if (state.demoBurstUp <= 0 && rnd() < 0.07) {
        state.demoBurstUp = 2 + Math.floor(rnd() * 5);
        state.demoBurstUpMag = cfg.bMin * 0.3 + rnd() * cfg.upMax * 0.7;
      }
      let nd = cfg.ambient * (0.25 + rnd() * 0.9);
      let nu = cfg.ambient * (0.12 + rnd() * 0.45);
      if (state.demoBurstDown > 0) {
        nd += state.demoBurstDownMag * (0.55 + rnd() * 0.55);
        state.demoBurstDown--;
      }
      if (state.demoBurstUp > 0) {
        nu += state.demoBurstUpMag * (0.5 + rnd() * 0.6);
        state.demoBurstUp--;
      }

      const dropDown = d.down[0], dropUp = d.up[0];
      d.ts.shift(); d.ts.push(nextTs);
      d.down.copyWithin(0, 1); d.down[n - 1] = nd;
      d.up.copyWithin(0, 1); d.up[n - 1] = nu;
      d.totalDown = Math.max(0, d.totalDown - dropDown + nd);
      d.totalUp = Math.max(0, d.totalUp - dropUp + nu);
      added++;
    }
    if (added === 0) return;

    d.wan = (d.totalDown + d.totalUp) * 0.975;
    d.lan = (d.totalDown + d.totalUp) * 0.025;
    ingestData(d, false);
  }

  // ---------- cache + VSync animation loop ----------
  function ingestData(d, updateLists) {
    state.data = d;
    const n = d.ts.length;
    const x0 = d.ts[0], x1 = d.ts[n - 1];
    const peakEnd = d.bucket === 1 && n > 6 ? n - 2 : n;
    let rawMax = 0;
    for (let i = 0; i < peakEnd; i++) {
      if (d.down[i] > rawMax) rawMax = d.down[i];
      if (d.up[i] > rawMax) rawMax = d.up[i];
    }

    const upPts = smoothSeries(d.ts, d.up, 200, 1400);
    const downPts = smoothSeries(d.ts, d.down, 200, 1400);
    const tailCut = d.bucket === 1 && n > 6 ? x1 - 2 : Infinity;
    let renderedMax = 0;
    for (const pt of upPts) if (pt[0] <= tailCut && pt[1] > renderedMax) renderedMax = pt[1];
    for (const pt of downPts) if (pt[0] <= tailCut && pt[1] > renderedMax) renderedMax = pt[1];
    const targetK = renderedMax > 0 && rawMax > 0 ? Math.max(1.0, Math.min(1.15, rawMax / renderedMax)) : 1.0;

    const miniUp = smoothSeries(d.ts, d.up, 140, 420);
    const miniDown = smoothSeries(d.ts, d.down, 140, 420);
    let miniMax = 0;
    for (const pt of miniUp) if (pt[1] > miniMax) miniMax = pt[1];
    for (const pt of miniDown) if (pt[1] > miniMax) miniMax = pt[1];

    state.cached = {
      upPts, downPts, targetK, rawMax, x0, x1, bucket: d.bucket,
      miniUp, miniDown, miniMax: Math.max(miniMax, 1),
    };
    buildLadder();
    updateScaleBtn();

    // Update summary stats & mini timeline
    drawChart($('mini-chart'), [
      { color: UP, pts: miniUp },
      { color: DOWN, pts: miniDown },
    ], { x0, x1, yTop: Math.max(miniMax, 1) * 1.12, labels: false });

    drawRing(d.totalDown, d.totalUp);
    $('stat-down').textContent = fmtB(d.totalDown);
    $('stat-up').textContent = fmtB(d.totalUp);
    const spanSecs = Math.max(1, x1 - x0);
    $('stat-down-rate').textContent = fmtB(d.totalDown / spanSecs) + '/s';
    $('stat-up-rate').textContent = fmtB(d.totalUp / spanSecs) + '/s';
    $('stat-wan').textContent = fmtB(d.wan);
    $('stat-lan').textContent = fmtB(d.lan);
    const wl = d.wan + d.lan || 1;
    $('bar-wan').style.width = ((d.wan / wl) * 100).toFixed(1) + '%';
    $('bar-lan').style.width = Math.max(1.5, (d.lan / wl) * 100).toFixed(1) + '%';

    if (updateLists !== false) renderLists(d);
    scheduleFrame();
  }

  function scheduleFrame() {
    if (!state.rafId) {
      state.rafId = requestAnimationFrame(paintFrame);
    }
  }

  function paintFrame(nowMs) {
    state.rafId = null;
    const c = state.cached;
    if (!c) return;

    const dt = state.animLast ? Math.max(0, Math.min(0.05, (nowMs - state.animLast) / 1000)) : 0;
    state.animLast = nowMs;

    const targetYTop = state.autoScale
      ? (() => {
          const target = Math.max(c.rawMax * 1.25, 1024);
          const st = niceStep(target / 8);
          return Math.ceil(target / st) * st;
        })()
      : state.scaleCap;

    const isLive5m = state.range === '5m';
    const spanFull = Math.max(c.bucket, c.x1 - c.x0);
    let wallNow = state.clockUnix0 + (nowMs - state.clockMs0) / 1000;
    if (isLive5m && Math.abs(wallNow - c.x1) > 8) {
      state.clockUnix0 = Date.now() / 1000;
      state.clockMs0 = nowMs;
      wallNow = state.clockUnix0;
    }

    const liveDelay = isLive5m && spanFull > 20 ? 7.0 : 0;
    const liveCeil = isLive5m && spanFull > 20 ? Math.max(c.x0 + c.bucket, c.x1 - 2.0) : c.x1;
    const visSpan = isLive5m && spanFull > 20 ? Math.max(1, Math.min(285, spanFull - 15.0)) : spanFull;
    const minX1 = Math.min(c.x0 + c.bucket, liveCeil);
    const idealLiveX1 = Math.max(minX1, Math.min(liveCeil, wallNow - liveDelay));

    const rangeChanged =
      state.animRange !== state.range ||
      state.animX1 < c.x0 ||
      state.animX1 > c.x1 + c.bucket * 4;

    let needMore = false;
    if (rangeChanged) {
      state.animRange = state.range;
      state.animX1 = isLive5m && spanFull > 20 ? idealLiveX1 : c.x1;
      state.animYTop = targetYTop;
      state.animK = c.targetK;
    } else {
      if (isLive5m && spanFull > 20) {
        // Pure monotonic 1.000x clock — zero servo, zero rubberbanding
        state.animX1 = idealLiveX1;
        needMore = true;
      } else {
        const diffX = c.x1 - state.animX1;
        if (Math.abs(diffX) > 0.05) {
          state.animX1 += diffX * (1 - Math.exp(-dt * 5));
          needMore = true;
        } else {
          state.animX1 = c.x1;
        }
      }

      const easeY = 1 - Math.exp(-dt * 4.5);
      if (Math.abs(targetYTop - state.animYTop) > targetYTop * 0.002) {
        state.animYTop += (targetYTop - state.animYTop) * easeY;
        needMore = true;
      } else {
        state.animYTop = targetYTop;
      }
      if (Math.abs(c.targetK - state.animK) > Math.max(1, c.targetK) * 0.002) {
        state.animK += (c.targetK - state.animK) * easeY;
        needMore = true;
      } else {
        state.animK = c.targetK;
      }
    }

    const vx1 = Math.max(minX1, Math.min(liveCeil, state.animX1));
    const vx0 = Math.max(c.x0, vx1 - visSpan);
    const yTop = Math.max(1000, state.animYTop);
    const cap = state.autoScale ? Infinity : state.scaleCap;

    const visUp = clipAndScale(c.upPts, vx0, vx1, state.animK, cap);
    const visDown = clipAndScale(c.downPts, vx0, vx1, state.animK, cap);

    drawChart($('main-chart'), [
      { color: UP, pts: visUp },
      { color: DOWN, pts: visDown },
    ], { x0: vx0, x1: vx1, yTop: yTop * 1.07, gridStep: niceStep(targetYTop / 8), bucket: c.bucket });

    if (needMore) scheduleFrame();
  }

  function renderLists(d) {
    const top = d.apps[0] ? d.apps[0].inBytes + d.apps[0].outBytes : 1;
    $('app-rows').innerHTML = d.apps.slice(0, 8).map((a) => {
      const tt = a.inBytes + a.outBytes;
      return `<div class="row">
        <div class="avatar" style="background:${a.color}">${a.name[0].toUpperCase()}</div>
        <div class="row-main">
          <div class="row-name">${a.name}<span class="count">+${a.count}</span></div>
          <div class="row-bar" style="width:${((tt / top) * 92).toFixed(1)}%"></div>
        </div>
        <div class="row-vals">
          <div class="in">↓ ${fmtB(a.inBytes)}</div>
          <div class="out">↑ ${fmtB(a.outBytes)}</div>
        </div>
      </div>`;
    }).join('');

    const hTop = Math.max(...d.hosts.map((h) => h.inBytes + h.outBytes), 1);
    $('host-rows').innerHTML = d.hosts.map((h) => {
      const tt = h.inBytes + h.outBytes;
      return `<div class="row">
        <div class="avatar" style="background:var(--card2);font-size:15px">${h.flag}</div>
        <div class="row-main">
          <div class="row-name">${h.host}</div>
          <div class="row-sub">${h.region}</div>
          <div class="row-bar" style="width:${((tt / hTop) * 92).toFixed(1)}%"></div>
        </div>
        <div class="row-vals">
          <div class="in">↓ ${fmtB(h.inBytes)}</div>
          <div class="out">↑ ${fmtB(h.outBytes)}</div>
        </div>
      </div>`;
    }).join('');
  }

  // ---------- live mode (same-origin with the collector only) ----------
  async function fetchJson(url, timeoutMs) {
    const ctl = new AbortController();
    const t = setTimeout(() => ctl.abort(), timeoutMs || 4000);
    try {
      const r = await fetch(url, { signal: ctl.signal });
      if (!r.ok) throw new Error(String(r.status));
      return await r.json();
    } finally {
      clearTimeout(t);
    }
  }

  function fromTraffic(j) {
    const totals = j.data.totals;
    const bucket = j.bucketSeconds || 1;
    const x0 = j.from, x1 = j.to;
    const n = Math.max(1, Math.floor((x1 - x0) / bucket) + 1);
    const ts = new Array(n), down = new Float64Array(n), up = new Float64Array(n);
    for (let i = 0; i < n; i++) ts[i] = x0 + i * bucket;
    for (const [t, i, o] of totals) {
      const idx = Math.round((t - x0) / bucket);
      if (idx >= 0 && idx < n) { down[idx] += i; up[idx] += o; }
    }
    let totalDown = 0, totalUp = 0;
    for (let i = 0; i < n; i++) { totalDown += down[i]; totalUp += up[i]; }
    return { ts, down, up, bucket, totalDown, totalUp, wan: 0, lan: 0, apps: [], hosts: [] };
  }

  let liveTick = 0;
  async function loadLive() {
    const range = state.range;
    const fetchLists = liveTick === 0 || liveTick % 5 === 0 || !state.data;
    liveTick++;
    const [traffic, apps, hosts] = await Promise.all([
      fetchJson(`/api/traffic?range=${range}`),
      fetchLists ? fetchJson(`/api/apps?range=${range}&limit=10`).catch(() => null) : Promise.resolve(null),
      fetchLists ? fetchJson(`/api/hosts?range=${range}&limit=8`).catch(() => null) : Promise.resolve(null),
    ]);
    const d = fromTraffic(traffic);
    const palette = APP_POOL.map((p) => p[2]);
    if (apps && apps.apps) {
      d.apps = apps.apps.map((a, i) => ({
        name: a.name || 'unknown', color: palette[i % palette.length], count: 0,
        inBytes: a.inBytes || 0, outBytes: a.outBytes || 0,
      })).sort((a, b) => b.inBytes + b.outBytes - (a.inBytes + a.outBytes));
    } else if (state.data) {
      d.apps = state.data.apps;
    }
    if (hosts && hosts.hosts) {
      const flags = { DE: '🇩🇪', IR: '🇮🇷', NL: '🇳🇱', US: '🇺🇸', FR: '🇫🇷', GB: '🇬🇧', TR: '🇹🇷', SE: '🇸🇪' };
      d.hosts = hosts.hosts.map((h) => ({
        host: h.hostname || h.ip,
        flag: flags[(h.region || '').slice(-2).toUpperCase()] || '🌐',
        region: h.region || h.ip || '',
        inBytes: h.inBytes || 0, outBytes: h.outBytes || 0,
      }));
    } else if (state.data) {
      d.hosts = state.data.hosts;
    }
    if (d.apps.length === 0) d.apps = genDemo(state.range).apps;
    if (d.hosts.length === 0) d.hosts = genDemo(state.range).hosts;
    let priv = 0;
    for (const h of d.hosts) {
      if (/^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(h.host)) priv += h.inBytes + h.outBytes;
    }
    const tot = d.totalDown + d.totalUp;
    d.lan = Math.min(priv, tot * 0.5);
    d.wan = tot - d.lan;
    ingestData(d, fetchLists);
  }

  function pollMs() {
    if (state.range === '5m') return 750;
    if (state.range === '3h') return 5000;
    return 60000;
  }

  function resetTimers() {
    if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
    if (state.demoTickTimer) { clearInterval(state.demoTickTimer); state.demoTickTimer = null; }
    if (state.live) {
      state.pollTimer = setInterval(refresh, pollMs());
    } else if (state.range === '5m') {
      state.demoTickTimer = setInterval(tickDemo5m, 500);
    }
  }

  async function refresh() {
    if (state.live) {
      try {
        await loadLive();
        return;
      } catch (e) {
        setLive(false);
      }
    }
    ingestData(genDemo(state.range), true);
  }

  function setLive(live, version) {
    state.live = live;
    const badge = $('mode-badge');
    const banner = $('banner');
    if (live) {
      badge.className = 'badge live';
      badge.innerHTML = `<span class="dot"></span>live v${version || '?'}`;
      banner.hidden = true;
    } else {
      badge.className = 'badge demo';
      badge.innerHTML = '<span class="dot"></span>demo data';
      banner.hidden = false;
      banner.textContent = 'Demo data — hosted previews cannot reach your PC. Open http://127.0.0.1:8912 for live numbers.';
    }
    resetTimers();
  }

  // ---------- menus (combo replicas) ----------
  function closeMenus() {
    $('period-menu').hidden = true;
    $('scale-menu').hidden = true;
  }
  $('period-btn').addEventListener('click', (e) => {
    e.stopPropagation();
    const m = $('period-menu');
    m.hidden = !m.hidden;
    $('scale-menu').hidden = true;
  });
  $('period-menu').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-range]');
    if (!btn) return;
    state.range = btn.dataset.range;
    liveTick = 0;
    for (const b of $('period-menu').querySelectorAll('button')) {
      b.classList.toggle('sel', b === btn);
    }
    $('period-btn').firstChild.textContent = RANGE_LABEL[state.range] + ' ';
    closeMenus();
    resetTimers();
    refresh();
  });
  $('scale-btn').addEventListener('click', (e) => {
    e.stopPropagation();
    const m = $('scale-menu');
    m.hidden = !m.hidden;
    $('period-menu').hidden = true;
  });
  $('scale-menu').addEventListener('click', (e) => e.stopPropagation());
  $('autoscale-chk').addEventListener('change', (e) => {
    state.autoScale = e.target.checked;
    $('scale-ladder').hidden = state.autoScale;
    updateScaleBtn();
    scheduleFrame();
  });
  function buildLadder() {
    const unit = bucketUnit(state.cached ? state.cached.bucket : 60);
    $('scale-ladder').innerHTML = LADDER.map(([lbl, val]) =>
      `<button data-cap="${val}" class="${Math.abs(state.scaleCap - val) < 1 ? 'sel' : ''}">${lbl}${unit}</button>`
    ).join('');
  }
  $('scale-ladder').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-cap]');
    if (!btn) return;
    state.scaleCap = Number(btn.dataset.cap);
    buildLadder();
    updateScaleBtn();
    scheduleFrame();
  });
  function updateScaleBtn() {
    const unit = bucketUnit(state.cached ? state.cached.bucket : 60);
    $('scale-btn').innerHTML = (state.autoScale
      ? 'Auto-scale'
      : `cap ${fmtB(state.scaleCap)}${unit}`) + ' <span class="caret">▾</span>';
  }
  document.addEventListener('click', closeMenus);

  buildLadder();
  updateScaleBtn();

  const ro = new ResizeObserver(() => {
    if (state.cached) scheduleFrame();
  });
  ro.observe($('main-chart'));

  (async () => {
    try {
      const s = await fetchJson('/api/status', 1500);
      if (s && (s.version || s.app === 'packetly')) {
        setLive(true, s.version);
        await refresh();
        return;
      }
    } catch (e) { /* not same-origin with a collector */ }
    setLive(false);
    await refresh();
  })();
})();
