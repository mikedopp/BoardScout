<#
    DriverScout library: offline hardware-ID name resolution.

    Resolves PCI VEN/DEV and USB VID/PID to human-readable vendor + device names
    using the maintained successors to the old pcidatabase.com:
        pci.ids  (https://pci-ids.ucw.cz / pciutils)
        usb.ids  (http://www.linux-usb.org)

    Both files share the same indentation grammar:
        VVVV<2 spaces>Vendor Name        (column 0)
        <TAB>DDDD<2 spaces>Device Name   (one tab = device/product under vendor)
    Class sections later in each file use other prefixes and are ignored.

    Dot-source this file, then:
        $db = Initialize-HwIdDb -CacheDir <dir>
        Resolve-PciDevice -Db $db -Ven 10de -Dev 2507
        Resolve-UsbDevice -Db $db -Vid 8087 -Pid 0029
#>

function Parse-IdsFile {
    param([string]$Path)
    $vendors = @{}
    $devices = @{}
    $cur = $null
    if (-not (Test-Path $Path)) { return @{ vendors = $vendors; devices = $devices } }
    switch -Regex -File $Path {
        '^([0-9a-fA-F]{4})  (.+)$'   { $cur = $Matches[1].ToLower(); $vendors[$cur] = $Matches[2].TrimEnd(); continue }
        '^\t([0-9a-fA-F]{4})  (.+)$' { if ($cur) { $devices["$cur`:$($Matches[1].ToLower())"] = $Matches[2].TrimEnd() }; continue }
    }
    return @{ vendors = $vendors; devices = $devices }
}

# Prefer a fresh cached copy; fall back to the vendored data/ snapshot (offline-safe).
function Get-IdsPath {
    param([string]$Name, [string]$CacheDir, [string]$DataDir)
    $c = Join-Path $CacheDir $Name
    if (Test-Path $c) { return $c }
    if ($DataDir) { $d = Join-Path $DataDir $Name; if (Test-Path $d) { return $d } }
    return $c
}

function Initialize-HwIdDb {
    param([string]$CacheDir, [string]$DataDir)
    $pci = Parse-IdsFile (Get-IdsPath 'pci.ids' $CacheDir $DataDir)
    $usb = Parse-IdsFile (Get-IdsPath 'usb.ids' $CacheDir $DataDir)
    return [pscustomobject]@{
        pciVendors = $pci.vendors; pciDevices = $pci.devices
        usbVendors = $usb.vendors; usbDevices = $usb.devices
        counts = [ordered]@{
            pci_vendors = $pci.vendors.Count; pci_devices = $pci.devices.Count
            usb_vendors = $usb.vendors.Count; usb_devices = $usb.devices.Count
        }
    }
}

function Resolve-PciDevice {
    param($Db, [string]$Ven, [string]$Dev)
    if (-not $Ven) { return $null }
    $v = $Ven.ToLower(); $d = ($Dev | ForEach-Object { $_.ToLower() })
    [ordered]@{
        vendor = $Db.pciVendors[$v]
        device = if ($d) { $Db.pciDevices["$v`:$d"] } else { $null }
    }
}

function Resolve-UsbDevice {
    param($Db, [string]$Vid, [string]$ProdId)   # 'ProdId' not 'Pid' -- $PID is a reserved automatic var
    if (-not $Vid) { return $null }
    $v = $Vid.ToLower(); $p = ($ProdId | ForEach-Object { $_.ToLower() })
    [ordered]@{
        vendor = $Db.usbVendors[$v]
        device = if ($p) { $Db.usbDevices["$v`:$p"] } else { $null }
    }
}

# Resolve directly from a Windows PNPDeviceID string (PCI or USB).
function Resolve-FromPnpId {
    param($Db, [string]$PnpId)
    if (-not $PnpId) { return $null }
    if ($PnpId -match 'VEN_([0-9A-Fa-f]{4})' ) {
        $ven = $Matches[1]
        $dev = if ($PnpId -match 'DEV_([0-9A-Fa-f]{4})') { $Matches[1] } else { $null }
        $r = Resolve-PciDevice -Db $Db -Ven $ven -Dev $dev
        return [ordered]@{ bus='PCI'; vendor_id=$ven.ToLower(); product_id=($dev | ForEach-Object { $_.ToLower() }); vendor=$r.vendor; device=$r.device }
    }
    if ($PnpId -match 'VID_([0-9A-Fa-f]{4})') {
        $vid = $Matches[1]
        $prod = if ($PnpId -match 'PID_([0-9A-Fa-f]{4})') { $Matches[1] } else { $null }
        $r = Resolve-UsbDevice -Db $Db -Vid $vid -ProdId $prod
        return [ordered]@{ bus='USB'; vendor_id=$vid.ToLower(); product_id=($prod | ForEach-Object { $_.ToLower() }); vendor=$r.vendor; device=$r.device }
    }
    return $null
}
