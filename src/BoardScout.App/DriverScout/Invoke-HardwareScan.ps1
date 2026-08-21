<#
.SYNOPSIS
    BoardScout - Portable hardware inventory scanner.

.DESCRIPTION
    Scans the local PC for hardware identity, currently-installed driver
    versions, and storage/BIOS firmware revisions. Emits a normalized JSON
    manifest designed for later import into a database for history tracking
    and to seed "latest version" lookups (Phase 2).

    Dependency-free: uses only built-in CIM/WMI cmdlets so it runs on any
    Windows box with no install and no admin rights required.

.PARAMETER OutDir
    Directory to write the scan JSON into. Defaults to .\scans next to the script.

.PARAMETER Quiet
    Suppress the console summary table (still writes JSON).

.EXAMPLE
    .\Invoke-HardwareScan.ps1
    .\Invoke-HardwareScan.ps1 -OutDir D:\inventory -Quiet
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$ToolVersion  = '0.1.0'
$SchemaVersion = '2.0'

# ---------- helpers -------------------------------------------------------

function Get-ScriptRoot {
    if ($PSScriptRoot) { return $PSScriptRoot }
    return (Get-Location).Path
}

# Run a collector block, never let one failure abort the whole scan.
function Invoke-Safe {
    param([string]$Name, [scriptblock]$Block)
    try {
        return & $Block
    } catch {
        Write-Warning "Collector '$Name' failed: $($_.Exception.Message)"
        return $null
    }
}

# Convert a CIM/WMI datetime (or null) to an ISO date string.
function ConvertTo-IsoDate {
    param($Value)
    if (-not $Value) { return $null }
    try {
        if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-dd') }
        return ([Management.ManagementDateTimeConverter]::ToDateTime($Value)).ToString('yyyy-MM-dd')
    } catch { return [string]$Value }
}

# Win32_PhysicalMemory.Speed often reports the active JEDEC clock instead of
# the advertised XMP/EXPO speed. Common DIMM part numbers usually encode the
# advertised rate (for example F4-3200C16). Prefer that only when it is a
# plausible DDR data rate and is faster than the reported value.
function Get-RatedMemorySpeed {
    param($MemoryModule)
    $reported = [int]$MemoryModule.Speed
    $part = "$($MemoryModule.PartNumber)".Trim()
    $encoded = 0
    if ($part -match '(?:DDR[345][-_]?)?(\d{4,5})(?:C\d+|[-_])') {
        $encoded = [int]$Matches[1]
    }
    if ($encoded -ge 1600 -and $encoded -le 10000 -and $encoded -gt $reported) {
        return $encoded
    }
    return $reported
}

# Pull VEN/DEV/SUBSYS/REV out of a PNPDeviceID string.
function Get-HardwareId {
    param([string]$PnpId)
    $h = [ordered]@{ bus = $null; ven = $null; dev = $null; subsys = $null; rev = $null; raw = $PnpId }
    if (-not $PnpId) { return $h }
    if ($PnpId -match '^(?<bus>[^\\]+)\\') { $h.bus = $Matches.bus }
    if ($PnpId -match 'VEN_([0-9A-Fa-f]+)')    { $h.ven    = $Matches[1].ToUpper() }
    if ($PnpId -match 'DEV_([0-9A-Fa-f]+)')    { $h.dev    = $Matches[1].ToUpper() }
    if ($PnpId -match 'SUBSYS_([0-9A-Fa-f]+)') { $h.subsys = $Matches[1].ToUpper() }
    if ($PnpId -match 'REV_([0-9A-Fa-f]+)')    { $h.rev    = $Matches[1].ToUpper() }
    return $h
}

# Stable, lowercase component key for DB history matching.
function Get-ComponentKey {
    param([string]$Category, [string]$PnpId, [string]$Fallback)
    if ($PnpId) {
        # Trim the trailing instance segment so the key stays stable across reslots.
        $core = ($PnpId -split '\\')[0..1] -join '\'
        return ($core.ToLower())
    }
    return ("{0}:{1}" -f $Category, $Fallback).ToLower()
}

