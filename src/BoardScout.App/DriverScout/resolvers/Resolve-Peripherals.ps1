<#
    DriverScout resolver: USB peripherals (trust=oem, status=manual-check)

    Peripheral firmware (mice, keyboards, headsets, webcams, controllers) is
    delivered exclusively through vendor-specific desktop apps with no public
    version API. This resolver identifies known vendor peripherals from the USB
    device list and routes each to the correct management tool for manual check.

    Covers: Logitech, Razer, Corsair, SteelSeries, HyperX, Elgato.

    Convention functions:
        Test-Resolver_Peripherals   <component>
        Invoke-Resolver_Peripherals <component> -CacheDir <dir> -OsContext <obj>
#>

# USB Vendor IDs for major peripheral brands.
$script:PeripheralVendors = @{
    '046D' = @{ brand = 'Logitech';    tool = 'Logitech G Hub / Options+'; url = 'https://www.logitechg.com/en-us/innovation/g-hub.html' }
    '1532' = @{ brand = 'Razer';       tool = 'Razer Synapse';             url = 'https://www.razer.com/synapse-3' }
    '1B1C' = @{ brand = 'Corsair';     tool = 'Corsair iCUE';              url = 'https://www.corsair.com/us/en/s/icue' }
    '1038' = @{ brand = 'SteelSeries'; tool = 'SteelSeries GG';            url = 'https://steelseries.com/gg' }
    '0951' = @{ brand = 'HyperX';      tool = 'HyperX NGENUITY';           url = 'https://hyperx.com/pages/ngenuity' }
    '0FD9' = @{ brand = 'Elgato';      tool = 'Elgato Control Center';     url = 'https://www.elgato.com/downloads' }
}

function Test-Resolver_Peripherals {
    param($Component)
    return $false   # peripherals are NOT in the rich component list; see below
}

# This resolver works differently: it's called once by the orchestrator's
# "extra resolvers" hook (if the scan has a devices[] array) to produce
# advisory rows for detected USB peripherals. For now, the orchestrator
# doesn't call it per-component. A future hook will; until then, call it
# directly: Invoke-Resolver_Peripherals -Devices $scan.devices
function Get-PeripheralAdvisories {
    param($Devices)
    if (-not $Devices) { return @() }
    $seen = @{}
    $genericNames = @('USB Input Device','USB Composite Device','USB Device','HID-compliant device',
                       'USB Root Hub','Generic USB Hub','USB Mass Storage Device')
    $out = foreach ($d in $Devices) {
        if ($d.bus -ne 'USB') { continue }
        if ($d.name -in $genericNames) { continue }
        $vid = $null
        if ($d.hardware_id -match 'VID_([0-9A-Fa-f]{4})') { $vid = $Matches[1].ToUpper() }
        if (-not $vid -or -not $script:PeripheralVendors.ContainsKey($vid)) { continue }
        $key = "$vid`:$($d.name)"
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $info = $script:PeripheralVendors[$vid]
        [ordered]@{
            component_key = "usb:$vid`:$($d.name -replace '\s+','-')".ToLower()
            category      = 'peripheral'
            model         = $d.name
            status        = 'manual-check'
            best          = [ordered]@{
                source  = $info.brand.ToLower()
                trust   = 'oem'
                status  = 'manual-check'
                note    = "Firmware managed by $($info.tool). Check for updates:"
                download_url = $info.url
                tool    = $info.tool
            }
            candidates    = @()
        }
    }
    @($out)
}
