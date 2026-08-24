# Changelog

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
- LibreHardwareMonitorLib 0.9.6 (MIT) — direct Super I/O, SMBus, GPU sensor access

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