function Get-Sha1Hex {
    param([string]$Text)
    if (-not $Text) { return $null }
    $sha = [Security.Cryptography.SHA1]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
        return -join ($bytes | ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
}

# ---------- collectors ----------------------------------------------------

Write-Verbose 'Collecting system / firmware identity...'

$os = Invoke-Safe 'os' {
    $o = Get-CimInstance Win32_OperatingSystem
    # SerialNumber here IS the Windows Product ID (e.g. 00330-xxxxx-xxxxx-xxxxx).
    [ordered]@{ caption = $o.Caption; version = $o.Version; build = $o.BuildNumber; arch = $o.OSArchitecture
                product_id = $o.SerialNumber; registered_to = $o.RegisteredUser; install_date = (ConvertTo-IsoDate $o.InstallDate) }
}

$bios = Invoke-Safe 'bios' {
    $b = Get-CimInstance Win32_BIOS
    [ordered]@{
        vendor         = $b.Manufacturer
        version        = $b.SMBIOSBIOSVersion
        smbios_version = "$($b.SMBIOSMajorVersion).$($b.SMBIOSMinorVersion)"
        release_date   = ConvertTo-IsoDate $b.ReleaseDate
    }
}

$baseboard = Invoke-Safe 'baseboard' {
    $bb = Get-CimInstance Win32_BaseBoard
    [ordered]@{ manufacturer = $bb.Manufacturer; product = $bb.Product; version = $bb.Version; serial = $bb.SerialNumber }
}

$computer = Invoke-Safe 'computer' {
    $c = Get-CimInstance Win32_ComputerSystem
    [ordered]@{ manufacturer = $c.Manufacturer; model = $c.Model; family = $c.SystemFamily }
}

$cpu = Invoke-Safe 'cpu' {
    Get-CimInstance Win32_Processor | ForEach-Object {
        [ordered]@{ name = ($_.Name).Trim(); id = $_.ProcessorId; cores = $_.NumberOfCores; threads = $_.NumberOfLogicalProcessors }
    }
}

# OEM identity: Dell SystemID/ServiceTag, HP ProductID, Lenovo MachineType, Surface model.
$oem = Invoke-Safe 'oem' {
    $csp = Get-CimInstance Win32_ComputerSystemProduct
    $cs  = Get-CimInstance Win32_ComputerSystem
    $mfr = "$($cs.Manufacturer)".Trim()
    $info = [ordered]@{
        manufacturer   = $mfr
        model          = "$($cs.Model)".Trim()
        sku            = "$($cs.SystemSKUNumber)".Trim()
        uuid           = "$($csp.UUID)".Trim()
        serial         = "$($csp.IdentifyingNumber)".Trim()   # Dell=ServiceTag, HP=serial, Lenovo=serial
        family         = "$($cs.SystemFamily)".Trim()
        oem_brand      = $null                                 # normalized below
        oem_system_id  = $null                                 # brand-specific lookup key
    }
    # Normalize known OEM brands and extract the lookup key each catalog needs.
    switch -Regex ($mfr) {
        'Dell'      { $info.oem_brand = 'dell';    $info.oem_system_id = $info.sku; break }
        'HP|Hewlett' { $info.oem_brand = 'hp';     $info.oem_system_id = $baseboard.product; break }
        'Lenovo'    { $info.oem_brand = 'lenovo';  $info.oem_system_id = ($info.model -replace '-.*','').Trim(); break }
        'Microsoft' { $info.oem_brand = 'surface'; $info.oem_system_id = $info.model; break }
    }
    $info
}

# ---------- components (rich, lookup-ready) -------------------------------

$components = [System.Collections.Generic.List[object]]::new()

# Motherboard BIOS as a first-class, updatable component.
if ($baseboard -and $bios) {
    $components.Add([ordered]@{
        component_key = Get-ComponentKey 'bios' $null ("{0}-{1}" -f $baseboard.manufacturer, $baseboard.product)
        category      = 'bios'
        vendor        = $baseboard.manufacturer
        model         = $baseboard.product
        hardware_id   = $null
        current       = [ordered]@{ driver_version = $null; driver_date = $null; firmware = $bios.version; firmware_date = $bios.release_date }
        source        = 'Win32_BIOS+Win32_BaseBoard'
        lookup_hints  = [ordered]@{ board_vendor = $baseboard.manufacturer; board_model = $baseboard.product }
    })
}

# GPUs
Invoke-Safe 'gpu' {
    Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID } | ForEach-Object {
        $hid = Get-HardwareId $_.PNPDeviceID
        $components.Add([ordered]@{
            component_key = Get-ComponentKey 'gpu' $_.PNPDeviceID $_.Name
            category      = 'gpu'
            vendor        = $_.AdapterCompatibility
            model         = $_.Name
            hardware_id   = $hid
            current       = [ordered]@{ driver_version = $_.DriverVersion; driver_date = (ConvertTo-IsoDate $_.DriverDate); firmware = $null }
            source        = 'Win32_VideoController'
            lookup_hints  = [ordered]@{ pci_vendor = $hid.ven; pci_device = $hid.dev; pci_subsys = $hid.subsys }
        })
    }
} | Out-Null

