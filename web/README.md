# Packetly Web

Browser version of the Packetly dashboard. Zero dependencies, plain HTML/CSS/JS —
this is what renders in hosted previews (Google AI Studio) and what the desktop
collector can serve directly.

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

The chart pipeline (3-pass box smoothing → Catmull-Rom → peak normalization →
raw-peak y-scale) is a direct port of `packetly-ui/src/main.rs`, so hills look
identical to the Windows app.

## Relationship to the desktop app

This folder is fully additive. The desktop app is:

- `packetly/` — .NET 8 ETW collector + REST API
- `packetly-ui/` — Rust egui window
- `.github/workflows/release.yml` — builds those two on `v*` tags and attaches
  the zip to a GitHub Release

Nothing here participates in that pipeline, so edits made in AI Studio can be
committed and pulled without touching the Windows build.
