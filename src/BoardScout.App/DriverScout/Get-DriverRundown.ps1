<#
.SYNOPSIS
    DriverScout - full driver & hardware rundown with PID name resolution.

.DESCRIPTION
    Loads a scan manifest and produces a complete rundown of EVERY device on the
    machine: resolves each PCI VEN/DEV and USB VID/PID to human-readable vendor +
    product names using the offline pci.ids / usb.ids database (the maintained
    successor to the old pcidatabase.com), and attaches the installed driver
    version/date. Exports CSV + a styled HTML report you can keep as a permanent
    record of your hardware and drivers.

.PARAMETER ScanFile
    Scan JSON to report on. Defaults to the newest in .\scans.

.PARAMETER Class
    Optional filter: only show devices of this PNP class (e.g. Net, Display, USB).

.PARAMETER RefreshDb
    Force re-download of pci.ids / usb.ids even if cached.
#>
[CmdletBinding()]
param(
    [string]$ScanFile,
    [string]$Class,
    [switch]$RefreshDb,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root  = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$cache = Join-Path $root 'cache'
$data  = Join-Path $root 'data'   # committed offline fallback snapshot
. (Join-Path $root 'lib\Resolve-HardwareIds.ps1')

# ---- ensure the offline ID database is present --------------------------
function Confirm-IdDatabase {
    param([string]$CacheDir, [switch]$Force, [int]$TtlDays = 30)
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $sources = @(
        @{ name = 'pci.ids'; url = 'https://raw.githubusercontent.com/pciutils/pciids/master/pci.ids' },
        @{ name = 'usb.ids'; url = 'http://www.linux-usb.org/usb.ids' }
    )
    foreach ($s in $sources) {
        $p = Join-Path $CacheDir $s.name
        $stale = $Force -or -not (Test-Path $p) -or ((Get-Date) - (Get-Item $p).LastWriteTime).TotalDays -gt $TtlDays
        if ($stale) {
            try { Invoke-WebRequest -Uri $s.url -OutFile $p -UseBasicParsing -TimeoutSec 40 }
            catch { Write-Warning "Could not refresh $($s.name): $($_.Exception.Message)" }
        }
    }
}

# ---- load scan ----------------------------------------------------------
if (-not $ScanFile) {
    $ScanFile = (Get-ChildItem (Join-Path $root 'scans\*.json') | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
}
if (-not $ScanFile -or -not (Test-Path $ScanFile)) { throw "No scan file. Run Invoke-HardwareScan.ps1 first." }
$scan = Get-Content $ScanFile -Raw | ConvertFrom-Json
if (-not $scan.devices) { throw "This scan predates device enumeration. Re-run Invoke-HardwareScan.ps1." }

Confirm-IdDatabase -CacheDir $cache -Force:$RefreshDb
$db = Initialize-HwIdDb -CacheDir $cache -DataDir $data

# Read the snapshot version + source (cache=fresh, data=vendored fallback) for credit.
function Get-IdsInfo {
    param([string]$Name)
    foreach ($dir in @($cache, $data)) {
        $p = Join-Path $dir $Name
        if (Test-Path $p) {
            $ver = (Get-Content $p -TotalCount 14 | Where-Object { $_ -match 'Version:' }) -replace '.*Version:\s*', ''
            return [pscustomobject]@{ version = $ver.Trim(); source = (Split-Path $dir -Leaf) }
        }
    }
    return [pscustomobject]@{ version = '?'; source = '?' }
}
$pciInfo = Get-IdsInfo 'pci.ids'
$usbInfo = Get-IdsInfo 'usb.ids'

# ---- index installed driver versions by device id -----------------------
$drvById = @{}
foreach ($d in $scan.drivers) { if ($d.device_id) { $drvById[$d.device_id.ToUpper()] = $d } }

# ---- build rundown rows -------------------------------------------------
$rows = foreach ($dev in $scan.devices) {
    if ($Class -and $dev.class -ne $Class) { continue }
    $res = Resolve-FromPnpId -Db $db -PnpId $dev.hardware_id
    $drv = $drvById[$dev.hardware_id.ToUpper()]
    [pscustomobject]@{
        Class        = $dev.class
        Name         = $dev.name
        Bus          = if ($res) { $res.bus } else { $dev.bus }
        VID_PID      = if ($res -and $res.vendor_id) { "{0}:{1}" -f $res.vendor_id, $res.product_id } else { $null }
        Vendor       = if ($res) { $res.vendor } else { $null }
        Device       = if ($res) { $res.device } else { $null }
        DriverVer    = $drv.version
        DriverDate   = $drv.date
        INF          = $drv.inf
        HardwareID   = $dev.hardware_id
    }
}
$rows = @($rows | Sort-Object Class, Name)

# ---- console summary ----------------------------------------------------
$resolved = @($rows | Where-Object Vendor).Count
Write-Host ""
Write-Host "DriverScout - Driver & Hardware Rundown" -ForegroundColor Cyan
Write-Host ("  Host: {0}   OS: {1}   Win Product ID: {2}" -f $scan.scan.hostname, $scan.scan.os.caption, $scan.scan.os.product_id) -ForegroundColor DarkGray
Write-Host ("  Scan: {0}" -f ([IO.Path]::GetFileName($ScanFile))) -ForegroundColor DarkGray
Write-Host ("  {0} devices ({1} ID-resolved), PID DB: {2} PCI + {3} USB" -f `
    $rows.Count, $resolved, $db.counts.pci_devices, $db.counts.usb_devices) -ForegroundColor DarkGray
Write-Host ("  IDs: pci.ids {0} ({1}) - pci-ids.ucw.cz | usb.ids {2} ({3}) - linux-usb.org" -f `
    $pciInfo.version, $pciInfo.source, $usbInfo.version, $usbInfo.source) -ForegroundColor DarkGray
Write-Host ""
Write-Host "  By class:" -ForegroundColor DarkGray
$rows | Group-Object Class | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("    {0,-22} {1,3}" -f ($_.Name), $_.Count)
}

# ---- exports ------------------------------------------------------------
if (-not $OutDir) { $OutDir = Join-Path $root 'reports' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$csv  = Join-Path $OutDir ("rundown_{0}_{1}.csv"  -f $scan.scan.hostname, $stamp)
$html = Join-Path $OutDir ("rundown_{0}_{1}.html" -f $scan.scan.hostname, $stamp)

$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8

# Minimal, self-contained HTML report.
$enc = { param($s) [System.Web.HttpUtility]::HtmlEncode([string]$s) }
Add-Type -AssemblyName System.Web
$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'><title>DriverScout Rundown - $($scan.scan.hostname)</title>")
[void]$sb.AppendLine("<style>body{font:13px/1.4 Segoe UI,Arial,sans-serif;margin:24px;color:#1b1f23}h1{margin:0}.meta{color:#666;margin:6px 0 18px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #e1e4e8;padding:4px 7px;text-align:left;vertical-align:top}th{background:#f6f8fa;position:sticky;top:0}tr:nth-child(even){background:#fafbfc}.cls{font-weight:600;background:#eef3ff}code{font-family:Consolas,monospace;font-size:12px;color:#444}.unres{color:#b0b0b0}</style></head><body>")
[void]$sb.AppendLine("<h1>Driver &amp; Hardware Rundown</h1>")
[void]$sb.AppendLine("<div class='meta'>Host <b>$(& $enc $scan.scan.hostname)</b> &middot; $(& $enc $scan.scan.os.caption) &middot; Win Product ID <code>$(& $enc $scan.scan.os.product_id)</code> &middot; Machine ID <code>$($scan.scan.machine_id.Substring(0,12))</code><br>$($rows.Count) devices, $resolved ID-resolved &middot; generated $(Get-Date -Format 'yyyy-MM-dd HH:mm')</div>")
[void]$sb.AppendLine("<table><tr><th>Class</th><th>Name</th><th>Bus</th><th>VID:PID</th><th>Vendor</th><th>Device</th><th>Driver</th><th>Date</th><th>Hardware ID</th></tr>")
foreach ($r in $rows) {
    $vp = if ($r.VID_PID) { "<code>$(& $enc $r.VID_PID)</code>" } else { "<span class='unres'>-</span>" }
    [void]$sb.AppendLine("<tr><td>$(& $enc $r.Class)</td><td>$(& $enc $r.Name)</td><td>$(& $enc $r.Bus)</td><td>$vp</td><td>$(& $enc $r.Vendor)</td><td>$(& $enc $r.Device)</td><td>$(& $enc $r.DriverVer)</td><td>$(& $enc $r.DriverDate)</td><td><code>$(& $enc $r.HardwareID)</code></td></tr>")
}
[void]$sb.AppendLine("</table>")
[void]$sb.AppendLine("<div class='meta' style='margin-top:18px;border-top:1px solid #e1e4e8;padding-top:10px'>Hardware IDs resolved using the <b>PCI ID Repository</b> (<a href='https://pci-ids.ucw.cz/'>pci-ids.ucw.cz</a>) &mdash; <code>pci.ids</code> $(& $enc $pciInfo.version) &mdash; and the <b>USB ID Repository</b> (<a href='http://www.linux-usb.org/usb.ids'>linux-usb.org</a>) &mdash; <code>usb.ids</code> $(& $enc $usbInfo.version). Maintained by Albert Pool, Martin Mare&scaron;, Stephen J. Gowdy and volunteers. Thank you for keeping them running.</div>")
[void]$sb.AppendLine("</body></html>")
Set-Content -Path $html -Value $sb.ToString() -Encoding UTF8

# Markdown report (paste into notes / GitHub / a wiki).
$md = Join-Path $OutDir ("rundown_{0}_{1}.md" -f $scan.scan.hostname, $stamp)
$mdEsc = { param($s) ([string]$s) -replace '\|', '\|' }
$mb = [Text.StringBuilder]::new()
[void]$mb.AppendLine("# Driver & Hardware Rundown - $($scan.scan.hostname)")
[void]$mb.AppendLine("")
[void]$mb.AppendLine("- **OS:** $($scan.scan.os.caption)")
[void]$mb.AppendLine("- **Windows Product ID:** ``$($scan.scan.os.product_id)``")
[void]$mb.AppendLine("- **Machine ID:** ``$($scan.scan.machine_id.Substring(0,12))``")
[void]$mb.AppendLine("- **Devices:** $($rows.Count) ($resolved ID-resolved)")
[void]$mb.AppendLine("- **PID database:** pci.ids $($pciInfo.version) / usb.ids $($usbInfo.version) - [pci-ids.ucw.cz](https://pci-ids.ucw.cz/) / [linux-usb.org](http://www.linux-usb.org/usb.ids)")
[void]$mb.AppendLine("- **Generated:** $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$mb.AppendLine("")
[void]$mb.AppendLine("| Class | Name | VID:PID | Vendor | Device | Driver | Date |")
[void]$mb.AppendLine("|-------|------|---------|--------|--------|--------|------|")
foreach ($r in $rows) {
    $line = "| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f `
        (& $mdEsc $r.Class), (& $mdEsc $r.Name), (& $mdEsc $r.VID_PID), `
        (& $mdEsc $r.Vendor), (& $mdEsc $r.Device), (& $mdEsc $r.DriverVer), (& $mdEsc $r.DriverDate)
    [void]$mb.AppendLine($line)
}
[void]$mb.AppendLine("")
[void]$mb.AppendLine("*Hardware IDs resolved via the PCI ID Repository (Albert Pool, Martin Mares) and USB ID Repository (Stephen J. Gowdy). Thank you for keeping them running.*")
Set-Content -Path $md -Value $mb.ToString() -Encoding UTF8

Write-Host ""
Write-Host ("  CSV : {0}" -f $csv)  -ForegroundColor Green
Write-Host ("  HTML: {0}" -f $html) -ForegroundColor Green
Write-Host ("  MD  : {0}" -f $md)   -ForegroundColor Green
Write-Host ""