# Network adapters (physical PCI/USB only)
Invoke-Safe 'net' {
    Get-CimInstance Win32_NetworkAdapter |
        Where-Object { $_.PNPDeviceID -and ($_.PNPDeviceID -match '^(PCI|USB)') } | ForEach-Object {
        $hid = Get-HardwareId $_.PNPDeviceID
        $components.Add([ordered]@{
            component_key = Get-ComponentKey 'network' $_.PNPDeviceID $_.Name
            category      = 'network'
            vendor        = $_.Manufacturer
            model         = $_.Name
            hardware_id   = $hid
            current       = [ordered]@{ driver_version = $null; driver_date = $null; firmware = $null }
            source        = 'Win32_NetworkAdapter'
            lookup_hints  = [ordered]@{ pci_vendor = $hid.ven; pci_device = $hid.dev; pci_subsys = $hid.subsys }
        })
    }
} | Out-Null

# Storage: merge Get-PhysicalDisk (media/bus/firmware/serial) with Win32_DiskDrive (pnp id).
Invoke-Safe 'storage' {
    $diskDrives = @(Get-CimInstance Win32_DiskDrive)
    Get-PhysicalDisk | ForEach-Object {
        $pd = $_
        # Get-PhysicalDisk carries its own SerialNumber/UniqueId -- always present and
        # unique per disk, so identical models (e.g. two USB "Game Drive"s) never collide.
        $serial = if ($pd.SerialNumber) { ($pd.SerialNumber).Trim() } else { $null }
        # Match Win32_DiskDrive by serial first (robust for USB), then fall back to model.
        $match = $diskDrives | Where-Object { $_.SerialNumber -and $serial -and ($_.SerialNumber).Trim() -eq $serial } | Select-Object -First 1
        if (-not $match) {
            $match = $diskDrives | Where-Object { $_.Model -and ($_.Model -replace '\s+USB Device$','').Trim() -eq ($pd.Model).Trim() } | Select-Object -First 1
        }
        $pnp = if ($match) { $match.PNPDeviceID } else { $null }
        if (-not $serial -and $match) { $serial = ($match.SerialNumber).Trim() }
        # Unique fallback id: serial, else the disk's stable UniqueId.
        $uid = if ($serial) { $serial } else { "$($pd.UniqueId)" }
        $components.Add([ordered]@{
            component_key = Get-ComponentKey 'storage' $pnp ("{0}-{1}" -f $pd.Model, $uid)
            category      = 'storage'
            vendor        = $pd.Manufacturer
            model         = ($pd.Model).Trim()
            hardware_id   = (Get-HardwareId $pnp)
            current       = [ordered]@{ driver_version = $null; driver_date = $null; firmware = $pd.FirmwareVersion }
            source        = 'Get-PhysicalDisk+Win32_DiskDrive'
            lookup_hints  = [ordered]@{ media_type = "$($pd.MediaType)"; bus_type = "$($pd.BusType)"; size_bytes = $pd.Size; serial = $serial }
        })
    }
} | Out-Null

