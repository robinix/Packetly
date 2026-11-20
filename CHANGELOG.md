# Changelog

All notable changes to Packetly are documented here, including technical handoff notes for the local Windows build/verification agent.

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
