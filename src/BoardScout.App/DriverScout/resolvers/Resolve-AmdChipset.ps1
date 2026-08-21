<#
    DriverScout resolver: AMD chipset drivers (trust=vendor)

    AMD's driver pages are server-side rendered HTML with version, date, and
    direct download URL all in plain markup — no JS rendering needed, no WAF.

    URL patterns:
      Chipset: amd.com/en/support/downloads/drivers.html/chipsets/am4/b550.html
      APU GPU: amd.com/en/support/downloads/drivers.html/processors/ryzen/ryzen-5000-series/amd-ryzen-7-5700g.html

    Audio package and device-driver versions use different version schemes, so
    this resolver intentionally does not compare AMD audio components against a
    chipset package version.

    Convention functions:
        Test-Resolver_AmdChipset   <component>
        Invoke-Resolver_AmdChipset <component> -CacheDir <dir> -OsContext <obj>
#>

$script:AmdChipsetMap = @{
    'B550'  = 'chipsets/am4/b550'
    'B450'  = 'chipsets/am4/b450'
    'X570'  = 'chipsets/am4/x570'
    'X470'  = 'chipsets/am4/x470'
    'A520'  = 'chipsets/am4/a520'
    'B650'  = 'chipsets/am5/b650'
    'B650E' = 'chipsets/am5/b650e'
    'X670'  = 'chipsets/am5/x670'
    'X670E' = 'chipsets/am5/x670e'
    'B850'  = 'chipsets/am5/b850'
    'B840'  = 'chipsets/am5/b840'
    'X870'  = 'chipsets/am5/x870'
    'X870E' = 'chipsets/am5/x870e'
}

$script:AmdApuMap = @{
    'AMD Ryzen 7 5700G'  = 'processors/ryzen/ryzen-5000-series/amd-ryzen-7-5700g'
    'AMD Ryzen 5 5600G'  = 'processors/ryzen/ryzen-5000-series/amd-ryzen-5-5600g'
    'AMD Ryzen 3 5300G'  = 'processors/ryzen/ryzen-5000-series/amd-ryzen-3-5300g'
    'AMD Ryzen 7 5700GE' = 'processors/ryzen/ryzen-5000-series/amd-ryzen-7-5700ge'
    'AMD Ryzen 5 5600GE' = 'processors/ryzen/ryzen-5000-series/amd-ryzen-5-5600ge'
    'AMD Ryzen 7 8700G'  = 'processors/ryzen/ryzen-8000-series/amd-ryzen-7-8700g'
    'AMD Ryzen 5 8600G'  = 'processors/ryzen/ryzen-8000-series/amd-ryzen-5-8600g'
    'AMD Ryzen 5 8500G'  = 'processors/ryzen/ryzen-8000-series/amd-ryzen-5-8500g'
    'AMD Ryzen 9 7950X3D' = 'processors/ryzen/ryzen-9000-series/amd-ryzen-9-7950x3d'
}

function Get-AmdDriverPage {
    param([string]$UrlPath, [string]$CacheDir)
    $cacheKey = ($UrlPath -replace '[/\\]','_') + '.html'
    $cachePath = Join-Path $CacheDir $cacheKey
    if ((Test-Path $cachePath) -and ((Get-Date) - (Get-Item $cachePath).LastWriteTime).TotalHours -lt 24) {
        return (Get-Content $cachePath -Raw -Encoding UTF8)
    }
    $url = "https://www.amd.com/en/support/downloads/drivers.html/$UrlPath.html"
    $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 30
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $r.Content | Set-Content $cachePath -Encoding UTF8
    return $r.Content
}

function Get-AmdDriverEntries {
    param([string]$Html)
    $blocks = [regex]::Matches($Html, '<article class="container-fluid driver-download-details"[^>]*>([\s\S]*?)</article>')
    $seen = @{}
    foreach ($b in $blocks) {
        $text = $b.Groups[1].Value
        $name = if ($text -match '<h4>([^<]+)</h4>') { $Matches[1].Trim() }
        $ver  = if ($text -match 'Revision Number</strong>\s*<p>([^<]+)</p>') { $Matches[1].Trim() }
        $date = if ($text -match 'Release Date</strong>\s*<p>\s*(\d{4}-\d{2}-\d{2})\s*</p>') { $Matches[1] }
        $dl   = if ($text -match 'href="(https://drivers\.amd\.com[^"]+)"') { $Matches[1] }
        if ($name -and $ver -and -not $seen[$name]) {
            $seen[$name] = $true
            [pscustomobject]@{ name = $name; version = $ver; date = $date; url = $dl }
        }
    }
}

function Test-Resolver_AmdChipset {
    param($Component)
    return $Component.category -eq 'chipset'
}

function Invoke-Resolver_AmdChipset {
    param($Component, [string]$CacheDir, $OsContext)

    $boardModel = "$($script:OemInfo.model)"
    if (-not $boardModel) {
        $f = Get-ChildItem (Join-Path (Split-Path $CacheDir) 'scans') -Filter 'scan_*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($f) {
            $scan = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $boardModel = "$($scan.system.baseboard.product)"
        }
    }

    $chipset = $null
    foreach ($k in $script:AmdChipsetMap.Keys) {
        if ($boardModel -match $k) { $chipset = $k; break }
    }
    if (-not $chipset) {
        return [ordered]@{ source = 'amd'; trust = 'vendor'; status = 'unknown'; note = "cannot determine AMD chipset from board model '$boardModel'" }
    }

    $urlPath = $script:AmdChipsetMap[$chipset]
    try {
        $html = Get-AmdDriverPage -UrlPath $urlPath -CacheDir $CacheDir
    } catch {
        return [ordered]@{ source = 'amd'; trust = 'vendor'; status = 'unknown'; note = "AMD page fetch failed: $($_.Exception.Message)" }
    }

    $entries = @(Get-AmdDriverEntries -Html $html)
    if ($entries.Count -eq 0) {
        return [ordered]@{ source = 'amd'; trust = 'vendor'; status = 'unknown'; note = 'no driver entries found on AMD page' }
    }

    $target = $entries | Where-Object { $_.name -match 'Chipset' } | Select-Object -First 1
    if (-not $target) { $target = $entries[0] }

    $installedVer = $Component.current.driver_version
    $status = if (-not $installedVer) { 'unknown' }
              elseif ($target.version -ne $installedVer) { 'update-available' }
              else { 'current' }

    return [ordered]@{
        source            = 'amd'
        trust             = 'vendor'
        status            = $status
        compared_by       = 'version-string'
        installed_version = $installedVer
        latest_version    = $target.version
        latest_date       = $target.date
        latest_name       = $target.name
        download_url      = $target.url
        amd_chipset       = $chipset
    }
}
