/* Packetly Web dashboard.
 * Mirrors the desktop app's chart pipeline (3-pass box smoothing,
 * Catmull-Rom rounding, peak normalization, honest raw-peak scale) so the
 * hills look identical. Runs on demo data everywhere; switches to live
 * mode automatically when served from the collector (same origin, :8912).
 * Zero dependencies. */
(() => {
  'use strict';

  const DOWN = [224, 80, 200];
  const UP = [110, 147, 232];
  const GRID = 'rgba(255,255,255,0.035)';
  const AXIS = 'rgba(160,172,200,0.8)';
  const RANGES = ['5m', '3h', '24h', '7d', '30d', 'unlimited'];
  const RANGE_LABEL = { '5m': '5 minutes', '3h': '3 hours', '24h': '24 hours', '7d': '7 days', '30d': '30 days', unlimited: 'all time' };

  const state = { range: '3h', live: false, timer: null, demo: null };

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
  // values[] -> smoothed, peak-normalized pts [[ts, v], ...]; returns {pts, rawMax}
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
    const steps = [15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800, 2592000];
    for (const s of steps) if (span / s <= 9) return s;
    return 31536000;
  }
  function fmtTime(ts, step) {
    const d = new Date(ts * 1000);
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
    const labels = opts.labels !== false;
    const top = labels ? 16 : 2, bottom = labels ? 22 : 2, left = 2, right = 2;
    const pw = w - left - right, ph = h - top - bottom;
    const x0 = opts.x0, x1 = opts.x1, yTop = opts.yTop || 1;

    // horizontal grid lines (labels draw after the series so fills
    // never cover them, like the desktop overlay)
    const yLabelPos = [];
    if (labels && opts.gridStep) {
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
      // fill
      ctx.beginPath();
      ctx.moveTo(px(pts[0][0]), base);
      for (const [ts, v] of pts) ctx.lineTo(px(ts), py(v));
      ctx.lineTo(px(pts[pts.length - 1][0]), base);
      ctx.closePath();
      ctx.fillStyle = dim(s.color);
      ctx.fill();
      // stroke
      ctx.beginPath();
      for (let i = 0; i < pts.length; i++) {
        const x = px(pts[i][0]), y = py(pts[i][1]);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = css(s.color);
      ctx.lineWidth = opts.lineWidth || 1.8;
      ctx.lineJoin = 'round';
      ctx.stroke();
    }
    ctx.restore();

    // labels on top of everything
    if (labels && opts.gridStep) {
      ctx.font = '11px "Segoe UI", system-ui, sans-serif';
      ctx.textAlign = 'left';
      ctx.textBaseline = 'alphabetic';
      ctx.fillStyle = AXIS;
      for (const [yv, py] of yLabelPos) ctx.fillText(fmtB(yv), left + 6, py - 4);
      const step = timeStep(x1 - x0);
      ctx.textAlign = 'center';
      for (let ts = Math.ceil(x0 / step) * step; ts <= x1; ts += step) {
        const px = left + ((ts - x0) / (x1 - x0)) * pw;
        ctx.fillStyle = AXIS;
        ctx.fillText(fmtTime(ts, step), px, h - 6);
      }
    }
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

    // apps split of the totals
    let wsum = 0;
    for (const a of APP_POOL) wsum += a[1];
    const apps = APP_POOL.map(([name, w, color], i) => {
      const share = (w / wsum) * (0.75 + rnd() * 0.5);
      return {
        name,
        color,
        count: 2 + Math.floor(rnd() * 140),
        inBytes: totalDown * share * (0.75 + rnd() * 0.4),
        outBytes: totalUp * share * (0.6 + rnd() * 0.8),
      };
    }).sort((a, b) => b.inBytes + b.outBytes - (a.inBytes + a.outBytes));

    // hosts split
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
    const target = 2400;
    const pd = pipeline(d.ts, Array.from(d.down), target);
    const pu = pipeline(d.ts, Array.from(d.up), target);

    let rawMax = 0;
    for (const v of d.down) if (v > rawMax) rawMax = v;
    for (const v of d.up) if (v > rawMax) rawMax = v;
    const targetCap = Math.max(rawMax * 1.25, 1024);
    const st = niceStep(targetCap / 8);
    const yTop = Math.ceil(targetCap / st) * st;

    drawChart($('main-chart'), [
      { color: DOWN, pts: pd.pts },
      { color: UP, pts: pu.pts },
    ], { x0: d.ts[0], x1: d.ts[d.ts.length - 1], yTop, gridStep: niceStep(yTop / 8) });

    // mini timeline: total activity
    const n = d.ts.length;
    const total = new Array(n);
    for (let i = 0; i < n; i++) total[i] = d.down[i] + d.up[i];
    const mr = Math.max(2, Math.min(400, Math.round(n / 25)));
    let mpts = total.map((v, i) => [d.ts[i], v]);
    mpts = movingAvg(mpts, mr);
    mpts = movingAvg(mpts, Math.max(1, Math.round((mr * 2) / 3)));
    mpts = movingAvg(mpts, Math.max(1, Math.round(mr / 3)));
    mpts = catmull(mpts, 400);
    drawChart($('mini-chart'), [{ color: DOWN, pts: mpts }], {
      x0: d.ts[0], x1: d.ts[d.ts.length - 1],
      yTop: Math.max(...mpts.map((p) => p[1])) * 1.1,
      labels: false, lineWidth: 1.0,
    });

    // stats
    $('stat-down').textContent = fmtB(d.totalDown);
    $('stat-up').textContent = fmtB(d.totalUp);
    $('stat-down-rate').textContent = fmtB(d.down[n - 1] / d.bucket) + '/s';
    $('stat-up-rate').textContent = fmtB(d.up[n - 1] / d.bucket) + '/s';
    $('stat-wan').textContent = fmtB(d.wan);
    $('stat-lan').textContent = fmtB(d.lan);
    const tot = d.totalDown + d.totalUp || 1;
    $('bar-down').style.width = ((d.totalDown / tot) * 100).toFixed(1) + '%';
    $('bar-up').style.width = ((d.totalUp / tot) * 100).toFixed(1) + '%';
    $('bar-wan').style.width = ((d.wan / (d.wan + d.lan || 1)) * 100).toFixed(1) + '%';
    $('bar-lan').style.width = ((d.lan / (d.wan + d.lan || 1)) * 100).toFixed(1) + '%';

    // apps
    const top = d.apps[0] ? d.apps[0].inBytes + d.apps[0].outBytes : 1;
    $('apps-sub').textContent = RANGE_LABEL[state.range];
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

    // hosts
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
    const totalDown = down.reduce((a, b) => a + b, 0);
    const totalUp = up.reduce((a, b) => a + b, 0);
    let wan = totalDown + totalUp, lan = 0;
    return { ts, down, up, bucket, totalDown, totalUp, wan, lan, apps: [], hosts: [] };
  }

  async function loadLive() {
    const range = state.range;
    const [traffic, apps, hosts] = await Promise.all([
      fetchJson(`/api/traffic?range=${range}`),
      fetchJson(`/api/apps?range=${range}&limit=10`).catch(() => null),
      fetchJson(`/api/hosts?range=${range}&limit=8`).catch(() => null),
    ]);
    const d = fromTraffic(traffic);
    if (apps && apps.apps) {
      d.apps = apps.apps.map((a) => ({
        name: a.name || 'unknown', color: null, count: 0,
        inBytes: a.inBytes || 0, outBytes: a.outBytes || 0,
      })).sort((a, b) => b.inBytes + b.outBytes - (a.inBytes + a.outBytes));
      const palette = APP_POOL.map((p) => p[2]);
      d.apps.forEach((a, i) => { a.color = palette[i % palette.length]; a.count = 0; });
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
    // live totals: prefer the API's own view of WAN/LAN if hosts reveal private peers
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
        // collector went away mid-session -> fall back to demo
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
      badge.innerHTML = `<span class="dot"></span>live · collector v${version || '?'}`;
      banner.hidden = true;
      if (!state.timer) state.timer = setInterval(refresh, 3000);
    } else {
      badge.className = 'badge demo';
      badge.innerHTML = '<span class="dot"></span>demo data';
      banner.hidden = false;
      banner.textContent = 'Demo data — this hosted preview cannot reach your PC. Run the dashboard on your machine (http://127.0.0.1:8912) for live numbers.';
      if (state.timer) { clearInterval(state.timer); state.timer = null; }
    }
  }

  // ---------- wiring ----------
  $('periods').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-range]');
    if (!btn) return;
    state.range = btn.dataset.range;
    for (const b of $('periods').querySelectorAll('button')) {
      b.classList.toggle('active', b === btn);
    }
    refresh();
  });

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