# AMD chipset driver (if AMD board)
Invoke-Safe 'chipset' {
    if ($baseboard.manufacturer -notmatch 'ASRock|ASUS|Gigabyte|MSI' -and $computer.manufacturer -notmatch 'AMD') { return }
    $chipsetDrivers = @(Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceName -match 'AMD (PSP|SMBus|Chipset|GPIO|I2C|PCI|IOMMU)' -and $_.DriverVersion } |
        Sort-Object DeviceName)
    $chipsetDrv = $chipsetDrivers | Where-Object DeviceName -match 'AMD PSP' | Select-Object -First 1
    if (-not $chipsetDrv) { $chipsetDrv = $chipsetDrivers | Select-Object -First 1 }

    # AMD publishes one chipset software package containing several device drivers
    # with unrelated version schemes (PSP, SMBus, GPIO, PCI, and others). Compare
    # package-to-package versions instead of treating a PSP version as the package.
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $chipsetPackage = Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq 'AMD Chipset Software' -and $_.DisplayVersion } |
        Sort-Object {
            try { [version]$_.DisplayVersion } catch { [version]'0.0' }
        } -Descending |
        Select-Object -First 1
    if (-not $chipsetDrv -and -not $chipsetPackage) { return }

    $chipset = $null
    if ($baseboard.product -match '(B550|B450|X570|X470|A520|B650|B650E|X670|X670E|B850|B840|X870|X870E)') { $chipset = $Matches[1] }
    $packageVersion = if ($chipsetPackage) { "$($chipsetPackage.DisplayVersion)" } else { $null }
    $fallbackVersion = if ($chipsetDrv) { "$($chipsetDrv.DriverVersion)" } else { $null }
    $components.Add([ordered]@{
        component_key = Get-ComponentKey 'chipset' "AMD:$chipset" $baseboard.product
        category      = 'chipset'
        vendor        = 'AMD'
        model         = if ($chipset) { "AMD $chipset Chipset" } elseif ($chipsetDrv) { $chipsetDrv.DeviceName } else { 'AMD Chipset' }
        hardware_id   = if ($chipsetDrv) { Get-HardwareId $chipsetDrv.DeviceID } else { $null }
        current       = [ordered]@{
            driver_version = if ($packageVersion) { $packageVersion } else { $fallbackVersion }
            driver_date    = if ($packageVersion) { $null } elseif ($chipsetDrv) { ConvertTo-IsoDate $chipsetDrv.DriverDate } else { $null }
            firmware       = $null
        }
        source        = if ($packageVersion) { 'Registry:AMD Chipset Software' } else { 'Win32_PnPSignedDriver fallback' }
        lookup_hints  = [ordered]@{
            chipset            = $chipset
            board_model        = $baseboard.product
            package_version    = $packageVersion
            psp_driver_version = if ($chipsetDrv) { "$($chipsetDrv.DriverVersion)" } else { $null }
        }
    })
} | Out-Null

# Audio devices (AMD HD Audio, Realtek, etc.)
Invoke-Safe 'audio' {
    Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceClass -eq 'MEDIA' -and $_.DeviceName -match 'Audio' -and $_.DriverVersion -and $_.DeviceID -match '^(PCI|HDAUDIO)' } |
        ForEach-Object {
            $hid = Get-HardwareId $_.DeviceID
            $components.Add([ordered]@{
                component_key = Get-ComponentKey 'audio' $_.DeviceID $_.DeviceName
                category      = 'audio'
                vendor        = $_.Manufacturer
                model         = $_.DeviceName
                hardware_id   = $hid
                current       = [ordered]@{ driver_version = $_.DriverVersion; driver_date = (ConvertTo-IsoDate $_.DriverDate); firmware = $null }
                source        = 'Win32_PnPSignedDriver'
                lookup_hints  = [ordered]@{ pci_vendor = $hid.ven; pci_device = $hid.dev }
            })
        }
} | Out-Null

# ---------- full installed-driver inventory (comprehensive, lighter) ------

$drivers = Invoke-Safe 'drivers' {
    Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceName -and $_.DriverVersion } |
        Sort-Object DeviceClass, DeviceName | ForEach-Object {
            [ordered]@{
                device_name  = $_.DeviceName
                device_class = $_.DeviceClass
                manufacturer = $_.Manufacturer
                version      = $_.DriverVersion
                date         = (ConvertTo-IsoDate $_.DriverDate)
                inf          = $_.InfName
                device_id    = $_.DeviceID
            }
        }
}

