<#
    DriverScout resolver: NVIDIA GPU (vendor-direct, trust=vendor)

    Dot-source this file to register the resolver. It exposes two functions the
    orchestrator looks for by convention:
        Test-Resolver_NvidiaGpu  <component>   -> [bool]  does this resolver apply?
        Invoke-Resolver_NvidiaGpu <component>  -> candidate object (or $null)

    Method (no fragile HTML scraping):
      - Map GPU model -> NVIDIA pfid via community-maintained gpu-data.json
      - Map OS -> osID via os-data.json
      - Query NVIDIA's AjaxDriverService DriverManualLookup for the latest driver
    Data files are cached locally to cut repeat network calls.
#>

$script:NvBase    = 'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php'
$script:NvGpuData = 'https://raw.githubusercontent.com/ZenitH-AT/nvidia-data/main/gpu-data.json'
$script:NvOsData  = 'https://raw.githubusercontent.com/ZenitH-AT/nvidia-data/main/os-data.json'

# Cached fetch of a JSON data file (24h TTL) -> reduces "hard lookups".
function Get-CachedJson {
    param([string]$Url, [string]$CacheDir, [int]$TtlHours = 24)
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $name = ([IO.Path]::GetFileName(($Url -split '\?')[0]))
    $path = Join-Path $CacheDir $name
    if ((Test-Path $path) -and ((Get-Date) - (Get-Item $path).LastWriteTime).TotalHours -lt $TtlHours) {
        return (Get-Content $path -Raw | ConvertFrom-Json)
    }
    $data = Invoke-RestMethod -Uri $Url -TimeoutSec 25
    $data | ConvertTo-Json -Depth 12 | Set-Content -Path $path -Encoding UTF8
    return $data
}

# Windows NVIDIA driver version (e.g. 32.0.15.9597) -> marketing version (595.97).
# Rule: take the digits of the version, last 5 form NNN.NN.
function ConvertTo-NvidiaMarketingVersion {
    param([string]$WinVersion)
    if (-not $WinVersion) { return $null }
    $digits = ($WinVersion -replace '\D', '')
    if ($digits.Length -lt 5) { return $WinVersion }
    $last5 = $digits.Substring($digits.Length - 5)
    return ('{0}.{1}' -f $last5.Substring(0, 3), $last5.Substring(3, 2))
}

# Compare two NVIDIA marketing versions ("610.47" > "595.97").
function Compare-NvidiaVersion {
    param([string]$A, [string]$B)   # returns 1 if A>B, 0 if equal, -1 if A<B
    $na = [double]($A -replace '[^\d.]', '')
    $nb = [double]($B -replace '[^\d.]', '')
    if ($na -gt $nb) { return 1 } elseif ($na -lt $nb) { return -1 } else { return 0 }
}

function Test-Resolver_NvidiaGpu {
    param($Component)
    return ($Component.category -eq 'gpu' -and
            ($Component.vendor -match 'NVIDIA' -or $Component.model -match 'NVIDIA|GeForce|Quadro|RTX|GTX'))
}

function Invoke-Resolver_NvidiaGpu {
    param($Component, [string]$CacheDir, $OsContext)

    $gpuData = Get-CachedJson -Url $script:NvGpuData -CacheDir $CacheDir
    $osData  = Get-CachedJson -Url $script:NvOsData  -CacheDir $CacheDir

    # Resolve pfid by matching the model name across gpu-data type buckets.
    # Strip the "NVIDIA GeForce " prefix Windows adds; prefer desktop bucket.
    $name = ($Component.model -replace '^NVIDIA\s+', '').Trim()
    $pfid = $null
    foreach ($type in @('desktop', 'notebook') + $gpuData.PSObject.Properties.Name) {
        if (-not $gpuData.$type) { continue }
        $prop = $gpuData.$type.PSObject.Properties | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if ($prop) { $pfid = $prop.Value; break }
    }
    if (-not $pfid) {
        return [ordered]@{ source = 'nvidia'; trust = 'vendor'; status = 'unknown'
                           note = "no pfid mapping for '$name'" }
    }

    # Resolve osID from the scan's OS caption/arch.
    $osBits = if ($OsContext.arch -match '64') { '64-bit' } else { '32-bit' }
    $osName = if ($OsContext.caption -match 'Windows 11') { 'Windows 11' } else { 'Windows 10' }
    $osID = ($osData | Where-Object { $_.name -match [regex]::Escape($osName) -and $_.name -match $osBits } |
                Select-Object -First 1).id
    if (-not $osID) { $osID = '57' }   # sane default: Win10/11 64-bit

    $url = "$($script:NvBase)?func=DriverManualLookup&pfid=$pfid&osID=$osID&dch=1"
    $resp = Invoke-RestMethod -Uri $url -TimeoutSec 25
    $info = $resp.IDS[0].downloadInfo
    if (-not $info.Version) {
        return [ordered]@{ source = 'nvidia'; trust = 'vendor'; status = 'unknown'; note = 'empty lookup result' }
    }

    $installedMkt = ConvertTo-NvidiaMarketingVersion $Component.current.driver_version
    $cmp = if ($installedMkt) { Compare-NvidiaVersion $info.Version $installedMkt } else { $null }
    $status = if ($cmp -eq $null) { 'unknown' } elseif ($cmp -gt 0) { 'update-available' } else { 'current' }

    return [ordered]@{
        source            = 'nvidia'
        trust             = 'vendor'
        status            = $status
        installed_version = $Component.current.driver_version
        installed_market  = $installedMkt
        latest_version    = $info.Version
        release_date      = $info.ReleaseDateTime
        download_url      = $info.DownloadURL
        pfid              = $pfid
        os_id             = $osID
    }
}
