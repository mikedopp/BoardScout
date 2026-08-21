<#
    DriverScout resolver: Router & network device firmware (trust=vendor/oem)

    Checks firmware updates for routers, switches, access points, and NAS
    devices discovered by Invoke-NetworkScan.ps1.

    Vendor coverage:
      - Ubiquiti  — fw-update.ubnt.com API (actual version comparison)
      - MikroTik  — upgrade.mikrotik.com version file (actual comparison)
      - ASUS      — advisory link to support page
      - TP-Link   — advisory link to support page
      - Netgear   — advisory link to support page
      - Linksys   — advisory link to support page
      - Synology  — advisory link to DSM download
      - pfSense   — advisory link to releases page

    Convention functions:
        Test-Resolver_RouterFirmware   <component>
        Invoke-Resolver_RouterFirmware <component> -CacheDir <dir> -OsContext <obj>
#>

function Test-Resolver_RouterFirmware {
    param($Component)
    return ($Component.category -in @('router','access_point','switch','nas','network_device'))
}

function Get-UbiquitiFirmware {
    param([string]$Model, [string]$CacheDir)
    $product = $Model -replace '\s',''
    $cachePath = Join-Path $CacheDir "ubnt_fw_${product}.json"
    if ((Test-Path $cachePath) -and ((Get-Date) - (Get-Item $cachePath).LastWriteTime).TotalHours -lt 24) {
        return (Get-Content $cachePath -Raw | ConvertFrom-Json)
    }
    try {
        $url = "https://fw-update.ubnt.com/api/firmware-latest?filter=eq~~product~~${product}&filter=eq~~channel~~release"
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10
        $r.Content | Set-Content $cachePath -Encoding UTF8
        return ($r.Content | ConvertFrom-Json)
    } catch { return $null }
}

function Get-MikroTikLatest {
    param([string]$CacheDir)
    $cachePath = Join-Path $CacheDir 'mikrotik_latest.txt'
    if ((Test-Path $cachePath) -and ((Get-Date) - (Get-Item $cachePath).LastWriteTime).TotalHours -lt 24) {
        return (Get-Content $cachePath -Raw).Trim()
    }
    try {
        $r = Invoke-WebRequest 'https://upgrade.mikrotik.com/routeros/NEWESTa7.stable' -UseBasicParsing -TimeoutSec 10
        $ver = ($r.Content -split '\r?\n')[0].Trim()
        $ver | Set-Content $cachePath -Encoding UTF8
        return $ver
    } catch { return $null }
}

$script:VendorFirmwareUrls = @{
    'ASUS'     = 'https://www.asus.com/support/download-center/'
    'TP-Link'  = 'https://www.tp-link.com/us/support/download/'
    'Netgear'  = 'https://www.netgear.com/support/product/'
    'Linksys'  = 'https://www.linksys.com/support-article?articleNum=48866'
    'Synology' = 'https://www.synology.com/en-us/support/download'
    'QNAP'     = 'https://www.qnap.com/en/download'
    'pfSense'  = 'https://www.pfsense.org/download/'
    'OPNsense' = 'https://opnsense.org/download/'
}

function Invoke-Resolver_RouterFirmware {
    param($Component, [string]$CacheDir, $OsContext)

    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $vendor = "$($Component.vendor)"
    $model  = "$($Component.model)"
    $fw     = "$($Component.current.firmware)"

    # ---- Ubiquiti: real API check ------------------------------------------
    if ($vendor -eq 'Ubiquiti') {
        $ubnt = Get-UbiquitiFirmware -Model $model -CacheDir $CacheDir
        if ($ubnt -and $ubnt._embedded -and $ubnt._embedded.firmware) {
            $latest = $ubnt._embedded.firmware | Select-Object -First 1
            $latestVer = "$($latest.version)"
            $status = if (-not $fw) { 'manual-check' }
                      elseif ($latestVer -ne $fw) { 'update-available' }
                      else { 'current' }
            return [ordered]@{
                source            = 'ubiquiti-api'
                trust             = 'vendor'
                status            = $status
                compared_by       = 'version-string'
                installed_version = $fw
                latest_version    = $latestVer
                latest_name       = "$model firmware"
                download_url      = "https://www.ui.com/download/releases/$($model -replace '\s','-')"
            }
        }
        return [ordered]@{
            source       = 'ubiquiti-api'
            trust        = 'vendor'
            status       = 'manual-check'
            note         = "Check Ubiquiti firmware for $model"
            download_url = 'https://www.ui.com/download/'
        }
    }

    # ---- MikroTik: version file check --------------------------------------
    if ($vendor -eq 'MikroTik') {
        $latest = Get-MikroTikLatest -CacheDir $CacheDir
        if ($latest) {
            $status = if (-not $fw) { 'manual-check' }
                      elseif ($latest -ne $fw) { 'update-available' }
                      else { 'current' }
            return [ordered]@{
                source            = 'mikrotik-api'
                trust             = 'vendor'
                status            = $status
                compared_by       = 'version-string'
                installed_version = $fw
                latest_version    = $latest
                latest_name       = "RouterOS $latest (stable)"
                download_url      = 'https://mikrotik.com/download'
            }
        }
    }

    # ---- All other vendors: advisory with link to firmware page -------------
    $url = $script:VendorFirmwareUrls[$vendor]
    if (-not $url) { $url = "https://www.google.com/search?q=$([uri]::EscapeDataString("$vendor $model firmware update"))" }

    $note = "Check $vendor for $model firmware updates"
    if ($fw) { $note += " (installed: $fw)" }

    return [ordered]@{
        source       = 'router-firmware'
        trust        = 'oem'
        status       = 'manual-check'
        note         = $note
        installed_version = $fw
        download_url = $url
    }
}
