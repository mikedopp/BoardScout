# Changelog

## 1.2.0 — 2026-09-27

Privacy mode, so screenshots and exports can be shared without giving away who or where you are.

### Fixed
- **Exporting a scan as JSON shared the Windows owner's email and product ID.** Export copied the
  raw scan file, which records the Windows registered owner (often an email address) and the
  Windows product ID. JSON exports now always leave both out; nothing in a hardware report needs them.
- **Copy diagnostics could include your user folder path.** Diagnostics are meant for public issue
  reports, so they now always replace your PC name, user folder, serial numbers, email addresses,
  and MAC addresses with placeholders.

### Added
- **Privacy mode** (version button → Settings, or Ctrl+Shift+P). While it is on:
  - the header shows a purple **Privacy on** chip, so a screenshot shows it was taken masked;
    clicking the chip turns privacy mode off;
  - the System tab hides the installed-software, patch, and scheduled-task lists and shows their
    counts only;
  - the Scan Log shows `THIS-PC` for the PC name and `%USERPROFILE%` for your user folder;
  - the Storage tab shows `Volume D:` instead of volume labels you named yourself;
  - spec sheet exports say "This PC" and omit the PC name, machine ID, serial numbers, system UUID,
    and volume labels from the embedded scan data; JSON exports omit the same and stay importable.
- Scans on disk are unchanged; privacy mode only changes what is shown and exported.

### Changed
- The Topology view now receives hardware details only (no PC name, owner details, or serials).
- The Storage tab shows `Volume <letter>` when a volume has no disk model or label (it showed a blank).

## 1.1.0 — 2026-09-26

.NET 10, the QuickLiquid glass look, a UI that no longer freezes, and a System tab that tells the
truth about Windows support.

### Fixed
- **The window froze for about 5 seconds after every start.** The System tab asked WMI for the TPM
  on the UI thread; without admin rights that query times out after ~5 s. TPM is now read through
  TPM Base Services (~10 ms, no admin), and the whole System gather (~0.6 s) runs off the UI thread.
- **"TPM: Not detected" on PCs that have one.** Same cause: the WMI query fails without admin. This
  PC's TPM 2.0 now shows correctly.
- **The UI stuttered every second.** Sensor sampling (~80 ms) and the network adapter scan (~30 ms)
  ran on the UI thread each tick, and the sensor driver's first open (~0.6 s) blocked startup. All
  sampling now runs on a background thread. Measured on the release build: UI stalls in the first
  8 s went from 6 (~1.2 s total, recurring) to 2 (~0.2 s, only in the first 0.6 s).
- **Patches listed out of order.** Install dates were sorted as text, so 9/9/2026 came before
  12/1/2025. Dates are parsed and sorted as dates (ISO format in the table).
- **The ".NET SDKs" trait never appeared.** SDKs were counted from `dotnet --list-runtimes`, which
  never lists SDKs. Runtimes and SDKs are now read from the dotnet folders (x64 and x86).
- **Topology was blank without internet.** D3 loaded from d3js.org; it is now bundled.
- **The web views broke in read-only folders.** WebView2 kept its profile beside the exe; it now
  lives in the BoardScout data folder.
- **Topology showed one board's details on every board.** "M2_2 disables SATA 5/6", PCIE2/PCIE3,
  and the Key-E note were hard-wired for the ASRock B550M Steel Legend. They now appear only for
  that board; other boards get generic labels plus the slots their firmware reports.
- **Topology bus labels overlapped** under the CPU. Labels now sit on each link near its device.
- **FANS showed an amber "Fans idle" warning** when only the GPU fan was readable (it stops at idle
  by design). It now says what is missing ("Needs PawnIO" / "Needs admin").
- **Stale version strings:** spec sheets said "BoardScout v0.8.0", downloads sent a v0.8.0
  User-Agent, and every build zip was named 0.9.0. All now come from the real version.
- **Upgrade report assumed one PC:** it called every GPU "a massive upgrade from RTX 3050" and
  listed an Intel AX210 as your Wi-Fi card. It now uses the detected GPU, NVMe drive, and Wi-Fi card,
  and notes when an APU limits the x16 slot to PCIe 3.0. Spec sheets no longer call DDR5 "DDR4".
- **The version pop-out ignored clicks elsewhere** (it only closed on another version click or
  when the window lost focus). Clicking anywhere else or pressing Escape now closes it.

### Added
- **QuickLiquid glass.** The System tab's personality card and the Topology legend use real SVG
  refraction from QuickLiquid 0.1.2 (bundled, works offline). The System tab's task filter has a
  liquid tab indicator.
- **Liquid native UI.** The sidebar selection glides between destinations and stretches like a
  droplet; buttons, header tiles, the inspector, and cards get a glass sheen; zoom eases instead of
  jumping; topology links show flowing traffic, faster on wider buses.
- **Windows support that matches Microsoft's lifecycle pages** (checked 2026-09-26): Windows 10
  shows Extended Security Updates coverage through October 12, 2027, with the days left; Windows 11
  shows the end date for its version and edition (Home/Pro or Enterprise/Education), including 25H2
  and 26H1.
