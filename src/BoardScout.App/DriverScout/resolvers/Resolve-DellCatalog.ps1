<#
    DriverScout resolver: Dell Command Update Catalog (trust=oem)

    Dell publishes a complete, structured XML catalog of every driver, BIOS, and
    firmware update for every Dell system: downloads.dell.com/catalog/CatalogPC.cab.
    No WAF, no auth, fully programmatic. 4000+ packages across 700+ system IDs.

    This resolver:
      1. Checks if the scanned machine is a Dell (system.oem.oem_brand == 'dell')
      2. Downloads + caches the catalog cab (24h TTL, ~3MB compressed → ~57MB XML)
      3. Filters packages by the machine's Dell SystemID (SKU from SMBIOS)
      4. For each DriverScout component, finds matching Dell packages by category
         and compares versions

    Convention functions:
        Test-Resolver_DellCatalog   <component>
        Invoke-Resolver_DellCatalog <component> -CacheDir <dir> -OsContext <obj>
#>

$script:DellCatalogUrl = 'https://downloads.dell.com/catalog/CatalogPC.cab'
$script:DellDownloadBase = 'https://downloads.dell.com/'
$script:DellCatalogCache = $null

function Get-DellCatalog {
    param([string]$CacheDir)
    if ($script:DellCatalogCache) { return $script:DellCatalogCache }

    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $xmlPath = Join-Path $CacheDir 'CatalogPC.xml'

    if ((Test-Path $xmlPath) -and ((Get-Date) - (Get-Item $xmlPath).LastWriteTime).TotalHours -lt 24) {
        $script:DellCatalogCache = [xml]([IO.File]::ReadAllText($xmlPath, [Text.Encoding]::Unicode))
        return $script:DellCatalogCache
    }

    $cabPath = Join-Path $CacheDir 'CatalogPC.cab'
    Invoke-WebRequest -Uri $script:DellCatalogUrl -OutFile $cabPath -UseBasicParsing -TimeoutSec 120

    $extractDir = Join-Path $CacheDir 'dell_extract'
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    & expand.exe $cabPath -F:* $extractDir 2>$null | Out-Null

    $extracted = Get-ChildItem $extractDir -File | Select-Object -First 1
    if (-not $extracted) { throw 'Dell catalog cab extraction produced no files' }

    $head = [IO.File]::ReadAllBytes($extracted.FullName) | Select-Object -First 10
    $headStr = [Text.Encoding]::ASCII.GetString($head)
    if ($headStr -match 'x.m.l' -or $headStr -match '<') {
        Copy-Item $extracted.FullName $xmlPath -Force
    } else {
        throw "Unexpected file format inside Dell catalog cab"
    }

    $script:DellCatalogCache = [xml]([IO.File]::ReadAllText($xmlPath, [Text.Encoding]::Unicode))
    return $script:DellCatalogCache
}

# Map DriverScout categories to Dell catalog category tokens.
$script:DellCategoryMap = @{
    'bios'    = @('BIOS')
    'gpu'     = @('Video')
    'network' = @('Network')
    'storage' = @('Serial ATA', 'RAID', 'Storage Controller')
    'audio'   = @('Audio')
    'chipset' = @('Chipset')
}

function Test-Resolver_DellCatalog {
    param($Component)
    # Only fire if we have OEM context in the scan indicating this is a Dell machine.
    # The orchestrator passes OsContext but not the full scan; we stash OEM info
    # in $script scope during the first call. See Invoke below.
    return ($script:DellOemBrand -eq 'dell' -and
            $Component.category -in @('bios','gpu','network','storage','audio','chipset'))
}

function Invoke-Resolver_DellCatalog {
    param($Component, [string]$CacheDir, $OsContext)

    $cat = Get-DellCatalog -CacheDir $CacheDir
    $sysId = $script:DellSystemId
    if (-not $sysId) {
        return [ordered]@{ source = 'dell-catalog'; trust = 'oem'; status = 'unknown'; note = 'no Dell SystemID in scan' }
    }

    $dellCats = $script:DellCategoryMap[$Component.category]
    if (-not $dellCats) {
        return [ordered]@{ source = 'dell-catalog'; trust = 'oem'; status = 'unknown'; note = "no Dell category mapping for $($Component.category)" }
    }

    # Filter catalog: packages that support this SystemID AND match the category.
    $matches = $cat.Manifest.SoftwareComponent | Where-Object {
        $catName = $_.Category.Display.'#cdata-section'
        ($catName -in $dellCats) -and
        ($_.SupportedSystems.Brand.Model.systemID -contains $sysId)
    }

    if (-not $matches -or @($matches).Count -eq 0) {
        return [ordered]@{ source = 'dell-catalog'; trust = 'oem'; status = 'unknown'; note = "no Dell packages for SystemID=$sysId category=$($Component.category)" }
    }

    # Pick the newest by releaseDate.
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $parsed = foreach ($m in @($matches)) {
        $d = $null
        try { $d = [datetime]::Parse($m.releaseDate, $inv) } catch {}
        if ($d) {
            [pscustomobject]@{
                name    = $m.Name.Display.'#cdata-section'
                version = $m.vendorVersion
                date    = $d
                url     = "$($script:DellDownloadBase)$($m.path)"
            }
        }
    }
    $parsed = @($parsed | Sort-Object date -Descending)
    if ($parsed.Count -eq 0) {
        return [ordered]@{ source = 'dell-catalog'; trust = 'oem'; status = 'unknown'; note = 'no parseable Dell packages' }
    }
    $latest = $parsed[0]

    $installedVer = if ($Component.category -eq 'bios') { $Component.current.firmware } else { $Component.current.driver_version }
    $installedDate = $null
    $dateField = if ($Component.category -eq 'bios') { $Component.current.firmware_date } else { $Component.current.driver_date }
    if ($dateField) { try { $installedDate = [datetime]::Parse($dateField, $inv) } catch {} }

    $status =
        if (-not $installedVer -and -not $installedDate) { 'unknown' }
        elseif ($installedDate -and $latest.date -gt $installedDate.AddDays(1)) { 'update-available' }
        elseif ($installedVer -and $latest.version -and $latest.version -ne $installedVer) { 'update-available' }
        else { 'current' }

    return [ordered]@{
        source            = 'dell-catalog'
        trust             = 'oem'
        status            = $status
        compared_by       = if ($installedDate) { 'release-date' } else { 'version-string' }
        installed_version = $installedVer
        installed_date    = $dateField
        latest_version    = $latest.version
        latest_date       = $latest.date.ToString('yyyy-MM-dd')
        latest_name       = $latest.name
        download_url      = $latest.url
        dell_system_id    = $sysId
    }
}
