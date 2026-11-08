# GWLite — a lightweight DIY GlassWire-style network monitor (Windows)

Phase 5 complete — the full GlassWire-style monitor, minus firewall,
network scanning and resource monitor:

- Collector: real-time ETW kernel-network events, per-app attribution,
  no-admin TCP polling fallback
- Storage: SQLite WAL, tiered 1s/60s/hourly/daily with watermarked rollups
  and retention (48 h / 62 d / 400 d / forever)
- API: /api/status, live, traffic, apps, hosts, usage, quotas, events,
  icon, settings
- Dashboard: live chart, 6 ranges, per-app drill-down, hostnames + country
  flags, app icons, usage & quotas card, alerts bell, light/dark/auto themes
- Tray icon (open dashboard / pause collection / exit) on its own STA thread
- Auto-start: `gwlite --install-autostart` registers a logon scheduled task
  with highest privileges (remove with `--uninstall-autostart`)
- Quotas: per-app or global, daily/weekly/monthly local-time periods;
  75/90/100 % alerts fire once per period and survive restarts
- GeoIP: strictly opt-in (off by default, toggle in the Hosts card) — when
  enabled, remote-host IPv4s are batched to ip-api.com; direct access falls
  back to the local SOCKS proxy on 127.0.0.1:10808 if blocked

Note: the collector needs the **.NET 8 Desktop Runtime** (x64) installed on
the machine — download it from https://dotnet.microsoft.com/download/dotnet/8.0
(or point the `GWLITE_DOTNET_ROOT`-style launcher at any local runtime).

## Run the monitor

Double-click **GWLite Dashboard** (Desktop or Start Menu) — a native Rust
window (egui + egui_plot) reading the collector API. The monitor itself
starts automatically at logon (scheduled task "GWLite", highest privileges
— no UAC prompt).

Binaries:
- Collector/service: `src\GWLitein\Release
et8.0-windows\gwlite.exe`
  - no args → serve mode + tray icon (pause/exit in the tray menu)
  - `--smoke` → console smoke test
  - `--install-autostart` / `--uninstall-autostart` → manage the logon task
  - `--db <path>` (default %LOCALAPPDATA%\GWLite\gwlite.db), `--port`
    (default 8912), `--mode auto|etw|poll`
- Native UI: `gwlite-ui/target/release/gwlite-ui.exe` (Rust, egui; source in
  `gwlite-ui/`), installed to %LOCALAPPDATA%\GWLite\gwlite-ui.exe

The tray double-click and second-instance path open the native window;
without it they fall back to an Edge app window, then the default browser.
Data lives in %LOCALAPPDATA%\GWLite\.

## API

| Endpoint | Meaning |
|---|---|
| `/api/status` | collector state, row counts, DB size |
| `/api/live?seconds=300` | per-second series + per-app totals from memory |
| `/api/traffic?range=5m\|3h\|24h\|30d\|unlimited\|custom&from=&to=&app_id=&breakdown=apps` | time series from the matching tier |
| `/api/apps?range=...` | per-app totals for a window |
| `/api/hosts?range=...&limit=50` | top remote hosts (hostname when reverse DNS succeeds) |
| `/api/usage?period=today\|week\|month` | per-app usage since the local-time period start |
| `/api/quotas` (GET/POST/DELETE) | per-app or global quotas, daily/weekly/monthly |
| `/api/events` + `/api/events/seen` | alert feed (new app, 75/90/100 % quota) + unread tracking |

Tier selection: ≤3 h → 1 s table, ≤3 d → 60 s, ≤90 d → hourly, else daily.
Rollups run on 30 s cycles with per-tier watermarks (keyed writes, so a
crash between rollup and watermark cannot duplicate rows). Retention:
1 s data 48 h, 60 s data 62 days, hourly 400 days, daily forever.

## Layout

- `Collectors/EtwNetworkCollector.cs` — NT Kernel Logger (NetworkTCPIP), real-time, TCP+UDP, v4+v6
- `Collectors/TcpOwners.cs` — live TCP owner-table snapshot (attribution fallback)
- `Collectors/PollCollector.cs` — no-admin fallback: GetExtendedTcpTable + GetPerTcpConnectionEStats diffs (TCP only)
- `Collectors/ProcessTracker.cs` — PID → image path map (Toolhelp snapshot + QueryFullProcessImageNameW)
- `Core/TrafficRing.cs` — per-second buckets keyed by (pid, host, port, proto)
- `Core/TrafficWriter.cs` — event filter → ring → SQLite flush, deferred pid→app resolution, live snapshots
- `Core/Aggregator.cs` — tier rollups with watermarks + retention pruning
- `Core/HostResolver.cs` — throttled reverse DNS into the hosts table
- `Core/AppRegistry.cs` — path → app_id cache over SQLite
- `Storage/Db.cs` — schema, queries, rollups, prunes, metadata
- `Api/AppApi.cs` — REST endpoints
- `Program.cs` — `--serve` (monitor + API) and smoke-test modes

Self traffic and loopback are excluded.

## Attribution notes (verified on this machine)

- The Microsoft-Windows-Kernel-Network manifest puts the owning **PID in
  payload field 0**; the collector parses raw payloads per the manifest
  layouts (ids 10/11/42/43 for IPv4, 26/27/58/59 for IPv6) rather than
  trusting the ETW event header.
- PID → app resolution happens at flush time (1 s after the events), so
  freshly spawned processes attribute correctly.
- On machines with a transparent kernel-level relay (WireSock/v2rayN-style
  proxies), flows to intercepted destinations appear under a PID that belongs
  to no live process. GWLite resolves what it can (live TCP owner table),
  merges relay copies of a resolved flow, and records the remainder under an
  `unknown:pidN` app so bytes are never lost. On unproxied paths attribution
  is exact: per-app byte counts matched the wire payloads to the byte.
- Verification method: controlled downloads while diffing
  `Get-NetAdapterStatistics`; per-app totals matched the downloaded payload
  sizes exactly.