# ---------- full device enumeration (EVERY PnP device) --------------------
# Broader than $drivers: includes devices with no signed driver. Captures the
# raw hardware IDs so the rundown tool can resolve VEN/DEV + VID/PID to names
# against the offline pci.ids / usb.ids database. Scanner stays offline here --
# it records identifiers only; name resolution happens in Get-DriverRundown.

$devices = Invoke-Safe 'devices' {
    Get-CimInstance Win32_PnPEntity |
        Where-Object { $_.PNPDeviceID } |
        Sort-Object PNPClass, Name | ForEach-Object {
            $hid = Get-HardwareId $_.PNPDeviceID
            [ordered]@{
                name         = $_.Name
                class        = $_.PNPClass
                manufacturer = $_.Manufacturer
                status       = $_.Status
                present      = $_.Present
                bus          = $hid.bus
                ven          = $hid.ven      # PCI VEN_ only; USB VID/PID parsed from hardware_id at resolve time
                dev          = $hid.dev
                hardware_id  = $_.PNPDeviceID
            }
        }
}

# ---------- enrich: back-fill component driver versions -------------------
# Every PnP device's installed-driver version lives in $drivers keyed by
# device_id (== component hardware_id.raw). Index it and fill the gaps so each
# component object is self-contained. Exact match only -- no guessing.

Invoke-Safe 'enrich' {
    if (-not $drivers) { return }
    $byId = @{}
    foreach ($d in $drivers) {
        if ($d.device_id) { $byId[$d.device_id.ToUpper()] = $d }
    }
    foreach ($c in $components) {
        if ($c.current.driver_version) { continue }           # already known (e.g. GPU)
        $raw = if ($c.hardware_id) { $c.hardware_id.raw } else { $null }
        if (-not $raw) { continue }
        $hit = $byId[$raw.ToUpper()]
        if (-not $hit) { continue }
        $c.current.driver_version = $hit.version
        $c.current.driver_date    = $hit.date
        # Note which INF supplied it -- for storage this is often the generic
        # OS disk driver (disk.inf), not a vendor driver. Transparency matters.
        $c.current.driver_source  = $hit.inf
    }
} | Out-Null

# ---------- board-layout collectors (v2) ---------------------------------

$memory = Invoke-Safe 'memory' {
    $slots = @(Get-CimInstance Win32_PhysicalMemory)
    $total_slots = 0
    try { $total_slots = (Get-CimInstance Win32_PhysicalMemoryArray | Select-Object -First 1).MemoryDevices } catch {}
    if (-not $total_slots -or $total_slots -lt $slots.Count) { $total_slots = $slots.Count }
    [ordered]@{
        total_slots   = $total_slots
        populated     = $slots.Count
        # Keep this an integer in JSON. PowerShell can promote Measure-Object's
        # sum to a floating-point value (for example 17179869184.0), which is
        # needlessly incompatible with strongly typed consumers.
        total_bytes   = [long](($slots | Measure-Object -Property Capacity -Sum).Sum)
        slots         = @($slots | ForEach-Object {
            [ordered]@{
                bank          = "$($_.BankLabel)"
                locator       = "$($_.DeviceLocator)"
                capacity_gb   = [math]::Round($_.Capacity / 1GB, 1)
                speed_mhz     = $_.ConfiguredClockSpeed
                rated_mhz     = Get-RatedMemorySpeed $_
                manufacturer  = "$($_.Manufacturer)".Trim()
                part_number   = "$($_.PartNumber)".Trim()
                serial        = "$($_.SerialNumber)".Trim()
                form_factor   = $_.FormFactor
                type_detail   = $_.TypeDetail
                data_width    = $_.DataWidth
            }
        })
    }
}