- **Windows 11 readiness** for Windows 10 PCs: TPM 2.0, UEFI Secure Boot, processor generation,
  memory, and system drive, each with a plain-language result. It is a quick check, not Microsoft's.
- **Sensor troubleshooting.** LibreHardwareMonitorLib 0.9.6 reads CPU, VRM, and motherboard fan
  sensors through the PawnIO driver, which it does not ship. The version pop-out now says whether
  PawnIO is installed and whether BoardScout is elevated, links to pawnio.eu, and can restart
  BoardScout as administrator.
- **Settings** under the version button: glass effects, motion, live refresh (1/2/5 s), and minimize
  to tray. Stored in `Data\settings.json`.
- **Copy diagnostics** (version, runtime, Windows build, WebView2, sensors, settings) for issue reports.
- **Standalone exe.** Releases now include a single `BoardScout-<version>-win-x64.exe` with everything
  inside, next to the portable zip. Both keep scans in a `Data` folder beside the exe.
- `crash.log` in the data folder when something unexpected happens, and a `--system-json` switch that
  prints the System tab's data.

### Changed
- .NET 8 → **.NET 10** (runtime 10.0.12). WebView2 SDK 1.0.2903.40 → 1.0.4191.47, System.Management
  10.0.2 → 10.0.12. The exe is ~20 MB smaller (the unused WebView2 WPF assembly is no longer shipped).
- The cached scan loads off the UI thread with source-generated JSON.
- Dark scrollbars; rounded glass cards replace square 1 px borders.
- Removed dead code (an unused GDI+ topology control and unused models).

### Known limitations
- Without PawnIO and administrator rights only GPU temperature and GPU fans are readable.
- Memory use is ~290 MB (was ~260 MB), mostly the newer WebView2 runtime and the glass engine.
- The Windows 11 readiness check covers common Intel Core and AMD Ryzen names; others show
  "check PC Health Check".

### Dependencies
- Added (bundled web assets): QuickLiquid 0.1.2 (MIT), D3.js 7.9.0 (ISC, previously loaded from a CDN).
- THIRD-PARTY-NOTICES now also lists LibreHardwareMonitorLib's own dependencies that ship inside the
  exe: BlackSharp.Core, DiskInfoToolkit, RAMSPDToolkit-NDD (MPL-2.0), HidSharp (Apache-2.0),
  System.IO.Ports and Mono.Posix.NETStandard (MIT).

## 1.0.0 — 2026-08-24

### Added
- **System tab**: new sidebar destination with WebView2-rendered dashboard showing OS personality analysis ("What Your OS Says About You"), build summary, .NET versions, installed patches, installed software, and scheduled tasks
- **OS personality verdicts**: automatic profiling based on OS version, patch discipline, installed software, and task scheduler — assigns a personality title (The Power Developer, The Reliable Holdout, The Battle Station, etc.) with emoji, traits, patch health, and actionable recommendations
- **Software inventory**: enumerates installed programs from registry (HKLM + WOW6432 + HKCU) with name, version, publisher, install date, and estimated size
- **Patch inventory**: lists all installed hotfixes from WMI with KB ID, description, install date, and installer
- **Scheduled task inventory**: parses `schtasks` output with category filtering (All / User / Microsoft / Windows)
- **.NET runtime detection**: lists CLR version, all installed runtimes via `dotnet --list-runtimes`, and .NET Framework version from registry
- **TPM and Secure Boot detection**: reads TPM spec version from WMI and Secure Boot state from registry
- **Search and sort**: all System tab tables support live search filtering and column-click sorting

### Changed
- **Board map spacing**: redistributed M2_3 WiFi (reduced height, moved up), M2_2 NVMe (moved up closer to chipset), PCIE3 x4, internal headers, and board identity positions for cleaner vertical spacing in the lower section; updated all circuit trace endpoints to match
- Sidebar navigation now has 7 entries (System added before Scan Log)

## 0.9.0 — 2026-08-23

### Changed
- **PCB-style circuit traces**: 9 Manhattan-routed traces replace plain lines on the board map — color-coded by bus type (blue=CPU-direct, teal=uplink, purple=chipset, orange=SATA) with 3-pass glow rendering and speed labels (DDR4-3200, PCIe×16, etc.)
- **Dot pattern background**: replaces grid lines with subtle dot matrix for a cleaner PCB aesthetic
- **Anti-aliased rounded toolbar buttons**: custom-painted `RoundedButton` control with GDI+ anti-aliased rendering replaces jagged Region-clipped buttons; hover and press states from theme colors
- **Larger board map typography**: label/small/title font minimums bumped by 1pt for readability at all zoom levels
- **Larger corner radii**: component boxes, dashed outlines, and port boxes all use softer rounding (7→12, 5→8, 6→10)
- **BoardScout brand mark** in Pimp My Build report footer

### Fixed
- DDR4-3200 trace label no longer overlaps fan header area (rerouted above at y=65)

## 0.8.0 — 2026-08-22

