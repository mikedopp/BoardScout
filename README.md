# BoardScout

BoardScout turns a local Windows hardware scan into a visual motherboard and
storage dashboard. It is portable, dependency-free, and keeps scan data on the
machine.

## Run it

Double-click `BoardScout.cmd`, or run:

```powershell
.\\BoardScout.ps1
```

The launcher scans the PC, writes the private scan under `data\\scans`, builds
a self-contained dashboard at `build\\index.html`, and opens it in the default
browser. No local web server is required.

To build from an existing schema-2 scan without opening a browser:

```powershell
.\\BoardScout.ps1 -ScanFile D:\\path\\scan.json -NoLaunch
```

You can also open `src\\web\\index.html` directly and choose **Load Scan JSON**.

## Privacy and accuracy

Raw scans can include hostnames, hardware IDs, and serial numbers. They are
ignored by Git, and the dashboard does not display the motherboard serial.
Generated dashboards can embed the full scan, so `build\\` is ignored too.

The board drawing is a schematic, not a manufacturer CAD diagram. BoardScout
uses firmware-reported expansion-slot data when available and clearly marks
form-factor-based connector counts as estimates. Confirm physical connectors
and PCIe lane sharing in the motherboard manual before buying hardware.

## Layout

- `src\\Invoke-BoardScan.ps1` — Windows hardware collector
- `src\\web\\index.html` — standalone dashboard source
- `Build-BoardScout.ps1` — produces `build\\index.html`
- `BoardScout.ps1` / `BoardScout.cmd` — scan, build, and launch