$pcie_topology = Invoke-Safe 'pcie' {
    $pciDevs = Get-CimInstance Win32_PnPSignedDriver | Where-Object {
        $_.DeviceID -match '^PCI\\' -and $_.Location
    }
    @($pciDevs | ForEach-Object {
        $hid = Get-HardwareId $_.DeviceID
        [ordered]@{
            name        = $_.DeviceName
            location    = "$($_.Location)"
            class       = $_.DeviceClass
            driver_ver  = $_.DriverVersion
            driver_date = (ConvertTo-IsoDate $_.DriverDate)
            ven         = $hid.ven
            dev         = $hid.dev
            hardware_id = $_.DeviceID
        }
    })
}

$expansion_slots = Invoke-Safe 'expansion_slots' {
    @(Get-CimInstance Win32_SystemSlot | ForEach-Object {
        [ordered]@{
            designation    = "$($_.SlotDesignation)".Trim()
            description    = "$($_.Description)".Trim()
            status         = "$($_.Status)".Trim()
            current_usage  = $_.CurrentUsage
            max_data_width = $_.MaxDataWidth
            purpose        = "$($_.Purpose)".Trim()
        }
    })
}

$volumes = Invoke-Safe 'volumes' {
    @(Get-CimInstance Win32_LogicalDisk | Where-Object { $_.Size -gt 0 } | ForEach-Object {
        $disk = $null
        $letter = "$($_.DeviceID)".TrimEnd(':')
        try {
            $partition = Get-Partition -DriveLetter $letter -ErrorAction Stop | Select-Object -First 1
            if ($partition) { $disk = $partition | Get-Disk -ErrorAction Stop }
        } catch {}
        [ordered]@{
            letter        = $_.DeviceID
            label         = $_.VolumeName
            file_system   = $_.FileSystem
            size_bytes    = $_.Size
            free_bytes    = $_.FreeSpace
            drive_type    = switch ([int]$_.DriveType) { 2 {'removable'} 3 {'local'} 4 {'network'} 5 {'optical'} default {"type_$($_.DriveType)"} }
            disk_number   = if ($disk) { $disk.Number } else { $null }
            disk_model    = if ($disk) { "$($disk.FriendlyName)".Trim() } else { $null }
            disk_serial   = if ($disk) { "$($disk.SerialNumber)".Trim() } else { $null }
            bus_type      = if ($disk) { "$($disk.BusType)" } else { $null }
        }
    })
}

$dead_devices = Invoke-Safe 'dead_devices' {
    @(Get-CimInstance Win32_PnPEntity | Where-Object {
        $_.ConfigManagerErrorCode -ne 0 -or $_.Status -ne 'OK'
    } | ForEach-Object {
        $errCodes = @{
            1='not configured';3='corrupt driver';10='cannot start';22='disabled';
            28='no driver installed';29='firmware error';31='not working';43='stopped'
        }
        [ordered]@{
            name          = $_.Name
            class         = $_.PNPClass
            status        = $_.Status
            error_code    = $_.ConfigManagerErrorCode
            error_desc    = if ($errCodes[[int]$_.ConfigManagerErrorCode]) { $errCodes[[int]$_.ConfigManagerErrorCode] } else { "error $($_.ConfigManagerErrorCode)" }
            hardware_id   = $_.PNPDeviceID
            present       = $_.Present
        }
    })
}

$displays = Invoke-Safe 'displays' {
    @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -eq 'Monitor' -and $_.Status -eq 'OK' } | ForEach-Object {
        $edid = $null
        try {
            $wmiMon = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue |
                Where-Object { $_.InstanceName -match ($_.PNPDeviceID -replace '\\','\\') } | Select-Object -First 1
            if ($wmiMon) {
                $edid = [ordered]@{
                    manufacturer_code = -join ($wmiMon.ManufacturerName | Where-Object {$_ -ne 0} | ForEach-Object {[char]$_})
                    product_code      = $wmiMon.ProductCodeID
                    serial            = -join ($wmiMon.SerialNumberID | Where-Object {$_ -ne 0} | ForEach-Object {[char]$_})
                    name              = -join ($wmiMon.UserFriendlyName | Where-Object {$_ -ne 0} | ForEach-Object {[char]$_})
                    year              = $wmiMon.YearOfManufacture
                }
            }
        } catch {}
        [ordered]@{
            name        = $_.Name
            hardware_id = $_.PNPDeviceID
            edid        = $edid
        }
    })
}

