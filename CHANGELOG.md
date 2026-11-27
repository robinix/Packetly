# Changelog

All notable changes to Packetly are documented here, including technical handoff notes for the local Windows build/verification agent.

## v1.8.4 — Crisp Sculpted GlassWire-Style Graph Curves & Reliable Real App Icons

### User-facing changes
- **Restored crisp, detailed, sculpted graph curves (matching GlassWire's Traffic Monitor):**
  - Removed the `rolling_max` dilation added in v1.8.2/v1.8.3, which merged nearby bursts into wide, vague blobs and suppressed steady uploads relative to short download spikes.
  - Added clean display-bucket summing (`rebin_factor`) on longer time ranges so charts always plot ~180–340 natural bins (`5m` → `1s`, `3h` → `1m`, `24h` → `5m`, `7d` → `30m`, `30d` → `3h`, `All Time` → `1d`) with crisp 3-pass box smoothing + Catmull-Rom + honest raw display-bucket peak normalization.
  - Sustained uploads and downloads now scale proportionally (so multi-GB upload mountains appear clearly alongside download peaks on `24h` and `7d`), organic shoulders and double-peaks stay crisp with clear valleys, and Y-axis units automatically show the exact display bucket (`/s`, `/m`, `/5m`, `/30m`, `/3h`, `/d`).
- **Fixed real app icons sometimes falling back to generic placeholders:**
  - Upgraded Windows icon extraction (`IconExtractor.cs`) to use MTA thread-safe PE resource readers (`PrivateExtractIconsW` → `ExtractIconExW` → `SHGetFileInfoW` → `ExtractAssociatedIcon`) and `DrawIconEx` 32-bit ARGB rendering instead of COM-dependent shell queries and `Icon.ToBitmap()`, eliminating intermittent extraction failures and alpha corruption.
  - Added sibling executable icon lookup (`/api/icon` + `Db.FindSiblingAppIconOrPath`) so `name:*.exe` rows and auto-updated apps whose older versioned `.exe` path was removed still inherit the app's real icon automatically, plus a one-time `icon_v3_migrated` re-extraction on startup.
  - Fixed the UI's paused-selection branch so drag-selecting a time window on Traffic Monitor fetches missing app icons immediately instead of leaving generic placeholders while paused, and stopped classifying `name:*.exe` rows as unidentified system entries.

### Local agent handoff notes
- **Files changed:**
  - `packetly/src/Packetly/Core/IconExtractor.cs` — upgraded to `PrivateExtractIconsW` + `ExtractIconExW` + `SHGetFileInfoW` + `DrawIconEx` 32bpp ARGB rendering.
  - `packetly/src/Packetly/Storage/Db.cs` — added `FindSiblingAppIconOrPath(int excludeId, string? appName)`.
  - `packetly/src/Packetly/Api/AppApi.cs` — version `1.8.4`; added sibling icon/path fallback on `GET /api/icon`.
  - `packetly/src/Packetly/Program.cs` — bumped one-time icon backfill migration to `icon_v3_migrated` with sibling backfill.
  - `packetly-ui/src/main.rs` — added `rebin_factor` + display-bucket summing in `pivot()`, updated `bucket_unit` to format dynamic intervals (`/5m`, `/30m`, `/3h`, etc.), removed `rolling_max`, restored crisp 3-pass smoothing + Catmull-Rom + raw-bucket peak normalization on main and mini charts, updated `is_pseudo_app`, and added icon fetching inside the paused-selection branch + `all_apps` tail.
  - `web/app.js` — updated `rebinFactor`/`rebinData`, `bucketUnit`, `smoothSeries`, and `ingestData` in exact lockstep with `packetly-ui/src/main.rs`.
  - `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.8.4`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `dotnet build packetly/src/Packetly -c Release`
  3. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  4. Confirm `/api/status` reports `"version": "1.8.4"`.
  5. Open **Traffic Monitor** on **7 Days**, **24 Hours**, **3 Hours**, and **5 Minutes**: verify peaks are crisp, sculpted, and detailed (no wide vague blobs), verify **blue Upload mountains** rise clearly alongside magenta Download mountains on 7 Days (`/30m` axis), and verify drag-selecting a range loads real app icons inside the selection popup immediately.
  6. Open **Traffic**, **Usage**, and **Alerts**: verify real apps (including auto-updated Electron/browser apps) display their real icons cleanly after the automatic `icon_v3_migrated` startup pass.

## v1.8.3 — Cleaner Traffic Monitor Graph, Rounded Peak-Faithful Curves & Glitch-Free Settings Dialog

### User-facing changes
- **Removed stray floating red circles on the Traffic Monitor graph:** Eliminated the legacy unlabeled red event dots that floated near the top of the plot whenever an alert (such as an app version change or quota event) occurred in the visible time window. All alert history remains cleanly organized in the **Alerts** tab.
- **Smoother, rounder graph curves while keeping honest peak heights:** Upgraded the smoothing pipeline from the sharp v1.8.2 tent envelope to a wider peak-preserving rolling-max dilation followed by 3-pass cubic B-spline smoothing (`n/100`, clamp `2..120`, `1.25×` dilation, passes `1.0r / 0.90r / 0.75r`) and Catmull-Rom interpolation. Bursts now render as lush, rounded hills across `5m`, `3h`, `24h`, `7d`, and `30d` while still accurately reaching their real byte heights on the Y-axis.
- **Fixed Settings dialog first-open jump and console flash:**
  - Replaced `egui::Area::anchor(CENTER_CENTER)` (which rendered shifted on Frame 0 before snapping to center on Frame 1) with an exact centered `fixed_pos` and stable dialog geometry + constant-height rows so the dialog never jumps or resizes when opening.
  - Added `CREATE_NO_WINDOW` (`0x08000000`) to `schtasks.exe` / `powershell.exe` startup checks, added single-flight `startup_loading` / `geoip_loading` guards (fixing a bug that spawned duplicate background threads every frame while waiting), and pre-warmed both states asynchronously at launch so Settings opens instantly with zero flash. Also added `Escape` to close Settings.

### Local agent handoff notes
- **Files changed:**
  - `packetly-ui/src/main.rs` — removed bare `t.danger` circle overlay loop on Traffic Monitor plot; upgraded main chart + mini-timeline smoothing to 3-pass rounded peak-preserving curves (`div=100` / `90`, `r_dil=1.25*r`, 3-pass `moving_avg`, `clamp(1.0, 1.20)`); added `startup_loading`/`geoip_loading` to `UiFlags`, `CREATE_NO_WINDOW` in `query_startup`/`set_startup`, launch-time pre-warm thread in `main()`, and fixed centered `fixed_pos` + constant row heights + `Esc` close in `settings_dialog()`.
  - `web/app.js` — updated `smoothSeries` and `ingestData` in exact lockstep with `packetly-ui/src/main.rs`.
  - `packetly/src/Packetly/Api/AppApi.cs`, `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.8.3`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `dotnet build packetly/src/Packetly -c Release`
  3. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  4. Confirm `/api/status` reports `"version": "1.8.3"`.
  5. Open **Traffic Monitor** across `5m`, `3h`, `24h`, and `7d`: verify zero floating red circles appear near the top of the chart, and verify peaks look smoothly rounded (no sharp spikes) while still matching the left Y-axis peak rate.
  6. Launch `packetly-ui.exe` fresh and click **Settings** for the very first time: verify it appears immediately dead-center with zero position jump, zero resize flicker, and zero console window flash; press `Esc` to confirm clean dismissal.

## v1.8.2 — Accurate Graph Scale & Selection Counter, Domain-Grouped Unidentified Traffic & Distinct Fallback Icons

### User-facing changes
- **Fixed Traffic Monitor graph scale vs. selection & side-panel usage counter:**
  - **Eliminated the live-tail blind spot across rollup tiers:** The collector now seamlessly stitches unrolled live `traffic_1s` (and `60s`/`3600s`) tail buckets onto rollup queries (`3h`, `24h`, `7d`, `30d`, `All Time`), so the graph and side-panel usage totals always agree even during active transfers instead of lagging behind by 1–90 seconds (or up to an hour/day).
  - **Peak-faithful curve rendering:** Replaced heavy 3-pass box smoothing and stride sampling with a peak-preserving rolling-max envelope (`n/200`, clamp `1..48`), gentle smoothing, bin-max Catmull-Rom downsampling, and bounded normalization (`1.0–1.15×`), so narrow heavy bursts reach their exact real byte height on the left Y-axis without inflating plateaus or dropping spikes.
  - **Explicit per-interval Y-axis labels & decimal scale caps:** Left Y-axis ticks and scale cap controls now display explicit interval units (`/s`, `/m`, `/h`, `/d`) with base-1000 caps (`50 KB`–`5 GB`) matching byte formatting, making it immediately clear why a multi-interval selection total (e.g. `250 MB+` across several minutes) differs from each individual bucket's peak rate.
  - **Selection summary strip & live bottom-bar sync:** Drag-selecting a time range on the Traffic Monitor graph now shows a dedicated summary strip (`Total`, `↓ Down`, `↑ Up`, and `Peak` rate matching the left Y-axis) in the selection popup, syncs the bottom summary bar to the selected window (labeled `Selected`), and fixes a bug where the selection popup's **Hosts** list ignored the selected range and showed the last 5 minutes instead.
- **Replaced `unknown:pidNNNN` spam with domain-grouped entries:**
  - Improved live Windows process resolution (`ProcessTracker`) to immediately capture system PIDs (`0`/`4` → `System`), retain executable names even when full-path `OpenProcess` is denied, and resolve short-lived processes on demand before they exit.
  - Any remaining truly unresolved process traffic is now grouped by its remote domain (e.g., `cloudflare.com (System)`, `Local Network (System)`, or `Background Traffic`) instead of creating dozens of separate `unknown:pidNNNN` rows.
  - Automatically migrates and consolidates legacy `unknown:pid*` database rows on startup and whenever reverse DNS resolves new hostnames, merging duplicate traffic buckets and clearing noisy duplicate alerts.
- **Two distinct, polished custom fallback icons:**
  - Replaced the old generic placeholder icons with two clean, visually distinct vector icons across Traffic Monitor, Traffic, Usage, Quotas, Alerts, and App Details:
    - **Unidentified / domain-grouped system traffic:** Deep indigo-violet rounded badge with a crisp 3-node network constellation icon.
    - **Real apps without an embedded icon:** Ocean-cyan application window tile with a titlebar, status dots, and `>_` emblem.

### Local agent handoff notes
- **Files changed:**
  - `packetly/src/Packetly/Core/HostGrouping.cs` *(new)* — registrable domain extraction (with 2-part TLD & LAN handling), friendly display naming (`{domain} (System)`, `Local Network (System)`, `Background Traffic`), and `IsPseudoPath`/`AppDisplayName` helpers.
  - `packetly/src/Packetly/Collectors/ProcessTracker.cs` — thread-safe `ConcurrentDictionary`, PID `0`/`4` → `name:System`, immediate `name:<exe>` fallback on attempt 1 while retrying for full path, and on-demand `TryResolveLive(pid)`.
  - `packetly/src/Packetly/Core/AppRegistry.cs`, `IconExtractor.cs`, `TrafficWriter.cs`, `AlertEngine.cs`, `HostResolver.cs` — domain-grouped `unidentified:<group>` tracking, live DNS resolution callback hook, clean display names, and pseudo-path guards.
  - `packetly/src/Packetly/Storage/Db.cs` — `TrafficSourceSql` watermark-aware live-tail union across `traffic_60s`/`3600s`/`86400s` for all series, top-app/host, usage, and selection queries; `GetHostname`; `TotalBytesBetween`; idempotent atomic `ConsolidateUnidentifiedApps()`; updated `UnseenEventCount()`.
  - `packetly/src/Packetly/Program.cs` — startup + post-DNS `ConsolidateUnidentifiedApps()` consolidation with `AppRegistry.InvalidatePaths` and 15s aggregator cadence.
  - `packetly/src/Packetly/Api/AppApi.cs` — version `1.8.2`; exact `totalIn`/`totalOut` fields on `/api/applist` (backward-compatible).
  - `packetly-ui/src/main.rs` — peak-preserving `rolling_max` + bin-max `smooth`, interval-labeled Y-axis (`fmt_b_unit`) & decimal scale ladder, fixed `/api/hosts?range=custom` on graph selection, selection popup summary strip (`Total`, `↓ Down`, `↑ Up`, `Peak`) & bottom-bar selection totals, `clean_app_display`, and `AppIconKind` (`draw_unidentified_proc_icon` + `draw_no_icon_app`) wired across all 7 icon sites.
  - `web/app.js` — chart pipeline, decimal scale ladder, and interval-labeled Y-axis updated in exact lockstep with `packetly-ui/src/main.rs`.
  - `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.8.2`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `dotnet build packetly/src/Packetly -c Release`
  3. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  4. Confirm `/api/status` reports `"version": "1.8.2"` and ports `8912`/`8913` remain unchanged.
  5. **Graph scale & selection counter:** Run a short burst download on `5m`, `3h`, and `24h`; verify the peak visually hits the exact left Y-axis value (labeled `/s`, `/m`, `/h`, or `/d`), drag-select around the burst, and verify the selection popup shows `Total` / `↓` / `↑` / `Peak` matching the chart and bottom summary bar (`Selected`), and its **Hosts** tab shows hosts strictly from the selected window.
  6. **Domain grouping & migration:** Open **Traffic**, **Usage**, and **Alerts**; confirm zero `unknown:pidNNNN` rows remain (legacy rows merged into domain groups like `example.com (System)` or `Background Traffic`), and verify the **indigo network-constellation badge** appears for unidentified/system-domain entries while the **cyan `>_` window tile** appears for real `.exe` apps that lack an embedded icon.

## v1.8.1 — Fix Desktop & Start Menu Shortcut Recreation and Launch Target

### User-facing changes
- **Stop recreating deleted or moved desktop shortcuts on startup:** Packetly no longer rewrites the `Packetly Dashboard.lnk` Desktop and Start Menu shortcuts on every collector boot/logon. Shortcuts are now created only once during installation (`--install-autostart`), so moving or deleting the desktop icon stays permanent.
- **Fix broken dashboard shortcut launch:** Changed the shortcut target from `schtasks.exe /run /tn Packetly` (which failed silently when clicked non-elevated against a `RunLevel Highest` task) to launch `packetly-ui.exe` directly with its proper working directory and app icon.

### Local agent handoff notes
- **Files changed:**
  - `packetly/src/Packetly/Program.cs` — removed `CreateDashboardShortcuts(port)` from `--serve` startup, moved `CreateDashboardShortcuts()` into `--install-autostart` (only when task registration succeeds), and switched the shortcut target to `NativeUiPath()` (`packetly-ui.exe`) with working directory + icon set.
  - `packetly/src/Packetly/Api/AppApi.cs`, `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.8.1`.
- **Verify locally on Windows:**
  1. `git pull`
  2. Build and install the `v1.8.1` release zip (or `dotnet build packetly/src/Packetly -c Release` + `cargo build --release --manifest-path packetly-ui/Cargo.toml`).
  3. Confirm `/api/status` reports `"version": "1.8.1"`.
  4. Verify the installed Desktop/Start Menu shortcut targets `packetly-ui.exe` directly and opens the dashboard cleanly on click.
  5. Delete or move the Desktop shortcut, restart the `Packetly` scheduled task (or reboot), and confirm the deleted desktop icon is not recreated.

## v1.8.0 — Modern Alerts Tab Overhaul, Application Info Monitor & App Details Fix

### User-facing changes
- **Modern, detailed Alerts tab overhaul:** Replaced the barebones flat alert list with a sleek GlassWire-inspired timeline view matching Packetly's deep-navy (`#121a2a`) Elite design language:
  - **Top filter toolbar:** Pill-shaped **Severity & Alert Type dropdown** (`All Alerts`, `Important`, `Logs`, `First network activity`, `Application info monitor`, `Quota monitor`) with live per-category counts and checkmarks, **Time Range dropdown** (`All Time`, `Day`, `Week`, `Month`), **`Mark all as read`** pill button, and live **`Search`** box matching app name, path, version, publisher, or category.
  - **Smart severity filtering (default `Important`):** Ephemeral `unknown:pid*` / `name:*` entries are separated into **`Logs`** so the default **`Important`** view and the nav bar unread counter (`Alerts (N)`) stay clean and focused on real applications, executable/version changes, and quota alerts.
  - **Date-grouped alert cards:** Alerts are grouped under **`Today`**, **`Yesterday`**, and formatted calendar dates inside elevated rounded cards with hairline row dividers. Each row shows an unread red indicator dot (for events newer than `seenId`), a crisp circular vector severity/type badge, the inline app icon (or clean default app-window / globe badge), rich bold-highlighted messages (e.g., **Firefox** version changed from **160.0.1.575** to **161.0.0.3**.), and right-aligned two-line metadata (monitor category + exact local clock time like `5:33 am`).
  - **Expandable details & quick App Details:** Clicking any alert row expands an inline drawer with full executable path, version, publisher, exact timestamp, and an **`App Details`** button (double-clicking also opens App Details directly).
- **Application Info Monitor (collector):** The `.NET 8` collector now tracks executable version, publisher, file size, and last-modified timestamp for `.exe` apps (seeding existing rows silently on upgrade without alert spam) and raises rich alerts when an app's **version**, **executable binary**, or **publisher** changes.
- **Fixed App Details popup closing immediately:** Fixed a polling-loop bug where clicking any app in **Traffic** or **Usage** opened the App Details popup only for the next background refresh tick (`150ms`–`750ms` later) to overwrite `app_detail` with `None` and shut it immediately. Also fixed closing the popup while paused on a graph selection, added instant open/close fast-path fetching, added `Escape`-to-close, aspect-fit header icons, deterministic Hosts tie-breaking, and human-readable per-app alerts inside the popup's **Alerts** tab.

### Local agent handoff notes
- **Files changed:**
  - `packetly/src/Packetly/Storage/Db.cs` — added `MigrateAppsMetadataColumns()` (`version`, `file_size`, `file_mtime` columns on `apps`), new event type constants (`EvAppVersionChanged = 5`, `EvAppExeChanged = 6`, `EvAppPublisherChanged = 7`), `GetMonitoredApps`, `UpdateAppMetadata`, `GetEventsForApp`, and updated `UnseenEventCount()` to exclude low-severity `unknown:*`/`name:*` new-app logs.
  - `packetly/src/Packetly/Core/AlertEngine.cs` — added periodic Application Info Monitor (`CheckAppChanges`, 120s interval, quiet baseline seeding on first scan) detecting version, binary, and publisher changes; enriched `OnAppCreated` with version/publisher metadata.
  - `packetly/src/Packetly/Program.cs` — moved `alerts.OnAppCreated` inside `Task.Run` alongside `IconExtractor.TryExtract` so executable disk reads never stall the `Db` lock.
  - `packetly/src/Packetly/Api/AppApi.cs` — version `1.8.0`; added `seenId` and raised limit (`500` default / `1000` max) on `GET /api/events`; added per-app `alerts` array to `GET /api/appinfo` (fully backward-compatible).
  - `packetly-ui/src/main.rs` — added `AlertFilter`, `AlertRange`, `EventRow.app_id`, `AppDetail.alerts`, `Snapshot.events_seen_id`, `parse_event_row`, `ParsedAlert`/`parse_alert`, date/clock formatters, custom vector badge/toolbar icons, and `segoe_ui_bold` (`FontFamily::Name("bold")`) registration; completely overhauled `alerts_tab`; fixed `active_detail` persistence + instant open/close fast-path in the background thread (and added `fr.events` `app_id`s to icon fetcher); polished `app_detail_popup` with `Escape` close, `paint_icon_fit`, host tie-breaking, and formatted per-app alerts.
  - `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.8.0`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `dotnet build packetly/src/Packetly -c Release`
  3. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  4. Click multiple apps in **Traffic**, **Usage**, **Alerts** (via `App Details` or double-click), and paused **Traffic Monitor** selection: verify the App Details popup opens instantly, stays open stably across refreshes, shows formatted alerts on its **Alerts** sub-tab, and closes cleanly via `✕` or `Esc`.
  5. Open **Alerts** tab across **Elite (navy)**, **Dark**, and **Light** themes: verify `Important` default view hides `unknown:pid*` noise (visible under `Logs` / `All Alerts`), test both floating dropdown menus (`Important ▾` with live counts, `All Time ▾` with `Day`/`Week`/`Month`), test live `Search`, test expanding rows, and click **`Mark all as read`** to confirm unread red dots and the nav badge clear immediately.

## v1.7.4 — Modern Usage Dashboard, Stable Tie-Breaking & Reliable Fast App Icons

### User-facing changes
- **Modern, responsive Usage tab overhaul:** Rebuilt the Usage tab to match Packetly's deep-navy (`#121a2a`) Elite design language with:
  - A **4-card KPI summary strip** (`Total Traffic` with live Upload/Download ratio bar, `Download`, `Upload`, and clickable `Top Application` with icon & share %).
  - A full-height **Applications Breakdown** card displaying up to 100 apps (previously truncated at 15 and capped at `380px` height) with ranked `38px` rows, inline icons, share percentages, split **blue Upload + magenta Download** proportional bars, live top-bar search filtering, sorting (`Total`, `Down`, `Up`, `Name`), and detailed hover tooltips.
  - A custom vector **Usage Distribution Donut Chart** (`Top 5 + Other`) with centered total and clickable interactive legend.
  - A redesigned **Data Quotas** manager with nested status cards (color-coded green / amber / red progress bars, app/global icons, quick `✕` delete) and an inline quota creator supporting `Enter` to submit and input validation.
  - Automatic responsive stacking on narrower windows (`< 860px`).
- **Fixed Traffic tab row flickering on tied usage:** Eliminated rapid back-and-forth row swapping when two apps, hosts, traffic types, or countries have identical byte totals by adding strict deterministic tie-breaking (by display name + ID) across both the UI sorters and SQLite queries.
- **Faster, more reliable & cleaner app icons:**
  - Replaced legacy `Icon.ExtractAssociatedIcon` with Windows Shell **`SHGetFileInfoW`** 32-bit ARGB extraction (`32×32` normalized PNGs with clean alpha transparency and blank-icon validation), plus a one-time automatic background upgrade of previously cached icons.
  - Added **on-demand icon extraction** directly inside `/api/icon` so newly seen apps return their icon on the very first request instead of 404-ing until backfill finishes.
  - Replaced the UI's permanent `icon_failed` blacklist with exponential retry backoff (reset on tab/range switch), prioritized top visible apps first, raised per-tick icon batching to `40` with fast `150ms` draining while icons are pending, added byte-hash texture invalidation, and preserved icon aspect ratios.

### Local agent handoff notes
- **Files changed:**
  - `packetly/src/Packetly/Core/IconExtractor.cs` — new `SHGetFileInfoW` (`SHGFI_ICON | SHGFI_LARGEICON`) + `DestroyIcon` extractor with `ExtractAssociatedIcon` fallback, `32×32` `Format32bppArgb` PNG normalization, and transparent-bitmap guard.
  - `packetly/src/Packetly/Storage/Db.cs` — added `AllExeApps`, `UsageSplitByApp` (returning separate `In`/`Out` totals per app across any rollup tier), and deterministic secondary `ORDER BY` tie-breakers (`a.id ASC` / `t.host ASC`) on `AppsInWindow`, `TopApps`, `TopHosts`, `AppHostsBetween`, and `AppsHostsBetween`.
  - `packetly/src/Packetly/Api/AppApi.cs` — version `1.7.4`; extended `/api/usage` (backward-compatible) with `inBytes`/`outBytes` totals and per-app split + deterministic `.ThenBy(...)` tie-breakers + tier selection (`TierFor`) for custom/range windows; added on-demand `IconExtractor.TryExtract` inside `/api/icon`.
  - `packetly/src/Packetly/Program.cs` — async `Task.Run` icon extraction on `AppCreated` (no longer holds `Db` lock), 1s startup backfill with one-time `icon_v2_migrated` re-extraction of existing `.exe` icons, and periodic 5-minute backfill for missing icons.
  - `packetly-ui/src/main.rs` — added `RowIcon::sort_key`, `tie_break_traffic`, `UsageAppRow`, `fnv1a64`, `paint_icon_fit`, `usage_sort`, `usage_filter`, `quota_err`, `icon_tex_hash`; upgraded background icon loader (priority-preserving dedup, 40/tick + `150ms` fast drain, exponential retry backoff, includes quota/detail/selection apps); enabled top-bar Filter on `Tab::Usage`; completely overhauled `usage_tab`.
  - `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.7.4`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `dotnet build packetly/src/Packetly -c Release`
  3. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  4. Open **Usage** tab across `Today`, `This week`, `5 Minutes`, and `All Time` in **Elite (navy)**, **Dark**, and **Light** themes: verify KPI cards, split Download/Upload bars, vector donut chart, full-height scrollable app list, live Filter/Sort, App Details click-through, and creating/deleting quotas (including pressing Enter and testing invalid input).
  5. Open **Traffic** tab when multiple rows share identical totals (e.g., `150 B` hosts/countries or tied apps) and confirm zero row flickering/swapping across refreshes.
  6. Verify app icons appear quickly (<1s) across Traffic, Usage, and Quotas, look crisp without black alpha artifacts, and automatically repair previously broken cached icons after first startup (`icon_v2_migrated`).

## v1.7.3 — Polished Responsive Traffic Panel Overhaul

### User-facing changes
- **GlassWire-inspired 4-column Traffic panel:** Replaced the sparse, multi-line stacked layout with a unified deep-navy (`#121a2a`) card containing 4 crisp equal-width columns (**Apps**, **Hosts**, **Traffic Type**, **Countries**) with subtle vertical dividers, item count headers, and an automatic **2×2 responsive grid** fallback on narrower windows.
- **Uniform compact single-line rows & inline icons:** Removed the colored bullet dots and stacked flag sub-lines. Every row is now a crisp `32px` single-line entry with ellipsized labels, aligned right-hand byte totals, hover highlights, and inline left icons — real app icons (or clean monogram badges) for **Apps**, crisp bordered country flags / painted **Local Network (LAN)** badges / wireframe **Globe** icons for **Hosts** & **Countries**, and clean flush labels for **Traffic Type**.
- **Split Upload/Download bars on every column:** Replaced random per-row colors with Packetly's signature dual-color proportional bar (**blue Upload + magenta Download**) along the bottom of every row across all 4 columns, including **Traffic Type** and **Countries** (which now preserve exact in/out byte splits), plus a live hover tooltip showing exact `↓ Download`, `↑ Upload`, and `% of top`.
- **Full country & protocol names + expanded flag coverage:** Mapped 60+ ISO country codes (`IR` → `Iran`, `US` → `United States`, `DE` → `Germany`, etc.), expanded procedural flags (adding `BE`, `SG`, `KR`, `AU`, `BR`, `MY`, `AE`, `HK`, `PK`, `CH`, `CZ`, `TW`, `IL`, `MX`, `ZA`, `ID`, `TH`, `VN`, `SA`, `NZ`, `AR`, `CL`, `SK`, `HR`, `SI`), and expanded protocol labels (`HTTPS (SSL/TLS)`, `Domain Name System (DNS)`, `Multicast DNS (mDNS)`, etc.).
- **Live search filter, larger lists & bottom summary bar:** Turned the top-bar `Filter` into a live search box that filters all 4 columns instantaneously, increased Traffic tab list limits (`100` apps / `120` hosts) and icon-fetch batching (`12`/tick), and brought the live bottom summary bar (ring gauge + Down/Up rates + WAN/LAN bars + mini-timeline) onto the Traffic tab.

### Local agent handoff notes
- **Files changed:**
  - `packetly-ui/src/main.rs` — added `RowIcon`, `TrafficRow`, `sort_traffic_rows`, `group_totals`, `country_name`, `traffic_type_label`, `is_local_host`, `draw_lan_icon`, `draw_globe_icon`, expanded `flag_known`/`draw_flag`; added `traffic_filter` to `PacketlyApp` & `range_bar`; increased Traffic tab `/api/apps` (`limit=100`), `/api/hosts` (`limit=120`), and icon batch (`12`); rendered `summary_bar` on `Tab::Traffic`; overhauled `traffic_tab` with custom-painted `32px` rows, split Upload/Download bars, hover tooltips, and 4-col / 2×2 responsive layout.
  - `packetly/src/Packetly/Api/AppApi.cs`, `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.7.3`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  3. `dotnet build packetly/src/Packetly -c Release`
  4. Open the **Traffic** tab in **Elite (navy)** theme: verify all 4 columns align cleanly inside the `#121a2a` card with inline flags/app icons/LAN badges, full country names, split blue+pink bars, hover highlights + tooltips, clickable App Details popup, live Filter typing, bottom summary bar, and responsive 2×2 switch when resizing the window narrower than `820px`. Also spot-check **Dark (AMOLED)** and **Light** themes.

## v1.7.2 — Rich Web-Matched Elite Theme Graph Colors & Deep Navy Background

### User-facing changes
- **True deep navy graph background (`#121a2a`):** Disabled `egui_plot`'s default canvas frame (`show_background(false)` + `show_axes([false, false])`), which had been covering the deep navy `#121a2a` card with the lighter `#1e283e` `extreme_bg_color` box and grey border. Both the main graph and bottom mini-timeline now sit on rich `#121a2a` matching the web preview.
- **Rich vertical linear-gradient area fills:** Replaced `egui_plot`'s faint flat fill with a custom GPU `egui::Mesh` vertical linear gradient (`0.34 -> 0.07` opacity on the main chart, `0.32 -> 0.08` on the mini-timeline) using exact sRGB-premultiplied alpha (`srgb_alpha`) matching HTML5 Canvas 2D compositing without `egui`'s low-alpha gamma washout.
- **Crisp, subtle gridlines behind curves & clean axis strip:** Moved vertical (`0.022` white) and horizontal (`0.035` white) gridlines behind the curve fills/strokes using exact sRGB alpha (fixing the overly bright grey horizontal bars caused by linear-to-sRGB premultiplication), placed `1.7px` Download (`#e050c8`) and Upload (`#6e93e8`) strokes cleanly on top, and gave X-axis timestamps a dedicated `24px` bottom strip below `y = 0` so labels never collide with low traffic curves.
- **Mini-timeline polish:** Removed the stray raw-timestamp hover readout (`x = 1795211800.0`) on the bottom mini-timeline and matched its rich `#121a2a` background and vertical gradient fills.

### Local agent handoff notes
- **Files changed:**
  - `packetly-ui/src/main.rs` — added `srgb_alpha()` and `paint_gradient_fill()` (`egui::Mesh` triangle strip); tuned `Theme::elite()` (`plot_bg: rgb(18, 26, 42)`, `grid: srgb_alpha(WHITE, 0.035)`, `axis: rgb(132, 143, 168)`, `danger: rgb(230, 100, 90)`) and `Theme::dark()`/`Theme::light()` grids; updated `graph_tab` (`Plot::show_background(false)`, `show_axes([false, false])`, 4-layer painter pipeline: gridlines -> gradient fills -> `1.7px` strokes -> axis labels with `24px` bottom strip) and `summary_bar` mini-timeline direct gradient renderer.
  - `packetly/src/Packetly/Api/AppApi.cs`, `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.7.2`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  3. `dotnet build packetly/src/Packetly -c Release`
  4. Verify in **Elite (navy)** theme that the graph background is deep navy `#121a2a` (not pale `#1e283e`), gridlines are ultra-subtle behind the curves, pink/blue peaks have rich vertical gradient glow fading to dark navy at the baseline, X-axis timestamps sit cleanly below `y = 0`, drag-selection still works over the plot, and hovering the bottom mini-timeline shows no raw coordinate tooltip. Also spot-check **Dark (AMOLED)** and **Light** themes.

## v1.7.1 — Lock-Steady 1.0x Live Scrolling (Zero Hiccups, Rubberbanding, or Right-Edge Jumps)

### User-facing changes
- **Exact 1.000x constant-speed scrolling (no speed surges or rubberbanding):** Replaced the poll-chasing servo (which sped up to 2.2x when polled integer seconds jumped +2s and stalled when hitting the ceiling) with a pure monotonic continuous wall clock locked to real time.
- **Zero right-edge jumps or popping:** Increased the live `5 Minutes` lookahead buffer to `7.0s` behind real time (`285s` visible span) and sped up `5m` polling to `750ms`. Because the 3-pass box filter (`r=2,1,1`) + Catmull-Rom spline reaches `5s` ahead of the right edge, every visible pixel now sits strictly inside already-closed, SQLite-flushed buckets (`<= now - 2s`) — already-formed hills glide onto the right edge pixel-by-pixel without ever mutating on screen.
- **No horizontal stretching during idle periods:** Fixed `pivot()` so bounded time ranges (`5m`, `3h`, `24h`, `7d`, `30d`, `Custom`) always keep an invariant `x0..x1` grid instead of trimming idle space at the left edge (leading-space trimming now applies only to `All Time`).
- **True VSync frame pacing & calmer Y-axis scaling:** Replaced `request_repaint_after(16ms)` (which stuttered on Windows' 15.6ms timer granularity) with direct `ctx.request_repaint()` while actively animating, excluded the unflushed 2-second tail from peak normalization (`p.y_max` / `rendered_max`), softened Y-scale easing, and removed redundant mini-timeline cache invalidation.

### Local agent handoff notes
- **Files changed:**
  - `packetly-ui/src/main.rs` — monotonic wall-clock anchor (`clock_unix0`/`clock_inst0` with automatic >8s sleep/wake re-anchor), `7.0s` closed-bucket right buffer (`vis_span = 285s`), `Range::M5` poll interval `750ms`, invariant `pivot()` grid for bounded ranges + unflushed-tail exclusion on 1s peak calculation, `traffic_ver` last-timestamp hash, direct `ctx.request_repaint()` VSync pacing, and removal of redundant `cache.mini = None` wipe in `graph_tab`.
  - `web/app.js` — exact 1:1 mirror of the monotonic wall-clock glide, `7.0s` buffer (`285s` visible span), `750ms` live poll cadence, tail-excluded peak normalization, and wall-clock-synced `5m` demo ticker.
  - `packetly/src/Packetly/Api/AppApi.cs`, `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.7.1`.
- **Verify locally on Windows:**
  1. `git pull`
  2. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  3. `dotnet build packetly/src/Packetly -c Release`
  4. Watch `5 Minutes` live for 60+ seconds during mixed idle and burst traffic: verify steady 1.0x right-to-left motion with zero rubberbanding, zero hiccups, and zero vertical jumps near the right edge when new bursts enter. Also verify drag-selection freezes live motion cleanly and clicking to clear resumes cleanly.

## v1.7.0 — Detailed Graphs & Smooth 60 FPS Live Scrolling

### User-facing changes
- **Sharper, more detailed graphs:** Tightened 3-pass Gaussian smoothing from `n / 20` (which blurred nearby peaks into wide blobs on `24h`/`3h`/`7d`) to **`n / 130` (clamped `1..120`)** so closely spaced bursts and valleys stay distinct while remaining smoothly rounded.
- **Smooth 60 FPS right-to-left live scrolling:** Replaced discrete 2-second jumps on `5 Minutes` with a continuous servo-locked 60 FPS viewport glide (~2.25 s lookahead behind `p.x1`, capped `<= p.x1 - 1.0` so closed buckets slide seamlessly onto the right edge with no drop-to-zero tail or popping). Longer ranges ease smoothly on new buckets and drop to 0% idle GPU once settled.
- **Smooth Y-axis scale transitions:** Auto-scale ceiling (`anim_y_top`) and peak-normalization multiplier (`anim_k`) now ease smoothly instead of snapping vertically in 1 frame (still snapping immediately when switching ranges).
- **Subtle vertical time gridlines & dual-series mini-timeline:** Added faint vertical gridlines aligned with local-midnight time ticks, fixed `Line::fill(0.0)` baseline with slightly richer dark-theme fills (`0.56`) and crisp `1.6px` strokes, and upgraded the bottom mini-timeline to sharper smoothing (`n / 110`) with both Upload and Download silhouettes.
- **Faster live polling:** Traffic Monitor now queries aggregate `/api/traffic?{q}` (`data.totals`) instead of `&breakdown=apps` (10x–30x smaller payloads), skips `/api/apps` and `/api/hosts` on the Graph tab, throttles `/api/status` & `/api/events` to every 5th tick, polls `5m` on a fetch-compensated `1000 ms` cadence, and loads the first frame immediately on launch (`1 ms`).

### Local agent handoff notes
- **Files changed:**
  - `packetly-ui/src/main.rs` — full desktop implementation (`smooth` stride resampling above 600 pts -> 480 control pts -> Catmull-Rom 1400 pts; `clip_and_scale` viewport slicing; `traffic_ver`; lightweight `fetch_traffic` using `data.totals`; `fetch_snapshot` Graph-tab fast path; 60 FPS `anim_x1`/`anim_y_top`/`anim_k` loop in `graph_tab`; dual-series `mini_timeline` in `summary_bar`).
  - `web/app.js` — exact 1:1 mirror of the chart pipeline, joint peak normalization, vertical gridlines, dual-series mini-timeline, and 60 FPS live/demo glide.
  - `packetly/src/Packetly/Api/AppApi.cs` — version bumped to `1.7.0` (no endpoint schema changes; `/api/traffic` already served `data.totals`).
  - `packetly-ui/Cargo.toml`, `packetly-ui/Cargo.lock`, `package.json` — version bumped to `1.7.0`.
  - `.github/workflows/release.yml` — now also auto-cuts a GitHub Release on pushes to `main` when `AppApi.cs` bumps to an untagged version (existing `v*` tag pushes remain supported).
- **Verify locally on Windows:**
  1. `git pull`
  2. `cargo build --release --manifest-path packetly-ui/Cargo.toml`
  3. `dotnet build packetly/src/Packetly -c Release`
  4. Verify on `24 Hours` that nearby bursts/valleys stay distinct and crisp, and on `5 Minutes` that the chart glides smoothly right-to-left without 2-second stutters or right-edge drop-to-zero, and that drag-selection cleanly pauses scrolling.

## v1.6.0 — Wall-Clock Time Axis & Calendar Custom Picker
- Wall-clock local-midnight anchored x-axis ticks (`time_ticks`) with denser intervals (`5m` 20s, `3h` 10m, `24h` 1h, `7d` 12h, `30d` 2d, `All time` 8d).
- Month calendar popup under `Custom` for single-day or two-click date ranges across Traffic Monitor, Traffic, and Usage tabs.