### Added
- **Pimp My Build button**: green-accent header button opens the upgrade planner report directly in the browser with Amazon and Newegg search links for each recommended component
- **Minimize to tray**: minimizing the window sends BoardScout to the system tray with a version-labeled icon; double-click or right-click "Open" to restore; right-click "Exit" to close
- **Version button animation rewrite**: rotating `LinearGradientBrush` (blue→red→yellow→green sweep, ~8 second revolution) matching SnipDeck's GlowEffect approach
- **Feedback/report links**: "Report issue" link in status bar and version popout opens GitHub Issues

### Changed
- Header toolbar decluttered from 9 buttons to 6 with separator grouping: primary (Scan now + Pimp My Build), driver workflow (Check drivers + Download), data (Import + Export)
- "Data folder" and "Report issue" moved to status bar links
- Pimp My Build uses a distinctive green accent (`StyleFeatureButton`) to stand out from standard buttons

### Dependencies
- No new dependencies

## 0.7.0 — 2026-08-22

### Added
- **Version button**: animated Google chasing-colors button in the header showing the current version; click to see runtime info, dependencies, and requirements
- **Component ages**: System details panel now shows release date and age for CPU, motherboard, and GPU with color-coded severity (green < 3yr, amber 3-5yr, red 5yr+)
- **Upgrade planner export**: Export → Upgrade planner generates a self-contained HTML report showing component ages, best compatible upgrades for the detected motherboard/socket, estimated pricing, and a "pimped out" dream build summary
- **Legal files**: MIT LICENSE, THIRD-PARTY-NOTICES.md with dependency licenses and trademark attributions

### Dependencies
- No new dependencies

## 0.6.0 — 2026-08-22

### Added
- **Spec sheet HTML export**: Export → Spec sheet (.html) generates a self-contained shareable hardware summary with stats bar, component cards, storage table with color-coded usage, USB device chips, collapsible raw JSON, and print-friendly light theme
- Export dialog now defaults to spec sheet HTML (also still offers JSON)
- Spec sheet auto-opens in default browser after export
- **Real sensor readings via LibreHardwareMonitorLib**: CPU package temp, GPU temp, VRM temp, and per-fan RPMs from the Super I/O chip (Nuvoton NCT6779D on ASRock B550M) — replaces empty WMI MSAcpi/Win32_Fan queries
- Header bar TEMP now shows multi-zone: "CPU 52° · GPU 41° · VRM 38°"
- Header bar FANS shows named fans with RPM: "CPU Fan 1120 · Chassis Fan #1 780"

### Changed
- System.Management bumped from 8.0.0 to 10.0.2 (LibreHardwareMonitorLib dependency)

### Dependencies
- LibreHardwareMonitorLib 0.9.6 (MPL-2.0; originally listed here as MIT in error) — direct Super I/O, SMBus, GPU sensor access

## 0.5.0 — 2026-08-22

### Added
- D3.js v7 bandwidth topology (WebView2): hierarchical tree layout replaces GDI+ diagram, with bandwidth badges, color-coded nodes, and hover tooltips explaining each bus
- Live thermal monitoring via MSAcpi_ThermalZoneTemperature (root\WMI)
- Live fan speed reading via Win32_Fan
- Live network throughput (up/down) via NetworkInterface.GetIPStatistics()
- Live disk I/O via GetProcessIoCounters P/Invoke
- TEMP, FANS, NET live metrics in the header bar with color-coded thresholds

### Changed
- Color palette swapped to match the DriverScout HTML dashboard (deep navy base, blue/green/amber/red semantic colors)
- Scanner engine bumped to v1.0.0 (schema 2.0) with full board-layout collectors
- Build archive updated for 0.5.0

### Dependencies
- Microsoft.Web.WebView2 1.0.2903.40 (topology visualization)
- System.Management 8.0.0 (WMI thermal/fan queries)

### Verified
- Memory: XMP/DOCP rated-speed detection from DIMM part numbers
- PCIe topology: 36 devices mapped via Win32_PnPSignedDriver Location
- Expansion slots: Win32_SystemSlot enumeration (6 slots)
- Volumes: 8 drives with disk model/bus-type correlation
- Dead devices: ConfigManagerErrorCode scan
- Displays: WmiMonitorID EDID extraction
- USB devices: VID/PID parsing (22 devices)
- Form factor: micro-atx inferred from baseboard product name
- D3.js topology: renders real scan data with CPU-direct and chipset paths

## 0.4.0

- Interactive board map with zoom, pan, hover inspector
- Bandwidth topology diagram (CPU-direct vs chipset paths)
- Sidebar pill navigation (Overview, Topology, Drivers, Storage, Efficiency, Scan Log)
- Driver grid with official vendor update links
- Storage grid with usage percentage color coding
- Suggestion engine: memory XMP, low-space volumes, dead devices, BIOS age, stale drivers
- Live CPU and memory telemetry (1-second polling)
- Dark/light theme with DWM title bar integration
- Import/export scan JSON
- Headless CLI modes: --scan, --check-drivers
