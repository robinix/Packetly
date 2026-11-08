/* Packetly dashboard */
"use strict";

const $ = (id) => document.getElementById(id);

const state = {
  range: "5m",
  appId: null,
  customFrom: null,
  customTo: null,
  themePref: localStorage.getItem("packetly-theme") || "auto",
  timer: null,
  chart: null,
  colorCache: new Map(),
  lastData: null,
  offline: false,
};

const RANGES = {
  "5m": { label: "last 5 minutes", refresh: 2000 },
  "3h": { label: "last 3 hours", refresh: 5000 },
  "24h": { label: "last 24 hours", refresh: 60000 },
  "30d": { label: "last 30 days", refresh: 60000 },
  "custom": { label: "custom range", refresh: 60000 },
  "unlimited": { label: "all time", refresh: 60000 },
};

/* ---------- helpers ---------- */

function fmtB(b) {
  if (b == null || isNaN(b)) return "–";
  const abs = Math.abs(b);
  if (abs >= 1 << 30) return (b / (1 << 30)).toFixed(2) + " GB";
  if (abs >= 1 << 20) return (b / (1 << 20)).toFixed(2) + " MB";
  if (abs >= 1 << 10) return (b / 1024).toFixed(1) + " KB";
  return Math.round(b) + " B";
}

function niceCeil(v) {
  if (!(v > 0)) return 1;
  const e = Math.pow(10, Math.floor(Math.log10(v)));
  const m = v / e;
  const s = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
  return s * e;
}

function flag(cc) {
  if (!cc || cc.length !== 2) return "";
  return String.fromCodePoint(...[...cc.toUpperCase()].map(ch => 0x1F1E6 + ch.charCodeAt(0) - 65));
}

function hashStr(s) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
  return h >>> 0;
}

