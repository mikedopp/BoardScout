<#
    DriverScout resolver: AMD Radeon GPU / APU integrated graphics (trust=vendor)

    Covers:
      - Ryzen APU integrated Radeon Graphics (5700G, 5600G, 8700G, etc.)
      - Discrete Radeon RX GPUs (RX 7900, RX 7600, etc.)

    AMD publishes Adrenalin driver version + download link on a server-rendered
    page per CPU/GPU model. No JS rendering, no WAF.

    Convention functions:
        Test-Resolver_AmdGpu   <component>
        Invoke-Resolver_AmdGpu <component> -CacheDir <dir> -OsContext <obj>
#>

$script:AmdGpuUrlPatterns = @{
    'Radeon RX 7900 XTX' = 'graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7900-xtx'
    'Radeon RX 7900 XT'  = 'graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7900-xt'
    'Radeon RX 7800 XT'  = 'graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7800-xt'
    'Radeon RX 7700 XT'  = 'graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7700-xt'
    'Radeon RX 7600 XT'  = 'graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7600-xt'
    'Radeon RX 7600'     = 'graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7600'
    'Radeon RX 6950 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6950-xt'
    'Radeon RX 6900 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6900-xt'
    'Radeon RX 6800 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6800-xt'
    'Radeon RX 6800'     = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6800'
    'Radeon RX 6750 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6750-xt'
    'Radeon RX 6700 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6700-xt'
    'Radeon RX 6650 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6650-xt'
    'Radeon RX 6600 XT'  = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6600-xt'
    'Radeon RX 6600'     = 'graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6600'
    'Radeon RX 9070 XT'  = 'graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070-xt'
    'Radeon RX 9070'     = 'graphics/radeon-rx/radeon-rx-9000-series/amd-radeon-rx-9070'
}

function Get-ApuUrlPath {
    param([string]$CpuName)
    foreach ($k in $script:AmdApuMap.Keys) {
        if ($CpuName -match [regex]::Escape($k)) { return $script:AmdApuMap[$k] }
    }
    return $null
}

function Test-Resolver_AmdGpu {
    param($Component)
    if ($Component.category -ne 'gpu') { return $false }
    return ("$($Component.vendor)$($Component.model)" -match 'AMD|ATI|Radeon')
}

function Invoke-Resolver_AmdGpu {
    param($Component, [string]$CacheDir, $OsContext)

    $model = "$($Component.model)"
    $urlPath = $null

    foreach ($k in $script:AmdGpuUrlPatterns.Keys) {
        if ($model -match [regex]::Escape($k)) { $urlPath = $script:AmdGpuUrlPatterns[$k]; break }
    }

    if (-not $urlPath -and $model -match 'Radeon Graphics|Radeon Vega') {
        $cpuName = $null
        $scanDir = Join-Path (Split-Path $CacheDir) 'scans'
        $f = Get-ChildItem $scanDir -Filter 'scan_*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($f) {
            $scan = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $cpuName = "$($scan.system.cpu.name)"
            if ($scan.system.cpu -is [array]) { $cpuName = "$($scan.system.cpu[0].name)" }
        }
        if ($cpuName) { $urlPath = Get-ApuUrlPath $cpuName }
    }

    if (-not $urlPath) {
        return [ordered]@{ source = 'amd'; trust = 'vendor'; status = 'unknown'; note = "no AMD driver page mapping for '$model'" }
    }

    try {
        $html = Get-AmdDriverPage -UrlPath $urlPath -CacheDir $CacheDir
    } catch {
        return [ordered]@{ source = 'amd'; trust = 'vendor'; status = 'unknown'; note = "AMD page fetch failed: $($_.Exception.Message)" }
    }

    $entries = @(Get-AmdDriverEntries -Html $html)
    $target = $entries | Where-Object { $_.name -match 'Adrenalin' } | Select-Object -First 1
    if (-not $target -and $entries.Count -gt 0) { $target = $entries[0] }
    if (-not $target) {
        return [ordered]@{ source = 'amd'; trust = 'vendor'; status = 'unknown'; note = 'no Adrenalin entry found on AMD page' }
    }

    $installedVer = $Component.current.driver_version
    $latestVer = $target.version -replace '\s*\(.*\)',''

    $status = if (-not $installedVer) { 'unknown' }
              elseif ($latestVer -ne $installedVer) { 'update-available' }
              else { 'current' }

    return [ordered]@{
        source            = 'amd'
        trust             = 'vendor'
        status            = $status
        compared_by       = 'version-string'
        installed_version = $installedVer
        latest_version    = $latestVer
        latest_date       = $target.date
        latest_name       = $target.name
        download_url      = $target.url
    }
}
