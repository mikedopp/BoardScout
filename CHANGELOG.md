# Changelog

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
