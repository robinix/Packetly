# Changelog

All notable changes to Packetly are documented here, including technical handoff notes for the local Windows build/verification agent.

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
