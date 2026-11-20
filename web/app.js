/* Packetly Web — pixel replica of the desktop Traffic Monitor screen.
 * Chart pipeline mirrors packetly-ui/src/main.rs exactly: 3-pass box
 * smoothing -> Catmull-Rom -> peak normalization -> honest raw-peak scale.
 * Demo mode in hosted previews; live mode when served by the collector. */
(() => {
  'use strict';

  const DOWN = [224, 80, 200];
  const UP = [110, 147, 232];
  const GRID = 'rgba(255,255,255,0.035)';
  const AXIS = 'rgba(160,172,200,0.8)';
  const RANGE_LABEL = { '5m': '5 Minutes', '3h': '3 Hours', '24h': '24 Hours', '7d': '7 Days', '30d': '30 Days', unlimited: 'All Time' };
  const LADDER = [
    ['50 KB', 51200], ['100 KB', 102400], ['250 KB', 256000], ['500 KB', 512000],
    ['1 MB', 1048576], ['2 MB', 2097152], ['5 MB', 5242880], ['10 MB', 10485760],
    ['25 MB', 26214400], ['50 MB', 52428800], ['100 MB', 104857600], ['250 MB', 262144000],
    ['500 MB', 524288000], ['1 GB', 1073741824], ['2 GB', 2147483648], ['5 GB', 5368709120],
  ];

  const state = { range: '3h', live: false, timer: null, demo: null, autoScale: true, scaleCap: 104857600 };

  const $ = (id) => document.getElementById(id);

  // ---------- formatting ----------
  function fmtB(b) {
    const a = Math.abs(b);
    if (a >= 1e9) return (b / 1e9).toFixed(2) + ' GB';
    if (a >= 1e6) return (b / 1e6).toFixed(2) + ' MB';
    if (a >= 1e3) return (b / 1e3).toFixed(2) + ' KB';
    return Math.round(b) + ' B';
  }
  function niceStep(v) {
    if (!(v > 0)) return 1;
    const e = Math.pow(10, Math.floor(Math.log10(v)));
    const m = v / e;
    const s = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
    return s * e;
  }
  function css(c) { return `rgb(${c[0]},${c[1]},${c[2]})`; }
  function dim(c) {
    return `rgb(${Math.round(c[0] * 0.42)},${Math.round(c[1] * 0.42)},${Math.round(c[2] * 0.42)})`;
  }

  // ---------- smoothing (ported from packetly-ui/src/main.rs) ----------
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
  function catmull(pts, target) {
    if (pts.length < 3) return pts;
    const n = pts.length, segs = n - 1;
    const per = Math.max(2, Math.round(target / segs));
    const out = [];
    for (let i = 0; i < segs; i++) {
      const p0 = pts[Math.max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.min(n - 1, i + 2)];
      for (let j = 0; j < per; j++) {
        const t = j / per, t2 = t * t, t3 = t2 * t;
        out.push([
          0.5 * (2 * p1[0] + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3),
          0.5 * (2 * p1[1] + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3),
        ]);
      }
    }
    out.push([pts[n - 1][0], pts[n - 1][1]]);
    return out;
  }
  function pipeline(ts, values, target) {
    const n = values.length;
    let rawMax = 0;
    for (const v of values) if (v > rawMax) rawMax = v;
    const r = Math.max(2, Math.min(400, Math.round(n / 20)));
    let pts = values.map((v, i) => [ts[i], v]);
    pts = movingAvg(pts, r);
    pts = movingAvg(pts, Math.max(1, Math.round((r * 2) / 3)));
    pts = movingAvg(pts, Math.max(1, Math.round(r / 3)));
    pts = catmull(pts, target);
    let renderedMax = 0;
    for (const p of pts) if (p[1] > renderedMax) renderedMax = p[1];
    if (renderedMax > 0 && rawMax > 0) {
      const k = rawMax / renderedMax;
      pts = pts.map((p) => [p[0], p[1] * k]);
    }
    return { pts, rawMax };
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
    const exact = { 300: 20, 10800: 600, 86400: 3600, 604800: 43200, 2592000: 172800 };
    if (exact[span]) return exact[span];
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
    const top = 14, bottom = 24, left = 2, right = 2;
    const pw = w - left - right, ph = h - top - bottom;
    const x0 = opts.x0, x1 = opts.x1, yTop = opts.yTop || 1;

    // grid lines first (labels draw after the series, like the desktop overlay)
    const yLabelPos = [];
    if (opts.gridStep) {
      for (let yv = opts.gridStep; yv <= yTop + 1e-9; yv += opts.gridStep) {
        const py = top + ph - (yv / yTop) * ph;
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
    const base = top + ph;
    for (const s of series) {
      const pts = s.pts;
      if (!pts || pts.length < 2) continue;
      const px = (ts) => left + ((ts - x0) / (x1 - x0)) * pw;
      const py = (v) => base - (Math.min(v, yTop) / yTop) * ph;
      ctx.beginPath();
      ctx.moveTo(px(pts[0][0]), base);
      for (const [ts, v] of pts) ctx.lineTo(px(ts), py(v));
      ctx.lineTo(px(pts[pts.length - 1][0]), base);
      ctx.closePath();
      ctx.fillStyle = dim(s.color);
      ctx.fill();
      ctx.beginPath();
      for (let i = 0; i < pts.length; i++) {
        const x = px(pts[i][0]), y = py(pts[i][1]);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = css(s.color);
      ctx.lineWidth = 1.8;
      ctx.lineJoin = 'round';
      ctx.stroke();
    }
    ctx.restore();

    // x labels on top, anchored to LOCAL midnight like the desktop
    // (epoch multiples were offset by the timezone)
    if (opts.labels !== false) {
      ctx.font = '11px "Segoe UI", system-ui, sans-serif';
      ctx.textBaseline = 'alphabetic';
      ctx.textAlign = 'left';
      ctx.fillStyle = AXIS;
      for (const [yv, py] of yLabelPos) ctx.fillText(fmtB(yv), left + 6, py - 4);
      const step = timeStep(x1 - x0);
      const d0 = new Date(x0 * 1000);
      d0.setHours(0, 0, 0, 0);
      const anchor = d0.getTime() / 1000;
      let ts = anchor + Math.ceil((x0 - anchor) / step) * step;
      ctx.textAlign = 'center';
      while (ts <= x1) {
        const px = left + ((ts - x0) / (x1 - x0)) * pw;
        ctx.fillText(fmtTime(ts, step), px, h - 7);
        ts += step;
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
    const sweep = Math.PI * 2 * 0.82; // gap at the bottom like the reference
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

  // ---------- demo data ----------
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
    '5m': { n: 300, bucket: 1, ambient: 60e3, bursts: 2, bMin: 2e6, bMax: 9e6, upMax: 1.5e6, seed: 11 },
    '3h': { n: 181, bucket: 60, ambient: 6e6, bursts: 4, bMin: 60e6, bMax: 750e6, upMax: 200e6, seed: 22 },
    '24h': { n: 1440, bucket: 60, ambient: 3e6, bursts: 9, bMin: 200e6, bMax: 3e9, upMax: 700e6, seed: 33 },
    '7d': { n: 10080, bucket: 60, ambient: 2e6, bursts: 22, bMin: 300e6, bMax: 5e9, upMax: 1.2e9, seed: 44 },
    '30d': { n: 720, bucket: 3600, ambient: 150e6, bursts: 15, bMin: 2e9, bMax: 40e9, upMax: 9e9, seed: 55 },
    unlimited: { n: 400, bucket: 86400, ambient: 400e6, bursts: 13, bMin: 5e9, bMax: 90e9, upMax: 25e9, seed: 66 },
  };

  function genDemo(range) {
    const cfg = DEMO_CFG[range];
    const rnd = mulberry32(cfg.seed);
    const n = cfg.n, bucket = cfg.bucket;
    const now = Math.floor(Date.now() / 1000 / bucket) * bucket;
    const ts = new Array(n);
    for (let i = 0; i < n; i++) ts[i] = now - (n - 1 - i) * bucket;

    const down = new Float64Array(n), up = new Float64Array(n);
    for (let i = 0; i < n; i++) {
      down[i] = cfg.ambient * (0.4 + rnd() * 1.2);
      up[i] = cfg.ambient * (0.15 + rnd() * 0.5);
    }
    const addBurst = (arr, maxV) => {
      const c = Math.floor(rnd() * n);
      const width = 1 + Math.floor(rnd() * 3);
      const mag = cfg.bMin + rnd() * (maxV - cfg.bMin);
      for (let i = c - width; i <= c + width; i++) {
        if (i < 0 || i >= n) continue;
        const d = Math.abs(i - c) / (width + 1);
        arr[i] += mag * Math.exp(-d * d * 2.2);
      }
    };
    for (let b = 0; b < cfg.bursts; b++) addBurst(down, cfg.bMax);
    for (let b = 0; b < Math.max(1, Math.round(cfg.bursts / 2)); b++) addBurst(up, cfg.upMax);

    const totalDown = down.reduce((a, b) => a + b, 0);
    const totalUp = up.reduce((a, b) => a + b, 0);

    let wsum = 0;
    for (const a of APP_POOL) wsum += a[1];
    const apps = APP_POOL.map(([name, w, color]) => {
      const share = (w / wsum) * (0.75 + rnd() * 0.5);
      return {
        name, color,
        count: 2 + Math.floor(rnd() * 140),
        inBytes: totalDown * share * (0.75 + rnd() * 0.4),
        outBytes: totalUp * share * (0.6 + rnd() * 0.8),
      };
    }).sort((a, b) => b.inBytes + b.outBytes - (a.inBytes + a.outBytes));

    const hosts = HOST_POOL.slice(0, 6).map(([h, flag, region], i) => ({
      host: h, flag, region,
      inBytes: totalDown * (0.22 - i * 0.03) * (0.8 + rnd() * 0.5),
      outBytes: totalUp * (0.25 - i * 0.035) * (0.8 + rnd() * 0.5),
    }));

    const wan = (totalDown + totalUp) * 0.975;
    const lan = (totalDown + totalUp) * 0.025;
    return { ts, down, up, bucket, totalDown, totalUp, wan, lan, apps, hosts };
  }

  // ---------- rendering ----------
  function render(d) {
    const pd = pipeline(d.ts, Array.from(d.down), 2400);
    const pu = pipeline(d.ts, Array.from(d.up), 2400);

    let rawMax = 0;
    for (const v of d.down) if (v > rawMax) rawMax = v;
    for (const v of d.up) if (v > rawMax) rawMax = v;
    const yTop = state.autoScale
      ? (() => {
          const target = Math.max(rawMax * 1.25, 1024);
          const st = niceStep(target / 8);
          return Math.ceil(target / st) * st;
        })()
      : state.scaleCap;

    drawChart($('main-chart'), [
      { color: DOWN, pts: pd.pts },
      { color: UP, pts: pu.pts },
    ], { x0: d.ts[0], x1: d.ts[d.ts.length - 1], yTop, gridStep: niceStep(yTop / 8) });

    const n = d.ts.length;
    const total = new Array(n);
    for (let i = 0; i < n; i++) total[i] = d.down[i] + d.up[i];
    const mr = Math.max(2, Math.min(400, Math.round(n / 25)));
    let mpts = total.map((v, i) => [d.ts[i], v]);
    mpts = movingAvg(mpts, mr);
    mpts = movingAvg(mpts, Math.max(1, Math.round((mr * 2) / 3)));
    mpts = movingAvg(mpts, Math.max(1, Math.round(mr / 3)));
    mpts = catmull(mpts, 400);
    let mMax = 0;
    for (const p of mpts) if (p[1] > mMax) mMax = p[1];
    drawChart($('mini-chart'), [{ color: DOWN, pts: mpts }], {
      x0: d.ts[0], x1: d.ts[d.ts.length - 1],
      yTop: mMax * 1.12, labels: false,
    });

    drawRing(d.totalDown, d.totalUp);
    $('stat-down').textContent = fmtB(d.totalDown);
    $('stat-up').textContent = fmtB(d.totalUp);
    $('stat-down-rate').textContent = fmtB(d.down[n - 1] / d.bucket) + '/s';
    $('stat-up-rate').textContent = fmtB(d.up[n - 1] / d.bucket) + '/s';
    $('stat-wan').textContent = fmtB(d.wan);
    $('stat-lan').textContent = fmtB(d.lan);
    const wl = d.wan + d.lan || 1;
    $('bar-wan').style.width = ((d.wan / wl) * 100).toFixed(1) + '%';
    $('bar-lan').style.width = Math.max(1.5, (d.lan / wl) * 100).toFixed(1) + '%';

    renderLists(d);
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
    return {
      ts, down, up, bucket,
      totalDown: down.reduce((a, b) => a + b, 0),
      totalUp: up.reduce((a, b) => a + b, 0),
      wan: 0, lan: 0, apps: [], hosts: [],
    };
  }

  async function loadLive() {
    const range = state.range;
    const [traffic, apps, hosts] = await Promise.all([
      fetchJson(`/api/traffic?range=${range}`),
      fetchJson(`/api/apps?range=${range}&limit=10`).catch(() => null),
      fetchJson(`/api/hosts?range=${range}&limit=8`).catch(() => null),
    ]);
    const d = fromTraffic(traffic);
    const palette = APP_POOL.map((p) => p[2]);
    if (apps && apps.apps) {
      d.apps = apps.apps.map((a, i) => ({
        name: a.name || 'unknown', color: palette[i % palette.length], count: 0,
        inBytes: a.inBytes || 0, outBytes: a.outBytes || 0,
      })).sort((a, b) => b.inBytes + b.outBytes - (a.inBytes + a.outBytes));
    }
    if (hosts && hosts.hosts) {
      const flags = { DE: '🇩🇪', IR: '🇮🇷', NL: '🇳🇱', US: '🇺🇸', FR: '🇫🇷', GB: '🇬🇧', TR: '🇹🇷', SE: '🇸🇪' };
      d.hosts = hosts.hosts.map((h) => ({
        host: h.hostname || h.ip,
        flag: flags[(h.region || '').slice(-2).toUpperCase()] || '🌐',
        region: h.region || h.ip || '',
        inBytes: h.inBytes || 0, outBytes: h.outBytes || 0,
      }));
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
    render(d);
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
    state.demo = genDemo(state.range);
    render(state.demo);
  }

  function setLive(live, version) {
    state.live = live;
    const badge = $('mode-badge');
    const banner = $('banner');
    if (live) {
      badge.className = 'badge live';
      badge.innerHTML = `<span class="dot"></span>live v${version || '?'}`;
      banner.hidden = true;
      if (!state.timer) state.timer = setInterval(refresh, 3000);
    } else {
      badge.className = 'badge demo';
      badge.innerHTML = '<span class="dot"></span>demo data';
      banner.hidden = false;
      banner.textContent = 'Demo data — hosted previews cannot reach your PC. Open http://127.0.0.1:8912 for live numbers.';
      if (state.timer) { clearInterval(state.timer); state.timer = null; }
    }
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
    for (const b of $('period-menu').querySelectorAll('button')) {
      b.classList.toggle('sel', b === btn);
    }
    $('period-btn').firstChild.textContent = RANGE_LABEL[state.range] + ' ';
    closeMenus();
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
    if (state.demo) render(state.demo);
  });
  function buildLadder() {
    $('scale-ladder').innerHTML = LADDER.map(([lbl, val]) =>
      `<button data-cap="${val}" class="${Math.abs(state.scaleCap - val) < 1 ? 'sel' : ''}">${lbl}</button>`
    ).join('');
  }
  $('scale-ladder').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-cap]');
    if (!btn) return;
    state.scaleCap = Number(btn.dataset.cap);
    buildLadder();
    updateScaleBtn();
    if (state.demo) render(state.demo);
  });
  function updateScaleBtn() {
    $('scale-btn').innerHTML = (state.autoScale
      ? 'Auto-scale'
      : `cap ${fmtB(state.scaleCap)}`) + ' <span class="caret">▾</span>';
  }
  document.addEventListener('click', closeMenus);

  buildLadder();
  updateScaleBtn();

  const ro = new ResizeObserver(() => {
    if (state.demo || state.live) refresh();
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
