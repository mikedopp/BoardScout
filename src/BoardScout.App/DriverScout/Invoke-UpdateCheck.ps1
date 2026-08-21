<#
.SYNOPSIS
    DriverScout - Phase 2: run update resolvers against a scan manifest.

.DESCRIPTION
    Loads the latest (or a specified) scan JSON, dot-sources every resolver in
    .\resolvers, and for each component runs the resolvers that apply. Each
    resolver returns a candidate tagged with source + trust level. The report
    ranks vendor-direct over baseline (e.g. Microsoft Update Catalog).

    Resolvers are discovered by convention -- drop a Resolve-*.ps1 in .\resolvers
    that defines Test-Resolver_<Name> / Invoke-Resolver_<Name>; no wiring needed.

.PARAMETER ScanFile
    Specific scan JSON to evaluate. Defaults to the newest in .\scans.
#>
[CmdletBinding()]
param(
    [string]$ScanFile,
    [string]$OutDir,
    [string]$CacheDir
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$cacheDir = if ($CacheDir) { $CacheDir } else { Join-Path $root 'cache' }
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

# Trust ranking: higher wins when multiple sources answer for one component.
$script:TrustRank = @{ vendor = 3; oem = 2; baseline = 1; unknown = 0 }

# ---- load scan ----------------------------------------------------------
if (-not $ScanFile) {
    $ScanFile = (Get-ChildItem (Join-Path $root 'scans\*.json') | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
}
if (-not $ScanFile -or -not (Test-Path $ScanFile)) { throw "No scan file found. Run Invoke-HardwareScan.ps1 first." }
$scan = Get-Content $ScanFile -Raw | ConvertFrom-Json
Write-Host "Evaluating scan: $([IO.Path]::GetFileName($ScanFile))" -ForegroundColor Cyan

# ---- load shared libs, then discover + load resolvers -------------------
foreach ($lf in (Get-ChildItem (Join-Path $root 'lib\*.ps1') -ErrorAction SilentlyContinue)) { . $lf.FullName }
$resolverFiles = Get-ChildItem (Join-Path $root 'resolvers\Resolve-*.ps1') -ErrorAction SilentlyContinue
foreach ($rf in $resolverFiles) { . $rf.FullName }
$resolvers = Get-Command -CommandType Function -Name 'Test-Resolver_*' |
    ForEach-Object {
        $suffix = $_.Name -replace '^Test-Resolver_', ''
        [pscustomobject]@{ name = $suffix; test = $_.Name; invoke = "Invoke-Resolver_$suffix" }
    } | Where-Object { Get-Command $_.invoke -ErrorAction SilentlyContinue }
Write-Host ("Loaded {0} resolver(s): {1}" -f $resolvers.Count, (($resolvers.name) -join ', ')) -ForegroundColor DarkGray

# ---- OEM context for brand-specific resolvers (Dell, HP, Lenovo, etc.) ---
$oem = $scan.system.oem
if ($oem) {
    $script:OemBrand     = "$($oem.oem_brand)"
    $script:OemSystemId  = "$($oem.oem_system_id)"
    $script:OemInfo      = $oem
    # Backward compat aliases for existing Dell resolver
    $script:DellOemBrand = $script:OemBrand
    $script:DellSystemId = $script:OemSystemId
    if ($oem.oem_brand) { Write-Host ("OEM detected: {0} (system ID: {1})" -f $oem.oem_brand, $oem.oem_system_id) -ForegroundColor DarkGray }
}

# ---- run ----------------------------------------------------------------
$results = [System.Collections.Generic.List[object]]::new()
foreach ($c in $scan.components) {
    $candidates = [System.Collections.Generic.List[object]]::new()
    foreach ($r in $resolvers) {
        $applies = $false
        try { $applies = & $r.test $c } catch { }
        if (-not $applies) { continue }
        try {
            $cand = & $r.invoke -Component $c -CacheDir $cacheDir -OsContext $scan.scan.os
            if ($cand) { $candidates.Add($cand) }
        } catch {
            $candidates.Add([ordered]@{ source = $r.name; trust = 'unknown'; status = 'error'; note = $_.Exception.Message })
        }
    }
    if ($candidates.Count -eq 0) { continue }   # no resolver covers this component yet

    $best = $candidates | Sort-Object @{ e = { $script:TrustRank[[string]$_.trust] } } -Descending | Select-Object -First 1
    # After downloading + installing, this command re-scans and confirms it landed.
    $verify = if ($best.status -in @('update-available','manual-check')) {
        ".\Confirm-Update.ps1 -Component `"$($c.component_key)`""
    } else { $null }
    $results.Add([ordered]@{
        component_key = $c.component_key
        category      = $c.category
        model         = $c.model
        status        = $best.status
        download_url   = "$($best.download_url)"
        verify_command = $verify
        best          = $best
        candidates    = $candidates
    })
}

# ---- USB peripherals (advisory, scan-wide rather than per-component) -----
if ($scan.devices -and (Get-Command Get-PeripheralAdvisories -ErrorAction SilentlyContinue)) {
    $peripherals = Get-PeripheralAdvisories -Devices $scan.devices
    foreach ($p in $peripherals) { $results.Add($p) }
}

# ---- report -------------------------------------------------------------
Write-Host ""
Write-Host ("  {0,-9} {1,-30} {2,-16} {3}" -f 'CATEGORY','MODEL','STATUS','DETAIL') -ForegroundColor DarkGray
foreach ($r in $results) {
    $color = switch ($r.status) {
        'update-available' { 'Yellow' }
        'current'          { 'Green' }
        default            { 'DarkGray' }
    }
    # vendor candidates compare by version; baseline compares by date.
    $from = if ($r.best.installed_market) { $r.best.installed_market }
            elseif ($r.best.installed_version) { $r.best.installed_version }
            elseif ($r.best.installed_date) { $r.best.installed_date }
            else { '?' }
    $to = if ($r.best.compared_by -eq 'release-date') { "{0} ({1})" -f $r.best.latest_date, $r.best.latest_version }
          else { $r.best.latest_version }
    $detail = switch ($r.status) {
        'update-available' { "{0} -> {1} [{2}/{3}]" -f $from, $to, $r.best.source, $r.best.trust }
        'current'          { "{0} is current [{1}/{2}]" -f $r.best.latest_version, $r.best.source, $r.best.trust }
        default            { [string]$r.best.note }
    }
    $model = if ($r.model.Length -gt 29) { $r.model.Substring(0,29) } else { $r.model }
    Write-Host ("  {0,-9} {1,-30} {2,-16} {3}" -f $r.category, $model, $r.status, $detail) -ForegroundColor $color
}

$updates = @($results | Where-Object status -eq 'update-available')
$manual  = @($results | Where-Object status -eq 'manual-check')
Write-Host ""
Write-Host ("  {0} component(s) checked, {1} update(s) available, {2} need a manual check." -f $results.Count, $updates.Count, $manual.Count) `
    -ForegroundColor $(if ($updates.Count) { 'Yellow' } else { 'Green' })
foreach ($u in $updates) {
    Write-Host ("    * {0}" -f $u.model) -ForegroundColor DarkYellow
    Write-Host ("        download: {0}" -f $u.best.download_url) -ForegroundColor DarkGray
    Write-Host ("        verify  : {0}" -f $u.verify_command) -ForegroundColor DarkGray
}
foreach ($u in $manual) {
    Write-Host ("    ? {0}" -f $u.model) -ForegroundColor DarkGray
    Write-Host ("        check   : {0}" -f $u.best.download_url) -ForegroundColor DarkGray
}
if ($updates.Count) {
    Write-Host ""
    Write-Host "  After installing, run .\Confirm-Update.ps1 to verify all updates landed." -ForegroundColor DarkGray
}

# ---- save report --------------------------------------------------------
if (-not $OutDir) { $OutDir = Join-Path $root 'reports' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$reportFile = Join-Path $OutDir ("report_{0}_{1}.json" -f $scan.scan.hostname, $stamp)
[ordered]@{
    schema_version = '1.0'
    generated_utc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    machine_id     = $scan.scan.machine_id
    based_on_scan  = [IO.Path]::GetFileName($ScanFile)
    results        = $results
} | ConvertTo-Json -Depth 10 | Set-Content -Path $reportFile -Encoding UTF8
Write-Host ""
Write-Host "  Report saved: $reportFile" -ForegroundColor Green