$usb_devices = Invoke-Safe 'usb_devices' {
    @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -match '^USB\\' } | ForEach-Object {
        $vid = $null; $pid_val = $null
        if ($_.PNPDeviceID -match 'VID_([0-9A-Fa-f]+)') { $vid = $Matches[1].ToUpper() }
        if ($_.PNPDeviceID -match 'PID_([0-9A-Fa-f]+)') { $pid_val = $Matches[1].ToUpper() }
        [ordered]@{
            name        = $_.Name
            class       = $_.PNPClass
            vid         = $vid
            pid         = $pid_val
            status      = $_.Status
            hardware_id = $_.PNPDeviceID
        }
    })
}

$form_factor = Invoke-Safe 'form_factor' {
    $product = "$($baseboard.product)"
    $ff = 'unknown'
    if ($product -match 'ITX|Mini-ITX') { $ff = 'mini-itx' }
    elseif ($product -match 'mATX|Micro|M-ATX|[A-Z]\d+M\b') { $ff = 'micro-atx' }
    elseif ($product -match 'E-ATX|EATX') { $ff = 'eatx' }
    elseif ($product -match 'ATX') { $ff = 'atx' }
    $ff
}

# ---------- assemble manifest --------------------------------------------

$machineSeed = @($baseboard.serial, $baseboard.product, $computer.manufacturer) -join '|'
$machineId   = Get-Sha1Hex $machineSeed

$manifest = [ordered]@{
    schema_version = $SchemaVersion
    scan = [ordered]@{
        tool          = 'BoardScout'
        tool_version  = $ToolVersion
        timestamp_utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        machine_id    = $machineId
        hostname      = $env:COMPUTERNAME
        os            = $os
    }
    system = [ordered]@{
        manufacturer = $computer.manufacturer
        model        = $computer.model
        family       = $computer.family
        baseboard    = $baseboard
        bios         = $bios
        cpu          = $cpu
        oem          = $oem
    }
    components     = @($components)
    drivers        = @($drivers)
    devices        = @($devices)
    memory         = $memory
    pcie_topology  = @($pcie_topology)
    expansion_slots = @($expansion_slots)
    volumes        = @($volumes)
    dead_devices   = @($dead_devices)
    displays       = @($displays)
    usb_devices    = @($usb_devices)
    form_factor    = $form_factor
}

# ---------- write ---------------------------------------------------------

if (-not $OutDir) { $OutDir = Join-Path (Get-ScriptRoot) 'scans' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$outFile = Join-Path $OutDir ("scan_{0}_{1}.json" -f $env:COMPUTERNAME, $stamp)

$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $outFile -Encoding UTF8

# ---------- console summary ----------------------------------------------

if (-not $Quiet) {
    Write-Host ""
    Write-Host "BoardScout v$ToolVersion" -ForegroundColor Cyan -NoNewline
    Write-Host "  -  $($env:COMPUTERNAME)  ($($os.caption))"
    Write-Host ("Machine ID: {0}" -f $machineId.Substring(0, 12)) -ForegroundColor DarkGray
    Write-Host ""
    Write-Host ("  {0,-9} {1,-32} {2,-18} {3}" -f 'CATEGORY','MODEL','CUR DRIVER','FIRMWARE') -ForegroundColor DarkGray
    foreach ($c in $components) {
        $drv = if ($c.current.driver_version) { $c.current.driver_version } else { '-' }
        $fw  = if ($c.current.firmware)       { $c.current.firmware }       else { '-' }
        $model = if ($c.model.Length -gt 31) { $c.model.Substring(0,31) } else { $c.model }
        Write-Host ("  {0,-9} {1,-32} {2,-18} {3}" -f $c.category, $model, $drv, $fw)
    }
    Write-Host ""
    Write-Host ("  {0} components, {1} drivers, {2} devices catalogued." -f $components.Count, ($drivers | Measure-Object).Count, ($devices | Measure-Object).Count) -ForegroundColor Green
    Write-Host ("  Saved: {0}" -f $outFile) -ForegroundColor Green
    Write-Host ""
}

return $outFile
