# AGENTS.md — guidance for AI coding agents (Google AI Studio, etc.)

Packetly is a **Windows desktop network monitor**. This repo is read by hosted
AI agents, so this file explains what is real, what is preview-only, and where
changes belong.

## What is the actual product

| Path | What it is |
| --- | --- |
| `packetly/` | .NET 8 ETW collector (`Packetly`), REST API on `127.0.0.1:8912` |
| `packetly-ui/` | Rust + egui desktop UI (`packetly-ui`) — the app window |
| `.github/workflows/release.yml` | Builds both on `v*` tags, attaches the install zip |
| `scripts/install.ps1`, `INSTALL.txt` | User-facing installer pieces |

**The desktop app is the product.** It ships to users via GitHub Releases.

## What is preview-only

| Path | What it is |
| --- | --- |
| `web/` | Static browser replica of the Traffic Monitor screen for hosted previews |
| `package.json` (root) | Zero-dep manifest so preview panes can run `bun install` + a dev server |

`web/` exists ONLY so agents without access to a Windows desktop can see and
reason about the app's current look. It runs on demo data in the cloud and on
live data only when served from the collector itself. It is **not** the
product, it is not shipped to users, and `release.yml` never touches it.

## The rule

> **Work on the actual desktop app, not the demo.**

- UI/UX changes belong in `packetly-ui/src/main.rs` (egui). API/collector
  changes belong in `packetly/src/Packetly/`.
- A hosted environment that cannot compile Rust/.NET (like AI Studio's cloud
  sandbox) should use `web/` to **prototype and describe** the change — then
  state clearly that it must be ported to the egui app. Do not treat a
  web/ edit as a shipped feature, and do not keep growing web/ with features
  the desktop app does not have.
- When prototyping in `web/`, keep it consistent with
  `assets/reference-traffic-monitor.png` / `assets/reference-30d.png` (real
  screenshots) and the fidelity contract in `web/README.md`. The chart
  pipeline in `web/app.js` mirrors `packetly-ui/src/main.rs` — change both or
  neither.

## Constraints (do not break these)

- Never modify the release pipeline (`release.yml`) unless the task is
  explicitly about releases; releases are only ever cut by pushing a `v*` tag.
- Never commit runtime data: `*.db`, `service.log`, `ui-prefs.txt`, local
  tools, screenshots outside `assets/`.
- Windows-only code paths: the collector uses ETW (`NetworkTCPIP`), the UI
  targets Windows (tray icon, scheduled-task autostart, single-instance guard
  on `127.0.0.1:8913`). Cross-platform rewrites are out of scope.
- Keep REST API changes backward-compatible — the egui UI and the web replica
  both consume `/api/traffic`, `/api/apps`, `/api/hosts`, `/api/status`.
- Graph rendering must preserve the established behavior: 3-pass box
  smoothing (radius `n/20`, clamp 2..400), Catmull-Rom rounding, peak
  normalization to the raw bucket peak, honest raw-peak y-scale (see
  `packetly-ui/src/main.rs` and `web/app.js`).

## Update flow

1. Agent edits (web/ prototype + description of the intended desktop change)
   → commit to GitHub.
2. Human pulls locally: `git pull`.
3. The change is implemented in the egui app / collector, built, verified on
   Windows, and released via a `v*` tag.
