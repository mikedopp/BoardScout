<#
    DriverScout resolver: Lenovo TVSU Catalog (trust=oem)

    Lenovo publishes per-machine-type XML catalogs at:
        download.lenovo.com/catalog/{machineType}_Win10.xml

    Each catalog lists ~30-50 package descriptors (BIOS, chipset, video, audio,
    networking, firmware). Each descriptor XML has version, release date, severity,
    and download filename. No auth, no WAF, fully programmatic.

    Machine type is the 4-character model prefix from Win32_ComputerSystem.Model
    (e.g. "21AH" for ThinkPad T14 Gen 3). The scanner's OEM detection already
    extracts this as oem_system_id when manufacturer matches "Lenovo".

    Convention functions:
        Test-Resolver_LenovoCatalog   <component>
        Invoke-Resolver_LenovoCatalog <component> -CacheDir <dir> -OsContext <obj>
#>

$script:LenovoCatalogCache = @{}

$script:LenovoCategoryMap = @{
    'bios'    = @('BIOS UEFI')
    'gpu'     = @('Display and Video Graphics')
    'network' = @('Networking Wireless LAN','Networking LAN Ethernet','Networking Wireless WAN')
    'storage' = @('Storage')
    'audio'   = @('Audio')
    'chipset' = @('Motherboard Devices Backplanes core chipset onboard video PCIe switches')
}

function Get-LenovoCatalog {
    param([string]$MachineType, [string]$CacheDir)
    if ($script:LenovoCatalogCache[$MachineType]) { return $script:LenovoCatalogCache[$MachineType] }

    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $cachePath = Join-Path $CacheDir "lenovo_${MachineType}.xml"

    $content = $null
    if ((Test-Path $cachePath) -and ((Get-Date) - (Get-Item $cachePath).LastWriteTime).TotalHours -lt 24) {
        $content = Get-Content $cachePath -Raw -Encoding UTF8
    } else {
        foreach ($os in @('Win11','Win10')) {
            $url = "https://download.lenovo.com/catalog/${MachineType}_${os}.xml"
            try {
                $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 20
                $content = $r.Content -replace '^﻿',''
                $content | Set-Content $cachePath -Encoding UTF8
                break
            } catch { }
        }
    }
    if (-not $content) { return $null }

    [xml]$xml = $content
    $script:LenovoCatalogCache[$MachineType] = $xml
    return $xml
}

function Get-LenovoPackageDetail {
    param([string]$DescriptorUrl, [string]$CacheDir)
    $key = [IO.Path]::GetFileNameWithoutExtension(([uri]$DescriptorUrl).Segments[-1])
    $cachePath = Join-Path $CacheDir "lenovo_pkg_${key}.xml"

    $content = $null
    if ((Test-Path $cachePath) -and ((Get-Date) - (Get-Item $cachePath).LastWriteTime).TotalHours -lt 24) {
        $content = Get-Content $cachePath -Raw -Encoding UTF8
    } else {
        $r = Invoke-WebRequest $DescriptorUrl -UseBasicParsing -TimeoutSec 15
        $content = $r.Content -replace '^﻿',''
        $content | Set-Content $cachePath -Encoding UTF8
    }
    [xml]$xml = $content
    return $xml.Package
}

function Test-Resolver_LenovoCatalog {
    param($Component)
    return ($script:DellOemBrand -eq 'lenovo' -and
            $Component.category -in @('bios','gpu','network','storage','audio','chipset'))
}

function Invoke-Resolver_LenovoCatalog {
    param($Component, [string]$CacheDir, $OsContext)

    $mt = "$($script:OemInfo.oem_system_id)"
    if (-not $mt) {
        return [ordered]@{ source = 'lenovo-catalog'; trust = 'oem'; status = 'unknown'; note = 'no Lenovo machine type in scan' }
    }

    $cat = Get-LenovoCatalog -MachineType $mt -CacheDir $CacheDir
    if (-not $cat) {
        return [ordered]@{ source = 'lenovo-catalog'; trust = 'oem'; status = 'unknown'; note = "no Lenovo catalog for machine type $mt" }
    }

    $targetCats = $script:LenovoCategoryMap[$Component.category]
    if (-not $targetCats) {
        return [ordered]@{ source = 'lenovo-catalog'; trust = 'oem'; status = 'unknown'; note = "no Lenovo category mapping for $($Component.category)" }
    }

    $packages = @($cat.packages.package | Where-Object { $_.category -in $targetCats })
    if ($packages.Count -eq 0) {
        return [ordered]@{ source = 'lenovo-catalog'; trust = 'oem'; status = 'unknown'; note = "no Lenovo packages for category=$($Component.category) machine=$mt" }
    }

    $inv = [Globalization.CultureInfo]::InvariantCulture
    $best = $null
    $bestDate = [datetime]::MinValue
    foreach ($p in $packages) {
        try {
            $detail = Get-LenovoPackageDetail -DescriptorUrl $p.location -CacheDir $CacheDir
            $ver = "$($detail.version)"
            $dStr = "$($detail.ReleaseDate)"
            $d = $null
            if ($dStr) { try { $d = [datetime]::Parse($dStr, $inv) } catch {} }

            $title = "$($detail.Title.InnerText)"
            if (-not $title) { $title = "$($detail.Title.'#cdata-section')" }
            if (-not $title) { $title = "$($detail.Title)" }

            $dlFile = "$($detail.Files.Installer.File.Name)"
            $dlBase = $p.location -replace '/[^/]+$','/'

            if ($d -and $d -gt $bestDate) {
                $bestDate = $d
                $best = [pscustomobject]@{
                    name    = $title
                    version = $ver
                    date    = $d
                    url     = if ($dlFile) { "${dlBase}${dlFile}" } else { $p.location }
                }
            }
        } catch { }
    }

    if (-not $best) {
        return [ordered]@{ source = 'lenovo-catalog'; trust = 'oem'; status = 'unknown'; note = 'no parseable Lenovo package descriptors' }
    }

    $installedVer = if ($Component.category -eq 'bios') { $Component.current.firmware } else { $Component.current.driver_version }
    $installedDate = $null
    $dateField = if ($Component.category -eq 'bios') { $Component.current.firmware_date } else { $Component.current.driver_date }
    if ($dateField) { try { $installedDate = [datetime]::Parse($dateField, $inv) } catch {} }

    $status =
        if (-not $installedVer -and -not $installedDate) { 'unknown' }
        elseif ($installedDate -and $best.date -gt $installedDate.AddDays(1)) { 'update-available' }
        elseif ($installedVer -and $best.version -and $best.version -ne $installedVer) { 'update-available' }
        else { 'current' }

    return [ordered]@{
        source            = 'lenovo-catalog'
        trust             = 'oem'
        status            = $status
        compared_by       = if ($installedDate) { 'release-date' } else { 'version-string' }
        installed_version = $installedVer
        installed_date    = $dateField
        latest_version    = $best.version
        latest_date       = $best.date.ToString('yyyy-MM-dd')
        latest_name       = $best.name
        download_url      = $best.url
        lenovo_machine_type = $mt
    }
}
