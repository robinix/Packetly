# Packetly

A lightweight, open-source GlassWire-style network monitor for Windows — live per-app
traffic graphing, attribution, usage quotas and alerts, with a native desktop UI.
Not affiliated with GlassWire; no firewall, no network scanner, no resource monitor.

## Features

- **Live traffic monitor** — real-time per-second traffic graph from Windows ETW
  kernel-network events, with GlassWire-style smooth rolling hills, auto-scale,
  drag-select any region for a per-app/host breakdown
- **Per-app attribution** — every byte is attributed to the executable that caused it
  (PID resolution + TCP owner-table fallback)
- **Traffic tab** — top apps, remote hosts, traffic types (HTTPS/DNS/QUIC/…) and
  countries in four sortable columns
- **Usage & quotas** — per-app or global usage over 5 minutes/3 hours/24 hours/
  7 days/30 days/all time/custom, daily/weekly/monthly quotas with 75/90/100 % alerts
- **Alerts** — new-app-on-the-network notifications, quota threshold alerts
- **Hostnames & countries** — reverse DNS; optional GeoIP (strictly opt-in, off by
  default; when enabled, host IPs are batched to ip-api.com)
- **Native Rust UI** — light/AMOLED-dark themes, 100–175 % zoom, tray icon, per-app icons
- **Efficient storage** — SQLite WAL with tiered 1 s/60 s/hourly/daily tables,
  watermarked rollups and retention (48 h / 62 d / 400 d / forever)

All data stays on your machine in `%LOCALAPPDATA%\Packetly\packetly.db`. No telemetry.

## Install (from a Release)

1. Download `Packetly-<version>-win-x64.zip` from the
   [Releases](../../releases) page and extract it.
2. Run `scripts\install.ps1` (right-click → *Run with PowerShell*, or
   `powershell -ExecutionPolicy Bypass -File scripts\install.ps1`). It:
   - copies the apps to `%LOCALAPPDATA%\Packetly\`
   - registers the **Packetly** scheduled task (starts at logon, elevated — ETW needs admin)
   - starts it; the dashboard window opens automatically
3. Done. A tray icon appears; use it to pause collection or exit.

**Requirements:** Windows 10/11 x64, administrator approval for the one UAC prompt,
and the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).

To remove: `packetly.exe --uninstall-autostart`, delete `%LOCALAPPDATA%\Packetly\`,
and delete the desktop/Start-Menu `Packetly Dashboard` shortcuts if you want.

## Build from source

```powershell
# collector (.NET 8 SDK)
dotnet build packetly/src/Packetly -c Release
# UI (Rust stable)
cargo build --release --manifest-path packetly-ui/Cargo.toml
```

Binaries land in `packetly/src/Packetly/bin/Release/net8.0-windows/packetly.exe` and
`packetly-ui/target/release/packetly-ui.exe`. To run without installing: launch
`packetly.exe` from an elevated prompt (serves the API on `127.0.0.1:8912` and opens
the UI), then `packetly.exe --install-autostart` to register logon autostart.

## Releases & updates

Releases are built automatically by GitHub Actions when a `v*` tag is pushed
(`.github/workflows/release.yml`): both binaries are compiled on a Windows runner,
packaged into `Packetly-<tag>-win-x64.zip` and attached to the GitHub Release with
auto-generated notes. To publish an update, bump the version and push a new tag.

## How it works

- The **collector** (`packetly.exe`, .NET 8) opens an ETW **"NT Kernel Logger"** session
  with the `NetworkTCPIP` keyword and parses kernel-network events (TCP/UDP, IPv4/IPv6)
  directly from their manifest layout, attributing every byte to a PID → executable.
  A polling fallback (live TCP owner tables) is used when ETW is unavailable.
- Events flow through a 1-second ring buffer into SQLite (WAL), where a 30 s
  aggregator rolls them up into 60 s / hourly / daily tiers with idempotent
  watermarked rollups and retention pruning.
- A small REST API on `127.0.0.1:8912` serves the data; the **UI** (`packetly-ui.exe`,
  Rust + egui/egui_plot) renders it. Both auto-start via the scheduled task and
  communicate over loopback only.
- GeoIP is off by default. When enabled it batches remote IPv4s to ip-api.com; if
  direct access is blocked it falls back to a local SOCKS5 proxy (default
  `127.0.0.1:10808`, override with the `PACKETLY_GEOIP_PROXY` environment variable).

## Limitations

- Transparent-proxy setups (WireSock/v2rayN relays) can attribute some relayed flows
  to `unknown:pidN` rows — bytes are never lost, but the originating app can't be
  identified without a WFP driver.
- ETW kernel sessions require elevation; the scheduled task runs at the highest
  integrity level for this reason.

## License

MIT — see [LICENSE](LICENSE).
