# Packetly Web

Browser **replica of the desktop app's Traffic Monitor screen**, used for hosted
previews (Google AI Studio) and served by nothing else. Zero dependencies,
plain HTML/CSS/JS.

## Fidelity contract (read before editing)

`assets/reference-traffic-monitor.png` and `assets/reference-30d.png` are real
screenshots of the Windows app. `web/index.html` + `web/styles.css` replicate
that layout: magenta title bar → nav (Traffic Monitor / Traffic / Usage /
Alerts + settings) → Filter row with the period combo → Auto-scale/cap button →
navy plot card with in-chart y labels → bottom panel (ring gauge, down/up
rates, WAN/LAN bars, bare mini-timeline strip). Keep every change consistent
with those screenshots — do not redesign the layout.

The chart pipeline (3-pass box smoothing → Catmull-Rom → peak normalization →
raw-peak y-scale) is a direct port of `packetly-ui/src/main.rs`; hill shapes
must keep matching the desktop.

## Run

```bash
npm run dev      # or: bun run dev  (root package.json)
# -> http://localhost:5173
```

The server honors the `PORT` env var for hosted environments.

## Data modes

- **Demo** (default in hosted previews): seeded, realistic mock traffic. The
  hosted iframe cannot reach your PC, so live data is impossible there.
- **Live** (automatic): if the page is served from the collector itself
  (`http://127.0.0.1:8912`, same origin as `/api/*`) it polls
  `/api/traffic`, `/api/apps`, `/api/hosts` every 3 s and renders real data.

## Relationship to the desktop app

This folder is fully additive. The desktop app is:

- `packetly/` — .NET 8 ETW collector + REST API
- `packetly-ui/` — Rust egui window
- `.github/workflows/release.yml` — builds those two on `v*` tags and attaches
  the zip to a GitHub Release

Nothing here participates in that pipeline, so edits made in AI Studio can be
committed and pulled without touching the Windows build. Changes to look &
feel should ultimately be mirrored into the egui UI by a human — the web
replica is a design surface, not a replacement.
