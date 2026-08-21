<#
.SYNOPSIS
    DriverScout - verify that downloaded updates actually installed.

.DESCRIPTION
    After you download + install an update, run this to confirm it took effect.
    It captures the previous update report as the "before" baseline, performs a
    fresh hardware scan + update check, then diffs the two: any component that was
    'update-available' and is now 'current' is reported APPLIED; anything still
    flagged is PENDING (with the old -> new installed version shown).

    DriverScout never installs anything itself -- it only re-measures and tells you
    whether what you installed landed.

.PARAMETER Component
    Only verify this component_key (substring match). Default: all that had
    updates available in the baseline report.

.PARAMETER NoRescan
    Skip the fresh scan/check and just diff the two most recent reports (useful if
    you already re-ran Check Updates from the UI).
#>
[CmdletBinding()]
param(
    [string]$Component,
    [switch]$NoRescan
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
function Get-Newest { param($Glob) Get-ChildItem $Glob -EA SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 }
function Read-Report { param($File) if ($File) { Get-Content $File.FullName -Raw | ConvertFrom-Json } }

# Index a report's results by component_key.
function Index-Report {
    param($Report)
    $m = @{}
    if ($Report) { foreach ($r in $Report.results) { $m[$r.component_key] = $r } }
    return $m
}

# 1) Baseline = the newest report that exists right now (the "before" picture).
$baselineFile = Get-Newest (Join-Path $root 'reports\report_*.json')
if (-not $baselineFile) { throw "No prior update report. Run Invoke-UpdateCheck.ps1 first." }
$baseline = Index-Report (Read-Report $baselineFile)

# 2) Fresh scan + check (unless told to reuse existing reports).
if (-not $NoRescan) {
    Write-Host "Re-scanning hardware and re-checking updates..." -ForegroundColor Cyan
    & (Join-Path $root 'Invoke-HardwareScan.ps1') -Quiet *>$null
    & (Join-Path $root 'Invoke-UpdateCheck.ps1') *>$null
}

# 3) After = newest report now (the fresh one if we re-ran).
$afterFile = Get-Newest (Join-Path $root 'reports\report_*.json')
$after = Index-Report (Read-Report $afterFile)

# 4) Diff: focus on components that had an update available in the baseline.
$targets = $baseline.Values | Where-Object {
    $_.status -eq 'update-available' -and (-not $Component -or $_.component_key -like "*$Component*")
}

function Get-Installed { param($Best)
    if ($Best.installed_market)  { return $Best.installed_market }
    if ($Best.installed_version) { return $Best.installed_version }
    if ($Best.installed_date)    { return $Best.installed_date }
    return '?'
}

Write-Host ""
Write-Host ("  {0,-30} {1,-22} {2,-14} {3}" -f 'COMPONENT','WAS -> TARGET','NOW','RESULT') -ForegroundColor DarkGray
$applied = 0; $pending = 0
foreach ($b in ($targets | Sort-Object category, model)) {
    $a = $after[$b.component_key]
    $wasInstalled = Get-Installed $b.best
    $target       = $b.best.latest_version
    $nowInstalled = if ($a) { Get-Installed $a.best } else { '?' }
    $nowStatus    = if ($a) { $a.status } else { 'missing' }

    if ($nowStatus -eq 'current' -or ($a -and $nowInstalled -eq $target)) {
        $result = 'APPLIED'; $color = 'Green'; $applied++
    } else {
        $result = 'PENDING'; $color = 'Yellow'; $pending++
    }
    $model = if ($b.model.Length -gt 29) { $b.model.Substring(0,29) } else { $b.model }
    Write-Host ("  {0,-30} {1,-22} {2,-14} {3}" -f `
        $model, ("{0} -> {1}" -f $wasInstalled, $target), $nowInstalled, $result) -ForegroundColor $color
}

Write-Host ""
$total = $applied + $pending
if ($total -eq 0) {
    Write-Host "  No updates were pending in the baseline report -- nothing to verify." -ForegroundColor Green
} else {
    Write-Host ("  {0} applied, {1} still pending (of {2} checked)." -f $applied, $pending, $total) `
        -ForegroundColor $(if ($pending) { 'Yellow' } else { 'Green' })
}
Write-Host ""