function colorFor(key) {
  if (state.colorCache.has(key)) return state.colorCache.get(key);
  const hue = (hashStr(String(key)) * 137.508) % 360;
  const c = `hsl(${hue.toFixed(0)}, 62%, 55%)`;
  state.colorCache.set(key, c);
  return c;
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function fmtTime(tsSec) {
  const d = new Date(tsSec * 1000);
  const range = state.range;
  if (range === "5m" || range === "3h") return d.toLocaleTimeString();
  if (range === "24h") return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return d.toLocaleDateString() + " " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

/* ---------- theme ---------- */

function appliedTheme() {
  if (state.themePref === "auto")
    return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  return state.themePref;
}

function applyTheme() {
  document.documentElement.setAttribute("data-theme", appliedTheme());
  if (state.chart) rebuildChart();
}

function cycleTheme() {
  const order = ["auto", "light", "dark"];
  state.themePref = order[(order.indexOf(state.themePref) + 1) % order.length];
  localStorage.setItem("packetly-theme", state.themePref);
  $("theme-btn").title = "Theme: " + state.themePref;
  applyTheme();
}

/* ---------- data ---------- */

async function fetchJson(url) {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${r.status} ${url}`);
  return r.json();
}

function rangeQuery() {
  let q = `range=${state.range}`;
  if (state.range === "custom" && state.customFrom) {
    q = `range=custom&from=${state.customFrom}&to=${state.customTo}`;
  }
  if (state.appId != null) q += `&app_id=${state.appId}`;
  return q;
}

async function refresh() {
  try {
    const [traffic, apps, hosts] = await Promise.all([
      fetchJson(`/api/traffic?${rangeQuery()}&breakdown=apps`),
      fetchJson(`/api/apps?${rangeQuery().replace("&app_id=" + state.appId, "")}&limit=10`),
      fetchJson(`/api/hosts?${rangeQuery().replace("&app_id=" + state.appId, "")}&limit=10`),
    ]);
    state.lastData = traffic;
    setOffline(false);
    renderChart(traffic);
    renderApps(apps.apps);
    renderHosts(hosts.hosts);
    loadUsagePanel();
  } catch (err) {
    setOffline(true);
  }
  scheduleNext();
}

function setOffline(off) {
  if (state.offline === off) return;
  state.offline = off;
  $("live-dot").classList.toggle("err", off);
  $("live-text").textContent = off ? "offline — retrying" : "live";
}

function scheduleNext() {
  if (state.timer) clearTimeout(state.timer);
  if (document.hidden) return;
  state.timer = setTimeout(refresh, RANGES[state.range].refresh);
}

document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });

/* ---------- chart ---------- */

const TOP_N = 8;

function pivot(traffic) {
  // response: { from, to, bucketSeconds, data: { rows: [[ts, app_id, in, out]], appNames } }
  const { from, to, bucketSeconds: bucket, data } = traffic;
  const rows = (data && data.rows) || [];
  const appNames = (data && data.appNames) || {};
  const first = rows.length ? rows[0][0] : from;
  let x0 = Math.ceil(Math.max(from, first - bucket) / bucket) * bucket;
  if (to <= x0) x0 = Math.ceil(from / bucket) * bucket;
  const n = Math.min(Math.floor((to - x0) / bucket) + 1, 25000);
  if (n <= 0) return null;

  const xs = new Array(n);
  for (let i = 0; i < n; i++) xs[i] = x0 + i * bucket;

  const byApp = new Map();
  for (const [ts, appId, cin, cout] of rows) {
    const i = Math.round((ts - x0) / bucket);
    if (i < 0 || i >= n) continue;
    if (!byApp.has(appId)) byApp.set(appId, [0, 0]);
    const acc = byApp.get(appId);
    acc[0] += cin; acc[1] += cout;
    if (!appNames[appId]) appNames[appId] = `app ${appId}`;
  }

  // per-app time series
  const series = new Map(); // appId → {in: Float64Array, out: Float64Array}
  for (const [appId, _] of byApp) series.set(appId, { in: new Float64Array(n), out: new Float64Array(n) });
  const seen = new Map(); // (appId,ts) → idx to avoid double count from multi-bucket rows
  for (const [ts, appId, cin, cout] of rows) {
    const i = Math.round((ts - x0) / bucket);
    if (i < 0 || i >= n) continue;
    const s = series.get(appId);
    const key = appId + ":" + ts;
    if (seen.has(key)) continue;
    seen.set(key, 1);
    s.in[i] += cin; s.out[i] += cout;
  }

  // top N apps by total, rest → Other
  const totals = [...byApp.entries()].map(([id, [i, o]]) => [id, i + o]).sort((a, b) => b[1] - a[1]);
  const top = totals.slice(0, TOP_N).map(t => t[0]);
  const rest = totals.slice(TOP_N).map(t => t[0]);

  return { xs, series, top, rest, appNames };
}

function renderChart(traffic) {
  const p = pivot(traffic);
  const sub = $("chart-subtitle");
  sub.textContent = `${RANGES[state.range].label} · ${traffic.tier === "ring" ? "live" : traffic.tier.replace("traffic_", "")} resolution`;
  $("empty-note").style.display = (p && p.top.length) ? "none" : "flex";

  if (state.appId != null) {
    const chip = $("filter-chip");
    chip.classList.add("show");
    $("filter-name").textContent = traffic.appNames[state.appId] || `app ${state.appId}`;
    chip.querySelector(".dot").style.background = colorFor(state.appId);
  } else {
    $("filter-chip").classList.remove("show");
  }

  if (!p || !p.top.length) { destroyChart(); state.curDefKeys = null; return; }
  const defKeys = chartSeriesDefs(p).map(d => String(d.key)).join("|");
  if (!state.chart || state.curDefKeys !== defKeys) {
    state.curDefKeys = defKeys;
    rebuildChart();
    return;
  }
  updateChartData(p);
}

function chartSeriesDefs(p) {
  const defs = [];
  state.curDefs = defs;
  state.curDefKeys = null; // set by callers once they commit to these defs
  if (state.appId != null) {
    defs.push({ key: state.appId + ":in", label: "download", color: cssVar("--in") });
    defs.push({ key: state.appId + ":out", label: "upload", color: cssVar("--out") });
    return defs;
  }
  for (const id of p.top) defs.push({ key: id, label: p.appNames[id] || `app ${id}`, color: colorFor(id) });
  if (p.rest.length) defs.push({ key: "other", label: `other (${p.rest.length})`, color: cssVar("--axis") });
  return defs;
}

function chartDataArrays(p) {
  const n = p.xs.length;
  const arrays = [];
  if (state.appId != null) {
    const s = p.series.get(state.appId) || { in: new Float64Array(n), out: new Float64Array(n) };
    arrays.push(Array.from(s.in));
    arrays.push(Array.from(s.out));
    return arrays;
  }
  for (const id of p.top) {
    const s = p.series.get(id);
    arrays.push(s ? Array.from(s.in.map((v, i) => v + s.out[i])) : new Array(n).fill(0));
  }
  if (p.rest.length) {
    const acc = new Array(n).fill(0);
    for (const id of p.rest) {
      const s = p.series.get(id);
      if (!s) continue;
      for (let i = 0; i < n; i++) acc[i] += s.in[i] + s.out[i];
    }
    arrays.push(acc);
  }
  return arrays;
}

function rebuildChart() {
  destroyChart();
  if (!state.lastData) return;
  const p = pivot(state.lastData);
  if (!p) return;
  const defs = chartSeriesDefs(p);
  state.curDefKeys = defs.map(d => String(d.key)).join("|");
  const container = $("chart");
  const width = container.clientWidth;
  const opts = {
    width,
    height: 340,
    class: "gwl-chart",
    scales: { x: { time: true }, y: { range: (u, dmin, dmax) => [0, niceCeil(dmax || 1)] } },
    axes: [
      {
        stroke: cssVar("--axis"),
        grid: { stroke: cssVar("--grid"), width: 1 },
        ticks: { stroke: cssVar("--grid") },
      },
      {
        stroke: cssVar("--axis"),
        grid: { stroke: cssVar("--grid"), width: 1 },
        ticks: { stroke: cssVar("--grid") },
        values: (u, splits) => splits.map(v => fmtB(v)),
      },
    ],
    series: [
      {},
      ...defs.map(d => ({
        label: d.label,
        stroke: d.color,
        width: 1.6,
        fill: d.color + "33",
        points: { show: false },
        spanGaps: true,
        value: (u, v) => fmtB(v),
      })),
    ],
    legend: { show: false },
    cursor: {
      drag: { setScale: false, x: false, y: false },
      points: { show: false },
    },
    hooks: {
      setCursor: [
        (u) => {
          const idx = u.cursor.idx;
          const tt = $("tooltip");
          if (idx == null || !p.top.length && state.appId == null) { tt.style.display = "none"; return; }
          if (u.cursor.left == null) { tt.style.display = "none"; return; }
          const ts = u.data[0][idx];
          const defs = chartSeriesDefs(p);
          const rows = defs.map((d, i) => ({ d, v: u.data[i + 1][idx] || 0 }))
            .filter(r => r.v > 0)
            .sort((a, b) => b.v - a.v)
            .slice(0, 6);
          if (!rows.length) { tt.style.display = "none"; return; }
          tt.innerHTML = `<div class="t">${fmtTime(ts)}</div>` + rows.map(r =>
            `<div class="row"><span class="dot" style="background:${r.d.color}"></span><span class="n">${escapeHtml(r.d.label)}</span><span class="v">${fmtB(r.v)}</span></div>`
          ).join("");
          tt.style.display = "block";
          const pad = 14;
          let x = u.cursor.left + container.getBoundingClientRect().left + pad;
          let y = u.cursor.top + container.getBoundingClientRect().top + pad;
          const bw = tt.offsetWidth, bh = tt.offsetHeight;
          if (x + bw > window.innerWidth - 8) x = window.innerWidth - bw - 8;
          if (y + bh > window.innerHeight - 8) y = y - bh - 2 * pad;
          tt.style.left = x + "px";
          tt.style.top = y + "px";
        },
      ],
    },
  };
  const arrays = chartDataArrays(p);
  state.chart = new uPlot(opts, [p.xs, ...arrays], container);
}

function updateChartData(p) {
  if (!state.chart) { rebuildChart(); return; }
  const arrays = chartDataArrays(p);
  if (arrays.length !== state.chart.series.length - 1) { state.curDefKeys = null; rebuildChart(); return; }
  state.chart.setData([p.xs, ...arrays]);
}

function destroyChart() {
  if (state.chart) { state.chart.destroy(); state.chart = null; }
  $("tooltip").style.display = "none";
}

window.addEventListener("resize", () => {
  if (state.chart) state.chart.setSize({ width: $("chart").clientWidth, height: 340 });
});

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

/* ---------- lists ---------- */

function renderApps(apps) {
  const el = $("apps-list");
  if (!apps || !apps.length) { el.innerHTML = '<div class="none">no traffic recorded in this range</div>'; return; }
  const max = Math.max(...apps.map(a => a.inBytes + a.outBytes), 1);
  el.innerHTML = apps.map(a => {
    const c = colorFor(a.id);
    const sel = state.appId === a.id ? " selected" : "";
    const share = ((a.inBytes + a.outBytes) / max * 100).toFixed(1);
    const icon = `<img class="appicon" src="/api/icon?app_id=${a.id}" onerror="this.remove()">`;
    return `<div class="row${sel}" data-app="${a.id}" title="${escapeHtml(a.path || "")}">
      ${icon}<span class="dot" style="background:${c}"></span>
      <span class="name">${escapeHtml(a.name)}<span class="sub">${escapeHtml(shortPath(a.path))}</span></span>
      <span class="num"><span class="d">↓ ${fmtB(a.inBytes)}</span></span>
      <span class="num"><span class="u">↑ ${fmtB(a.outBytes)}</span></span>
      <span class="bar"><i style="width:${share}%;background:${c}"></i></span>
    </div>`;
  }).join("");
  el.querySelectorAll(".row").forEach(r => r.addEventListener("click", () => {
    const id = parseInt(r.dataset.app, 10);
    state.appId = state.appId === id ? null : id;
    refresh();
  }));
}

function shortPath(p) {
  if (!p || p.startsWith("unknown:")) return "";
  return p;
}

function renderHosts(hosts) {
  const el = $("hosts-list");
  if (!hosts || !hosts.length) { el.innerHTML = '<div class="none">no remote hosts recorded in this range</div>'; return; }
  const max = Math.max(...hosts.map(h => h.inBytes + h.outBytes), 1);
  el.innerHTML = hosts.map(h => {
    const label = h.hostname || h.ip;
    const share = ((h.inBytes + h.outBytes) / max * 100).toFixed(1);
    const fl = flag(h.region);
    return `<div class="row" style="cursor:default">
      <span class="dot" style="background:var(--accent)"></span>
      <span class="name">${fl ? fl + " " : ""}${escapeHtml(label)}<span class="sub">${h.hostname ? escapeHtml(h.ip) : ""}</span></span>
      <span class="num"><span class="d">↓ ${fmtB(h.inBytes)}</span></span>
      <span class="num"><span class="u">↑ ${fmtB(h.outBytes)}</span></span>
      <span class="bar"><i style="width:${share}%;background:var(--accent)"></i></span>
    </div>`;
  }).join("");
}

/* ---------- status footer ---------- */

async function refreshStatus() {
  try {
    const [s, settings] = await Promise.all([fetchJson("/api/status"), fetchJson("/api/settings")]);
    updateGeoipToggle(settings.geoip);
    $("ft-collector").textContent = s.collector.split(" (")[0] + (s.elevated ? "" : " (limited)");
    $("ft-events").textContent = s.eventsSeen.toLocaleString();
    $("ft-rows").textContent = `${s.rows.t1s.toLocaleString()} / ${s.rows.t60s.toLocaleString()} / ${s.rows.t3600s.toLocaleString()} / ${s.rows.t86400s.toLocaleString()}`;
    $("ft-db").textContent = fmtB(s.dbBytes);
    $("ft-uptime").textContent = Math.floor(s.uptimeSeconds / 60) + "m " + (s.uptimeSeconds % 60) + "s";
  } catch { }
  refreshEventsBadge();
}

/* ---------- phase 4: usage, quotas, alerts ---------- */

const usageState = { period: "today", lastFetch: 0, appSelectLoaded: 0 };

const USAGE_LABEL = { today: "today", week: "this week", month: "this month" };
const EV_ICON = { 1: "\u{1F195}", 2: "\u{1F7E1}", 3: "\u{1F7E0}", 4: "\u{1F534}" };

function relTime(ts) {
  const s = Math.max(0, Math.floor(Date.now() / 1000) - ts);
  if (s < 60) return s + "s ago";
  if (s < 3600) return Math.floor(s / 60) + "m ago";
  if (s < 86400) return Math.floor(s / 3600) + "h ago";
  return Math.floor(s / 86400) + "d ago";
}

function eventText(e) {
  if (e.type === 1) {
    let name = e.appName;
    try { name = name || JSON.parse(e.data).name; } catch { }
    return `New app on the network: ${name || "unknown"}`;
  }
  try {
    const d = JSON.parse(e.data);
    const who = e.appId == null ? "All apps" : (e.appName || "app");
    const label = { daily: "daily", weekly: "weekly", monthly: "monthly" }[d.period] || d.period;
    return `${who} reached ${d.threshold}% of its ${label} quota (${fmtB(d.used)} / ${fmtB(d.limit)})`;
  } catch { return "alert"; }
}

async function refreshEventsBadge() {
  try {
    const d = await fetchJson("/api/events?limit=1");
    const b = $("bell-badge");
    b.textContent = d.unseen > 99 ? "99+" : (d.unseen || "");
    b.classList.toggle("show", (d.unseen || 0) > 0);
  } catch { }
}

async function toggleEventsPanel(force) {
  const panel = $("events-panel");
  const show = force != null ? force : panel.style.display === "none";
  panel.style.display = show ? "block" : "none";
  if (!show) return;
  try {
    const d = await fetchJson("/api/events?limit=60");
    const el = $("events-list");
    if (!d.events.length) { el.innerHTML = '<div class="none">no alerts yet</div>'; return; }
    el.innerHTML = d.events.map(e => `
      <div class="event-row">
        <span class="ico">${EV_ICON[e.type] || "\u{1F514}"}</span>
        <span class="body"><span class="txt">${escapeHtml(eventText(e))}</span>
        <span class="when">${relTime(e.ts)}</span></span>
      </div>`).join("");
  } catch { el.innerHTML = '<div class="none">failed to load alerts</div>'; }
}

async function markEventsSeen() {
  try { await fetch("/api/events/seen", { method: "POST" }); } catch { }
  $("bell-badge").classList.remove("show");
  $("bell-badge").textContent = "";
  toggleEventsPanel(false);
}

async function loadUsagePanel(force) {
  const nowMs = Date.now();
  if (!force && nowMs - usageState.lastFetch < 10000) return;
  usageState.lastFetch = nowMs;
  try {
    const [usage, quotas] = await Promise.all([
      fetchJson(`/api/usage?period=${usageState.period}`),
      fetchJson("/api/quotas"),
    ]);
    renderUsage(usage, quotas.quotas);
    renderQuotaList(quotas.quotas);
    if (nowMs - usageState.appSelectLoaded > 120000) { await populateQuotaApps(); usageState.appSelectLoaded = nowMs; }
  } catch { }
}

function renderUsage(usage, quotas) {
  $("usage-total").textContent = `${fmtB(usage.total)} ${USAGE_LABEL[usageState.period] || ""}`;
  const gq = quotas.find(q => q.appId == null);
  const el = $("usage-bars");
  const rows = usage.perApp.slice(0, 12);
  if (!rows.length) { el.innerHTML = '<div class="none">no usage recorded in this period yet</div>'; return; }
  const max = Math.max(...rows.map(r => r.bytes), 1);
  let html = "";
  if (gq) {
    const pct = Math.min(100, gq.ratio * 100).toFixed(1);
    const cls = gq.ratio >= 1 ? "over" : gq.ratio >= 0.75 ? "warn" : "";
    html += `<div class="row" style="cursor:default">
      <span class="dot" style="background:var(--accent)"></span>
      <span class="name">All apps — quota</span>
      <span class="num" style="grid-column:3/5;text-align:right">${fmtB(gq.usedBytes)} / ${fmtB(gq.limitBytes)} (${pct}%)</span>
      <span class="bar"><i class="${cls}" style="width:${pct}%"></i></span>
    </div>`;
  }
  html += rows.map(r => {
    const c = colorFor(r.appId);
    const share = (r.bytes / max * 100).toFixed(1);
    return `<div class="row" style="cursor:default">
      <span class="dot" style="background:${c}"></span>
      <span class="name">${escapeHtml(r.name)}</span>
      <span class="num" style="grid-column:3/5;text-align:right">${fmtB(r.bytes)}</span>
      <span class="bar"><i style="width:${share}%;background:${c}"></i></span>
    </div>`;
  }).join("");
  el.innerHTML = html;
}

function renderQuotaList(quotas) {
  const el = $("quota-list");
  if (!quotas.length) { el.innerHTML = '<div class="none">no quotas set — add one below</div>'; return; }
  el.innerHTML = quotas.map(q => {
    const pct = Math.min(100, q.ratio * 100).toFixed(1);
    const cls = q.ratio >= 1 ? "over" : q.ratio >= 0.75 ? "warn" : "";
    const label = { daily: "daily", weekly: "weekly", monthly: "monthly" }[q.period] || q.period;
    return `<div class="quota-row" data-app="${q.appId == null ? "" : q.appId}" data-period="${q.period}">
      <span class="qn">${escapeHtml(q.appName)}<span class="sub">${label} quota</span></span>
      <span class="bar"><i class="${cls}" style="width:${pct}%"></i></span>
      <span class="qv">${fmtB(q.usedBytes)} / ${fmtB(q.limitBytes)} (${(q.ratio * 100).toFixed(0)}%)</span>
      <button class="del" title="Delete quota">\u2715</button>
    </div>`;
  }).join("");
  el.querySelectorAll(".quota-row .del").forEach(btn => btn.addEventListener("click", async () => {
    const row = btn.closest(".quota-row");
    const app = row.dataset.app ? `&appId=${row.dataset.app}` : "";
    try { await fetch(`/api/quotas?period=${row.dataset.period}${app}`, { method: "DELETE" }); } catch { }
    loadUsagePanel(true);
  }));
}

async function populateQuotaApps() {
  try {
    const d = await fetchJson("/api/apps?range=30d&limit=500");
    const sel = $("quota-app");
    const cur = sel.value;
    sel.innerHTML = '<option value="">All apps (global)</option>' +
      d.apps.map(a => `<option value="${a.id}">${escapeHtml(a.name)}</option>`).join("");
    sel.value = cur;
  } catch { }
}

function setUsagePeriod(p) {
  usageState.period = p;
  document.querySelectorAll("#usage-periods button").forEach(b => b.classList.toggle("active", b.dataset.p === p));
  loadUsagePanel(true);
}

function updateGeoipToggle(on) {
  const t = $("geoip-toggle");
  t.textContent = on ? "🌍 GeoIP: on" : "🌍 GeoIP: off";
  t.classList.toggle("on", !!on);
  t.title = on
    ? "GeoIP is ON: remote-host IPs are sent in batches to ip-api.com to resolve country names. Click to disable."
    : "Look up host countries. OFF by default — when enabled, remote-host IPs are sent in batches to ip-api.com. Click to enable.";
}

async function toggleGeoip() {
  const on = !$("geoip-toggle").classList.contains("on");
  if (on && !confirm("Enable GeoIP? Remote-host IPs will be sent in batches to ip-api.com (free service) to resolve country names. This shares your browsing metadata with that service.")) return;
  try { await fetch("/api/settings", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ geoip: on }) }); } catch { }
  updateGeoipToggle(on);
}

/* ---------- controls ---------- */

function setRange(r) {
  state.range = r;
  state.appId = null;
  document.querySelectorAll("#ranges button").forEach(b => b.classList.toggle("active", b.dataset.range === r));
  $("custom-panel").classList.toggle("show", r === "custom");
  if (r === "custom") {
    if (!state.customFrom) {
      const now = new Date();
      const dayAgo = new Date(now.getTime() - 24 * 3600 * 1000);
      $("custom-from").value = toLocalInput(dayAgo);
      $("custom-to").value = toLocalInput(now);
      state.customFrom = Math.floor(dayAgo.getTime() / 1000);
      state.customTo = Math.floor(now.getTime() / 1000);
    }
  }
  destroyChart();
  refresh();
}

function toLocalInput(d) {
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function initControls() {
  document.querySelectorAll("#ranges button").forEach(b =>
    b.addEventListener("click", () => setRange(b.dataset.range)));
  $("refresh-btn").addEventListener("click", refresh);
  $("theme-btn").addEventListener("click", cycleTheme);
  $("filter-clear").addEventListener("click", () => { state.appId = null; refresh(); });
  $("custom-apply").addEventListener("click", () => {
    const f = $("custom-from").value, t = $("custom-to").value;
    if (!f || !t) return;
    state.customFrom = Math.floor(new Date(f).getTime() / 1000);
    state.customTo = Math.max(Math.floor(new Date(t).getTime() / 1000), Math.floor(new Date(f).getTime() / 1000) + 60);
    refresh();
  });
  $("theme-btn").title = "Theme: " + state.themePref;
  window.matchMedia("(prefers-color-scheme: light)").addEventListener("change", () => {
    if (state.themePref === "auto") applyTheme();
  });
  $("bell-btn").addEventListener("click", () => toggleEventsPanel());
  $("geoip-toggle").addEventListener("click", toggleGeoip);
  $("events-seen").addEventListener("click", markEventsSeen);
  document.querySelectorAll("#usage-periods button").forEach(b =>
    b.addEventListener("click", () => setUsagePeriod(b.dataset.p)));
  $("quota-add").addEventListener("click", async () => {
    const appVal = $("quota-app").value;
    const period = $("quota-period").value;
    const n = parseFloat($("quota-limit").value);
    if (!n || n <= 0) { $("quota-limit").focus(); return; }
    const bytes = Math.round(n * parseFloat($("quota-unit").value));
    try {
      await fetch("/api/quotas", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ appId: appVal ? parseInt(appVal, 10) : null, period, limitBytes: bytes }),
      });
      $("quota-limit").value = "";
      loadUsagePanel(true);
    } catch { }
  });
  document.addEventListener("click", (e) => {
    const panel = $("events-panel");
    if (panel.style.display !== "none" && !panel.contains(e.target) && e.target.id !== "bell-btn" && !e.target.closest("#bell-btn"))
      panel.style.display = "none";
  });
}

/* ---------- boot ---------- */

initControls();
applyTheme();
refresh();
refreshStatus();
setInterval(refreshStatus, 30000);
