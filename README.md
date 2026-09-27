# BoardScout

A portable Windows app that maps your motherboard, bandwidth, drivers, storage, and operating
system in one place: what is plugged in where, what each part is good for, what needs attention,
and how long Windows will keep patching it. It never installs drivers or firmware.

**Status:** v1.1.0 (2026-09-26) · Windows 10/11 · .NET 10 · MIT license ·
[Download](https://github.com/mikedopp/BoardScout/releases/latest) · [Changelog](CHANGELOG.md)

![BoardScout overview: interactive board map, live header tiles, and part inspector](docs/screenshots/overview.png)

## Download and run

From the [latest release](https://github.com/mikedopp/BoardScout/releases/latest):

| File | What it is |
| --- | --- |
| `BoardScout-1.1.0-win-x64.exe` | **Standalone.** One file with everything inside. Put it in any writable folder and run it. |
| `BoardScout-1.1.0-win-x64.zip` | **Portable folder.** Extract and run `BoardScout.exe`; keep `Assets` and `DriverScout` beside it. |
| `BoardScout-1.1.0-SHA256SUMS.txt` | Checksums for both. |

No installer, no admin rights, and no .NET install needed; the .NET 10 runtime is built in.
Scans, reports, settings, and the web views' profile live in a `Data` folder beside the exe. If
that folder is read-only, BoardScout uses `%LOCALAPPDATA%\BoardScout`; set `BOARDSCOUT_DATA` to
choose another folder. The standalone exe unpacks its bundled files to .NET's per-version cache
under `%TEMP%\.net` on first run.

## What you get

**Overview** — an interactive motherboard map. Hover the CPU, memory, graphics, drives, ports, or
open slots for status, live usage, and a plain-language capability estimate. Mouse wheel zooms
(it eases rather than jumps), drag pans. The ASRock B550M Steel Legend has a verified layout
(lane sharing, the M.2 Key-E Wi-Fi slot); other boards get the parts their scan reports.

**Topology** — CPU-direct lanes versus devices behind the chipset uplink, with flowing links that
move faster on wider buses. Board-specific slot names and lane sharing appear only for the verified
board; other boards get generic labels and the slots their firmware reports.

![Bandwidth topology with flowing links and a QuickLiquid glass legend](docs/screenshots/topology.png)

**System** — "What your OS says about you": a personality verdict (The Power Developer, The
Reliable Holdout, The Battle Station, …), Windows support status from Microsoft's lifecycle
pages, patch health, a Windows 11 readiness check on Windows 10, your build, every .NET runtime
and SDK, and searchable tables of patches, installed software, and scheduled tasks.

![System tab: personality card with QuickLiquid glass, ESU status, and Windows 11 readiness](docs/screenshots/system.png)

**Drivers, Storage, Efficiency** — driver versions with links to official vendor, OEM, or Microsoft
update pages; every volume with its usage; and suggestions such as enabling XMP/DOCP/EXPO,
freeing space, or reviewing BIOS updates.

**Version button** — the chasing-colors `v1.1.0` pill opens runtime details, sensor
troubleshooting, settings (glass effects, motion, live refresh, minimize to tray), dependencies,
requirements, and legal notices. *Copy diagnostics* gathers what an issue report needs.

![Version pop-out: runtime, sensor status with PawnIO guidance, and settings](docs/screenshots/version-popout.png)

## Sensors: why a temperature can be missing

BoardScout reads live sensors through LibreHardwareMonitorLib 0.9.6. That library reads CPU, VRM,
and motherboard fan sensors through the **PawnIO** driver (by namazso, https://pawnio.eu/), which
it does not ship, and those reads need **administrator rights**. Without both, only GPU
temperature and GPU fans are readable, and the header says what is missing ("Needs PawnIO" /
"Needs admin"). The version pop-out shows the current state, links to PawnIO, and can restart
BoardScout as administrator. Everything else in BoardScout works without either.

## What goes online

Only these, and only when you ask:

- **Check drivers** and **Download drivers** contact vendor, OEM, and Microsoft catalog sources.
- Links you click (official update pages, PawnIO, Report issue) open in your browser.

Everything else is local. Scans use built-in Windows tools, and the Topology and System views
load their scripts (QuickLiquid, D3) from the app folder, not a CDN.

## How BoardScout keeps work cheap

1. Startup shows the latest cached scan immediately.
2. **Scan now** is a local inventory (about 10 s). Run it after hardware or firmware changes.
3. **Check drivers** is a separate, cancellable online step.
4. Live telemetry (CPU, memory, sensors, network) runs on a background thread at the refresh rate
   you choose, so the window never waits on hardware.
5. BoardScout presents updates for review and **never installs or flashes anything**.

## Troubleshooting

- **Version button → Copy diagnostics** puts version, runtime, Windows build, WebView2, sensor
  state, and settings on the clipboard for an issue report.
- Unexpected errors are written to `crash.log` in the data folder.
- `BoardScout.exe --system-json` prints the System tab's data; `--scan` runs a headless scan;
  `--check-drivers` also checks drivers.
- Topology and System need the Microsoft Edge WebView2 Runtime (built into Windows 11 and current
  Windows 10). Without it those two views say so; the rest works.

## Build from source

Requires the .NET 10 SDK on Windows.

    .\Build-Portable.ps1 -Runtime win-x64

This produces `build\BoardScout-<version>-win-x64.zip` (portable folder),
`build\BoardScout-<version>-win-x64.exe` (standalone), and a SHA-256 checksum file. The version
comes from `src\BoardScout.App\BoardScout.App.csproj`. `BoardScout.cmd` builds on first use and
then launches the portable build.

Source layout:

- `src\BoardScout.App` — the .NET 10 Windows Forms application
- `src\BoardScout.App\UI` — board map, liquid sidebar, glass controls, version pop-out
- `src\BoardScout.App\Services` — scanning, driver checks, telemetry, System tab data
- `src\BoardScout.App\Assets` — Topology and System web views, plus `vendor\` (QuickLiquid, D3)
- `src\BoardScout.App\DriverScout` — the bundled PowerShell scan engine and its notices

## License and legal

MIT — see [LICENSE](LICENSE). Bundled components keep their own licenses: see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which also lists trademark attributions.

BoardScout reads hardware identifiers that Windows and firmware report, for diagnostic and
informational purposes only. No proprietary firmware, drivers, or copyrighted vendor materials are
bundled or distributed. Windows lifecycle dates reflect Microsoft's published pages as of
2026-09-26 and can change; the Windows 11 readiness result is BoardScout's quick check, and
Microsoft's PC Health Check app has the final word. Upgrade prices in the Pimp My Build report are
rough estimates, not offers. All product names and brands belong to their respective owners, and
their use does not imply affiliation or endorsement.
