<#
    DriverScout resolver: SSD/NVMe firmware (trust=oem, status=manual-check)

    SSD firmware is the highest-risk update (a bad flash bricks the drive) and the
    least automatable: each vendor ships firmware only through its own proprietary
    tool (Crucial Storage Executive, WD Dashboard, Samsung Magician, ...), with no
    clean public version API. So this resolver does NOT auto-compare; it surfaces
    each internal SSD's installed firmware revision and routes it to the correct
    vendor tool / support page for a deliberate manual update.

    Only internal SSD/NVMe drives are considered -- USB enclosures and spinning
    HDDs are skipped (not meaningful firmware-update targets here).

    Convention functions:
        Test-Resolver_SsdFirmware   <component>
        Invoke-Resolver_SsdFirmware <component> -CacheDir <dir> -OsContext <obj>
#>

function Test-Resolver_SsdFirmware {
    param($Component)
    if ($Component.category -ne 'storage') { return $false }
    $bus   = "$($Component.lookup_hints.bus_type)"
    $media = "$($Component.lookup_hints.media_type)"
    return ($bus -in @('NVMe','SATA','RAID') -and $media -ne 'HDD')
}

function Invoke-Resolver_SsdFirmware {
    param($Component, [string]$CacheDir, $OsContext)

    $model = "$($Component.model)"
    $vt    = "$($Component.vendor) $model"

    # Route each drive to its vendor's firmware tool.
    $tool, $url = switch -Regex ($vt.Trim()) {
        'Crucial|CT\d{3}|Micron|P\dCR'  { 'Crucial Storage Executive', 'https://www.crucial.com/support/storage-executive'; break }
        'WD|Western Digital|^WDS|SN\d|SanDisk' { 'WD Dashboard', 'https://support.wdc.com/downloads.aspx?p=279'; break }
        'Samsung'                     { 'Samsung Magician', 'https://semiconductor.samsung.com/consumer-storage/support/tools/'; break }
        'Lexar'                       { 'Lexar firmware support', 'https://www.lexar.com/support/'; break }
        'Kingston'                    { 'Kingston SSD Manager', 'https://www.kingston.com/en/support/technical/ssdmanager'; break }
        'Seagate|FireCuda'            { 'SeaTools / Seagate firmware', 'https://www.seagate.com/support/downloads/'; break }
        default                       { 'Vendor firmware tool', ('https://www.google.com/search?q=' + [uri]::EscapeDataString("$model SSD firmware update")) }
    }

    return [ordered]@{
        source            = 'ssd-vendor'
        trust             = 'oem'
        status            = 'manual-check'
        installed_version = $Component.current.firmware
        note              = "SSD firmware: installed $($Component.current.firmware). Update only via $tool (flashing risk). Get it here:"
        download_url      = $url
        tool              = $tool
    }
}
