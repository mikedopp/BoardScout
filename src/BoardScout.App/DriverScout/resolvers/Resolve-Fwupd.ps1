<#
    DriverScout resolver: fwupd / LVFS firmware updates (trust=vendor, Linux only)

    fwupd is the standard Linux firmware update framework. 200+ vendors
    publish firmware to the Linux Vendor Firmware Service (LVFS). fwupdmgr
    returns structured JSON listing installed firmware and available updates.

    This resolver shells out to fwupdmgr, so it only fires on Linux systems
    with fwupd installed. On Windows or when fwupd is absent, it silently
    skips via Test-Resolver_Fwupd returning $false.

    Convention functions:
        Test-Resolver_Fwupd   <component>
        Invoke-Resolver_Fwupd <component> -CacheDir <dir> -OsContext <obj>
#>

$script:FwupdUpdatesCache = $null

function Get-FwupdUpdates {
    if ($null -ne $script:FwupdUpdatesCache) { return $script:FwupdUpdatesCache }
    try {
        $json = & bash -c 'fwupdmgr get-updates --json 2>/dev/null' | Out-String
        if ($json) {
            $script:FwupdUpdatesCache = ($json | ConvertFrom-Json)
            return $script:FwupdUpdatesCache
        }
    } catch { }
    $script:FwupdUpdatesCache = @{ Devices = @() }
    return $script:FwupdUpdatesCache
}

function Test-Resolver_Fwupd {
    param($Component)
    if (-not $IsLinux) { return $false }
    if (-not (Get-Command fwupdmgr -ErrorAction SilentlyContinue)) { return $false }
    return ($Component.category -in @('bios','storage','chipset','network'))
}

function Invoke-Resolver_Fwupd {
    param($Component, [string]$CacheDir, $OsContext)

    $updates = Get-FwupdUpdates
    if (-not $updates.Devices -or $updates.Devices.Count -eq 0) {
        return [ordered]@{ source = 'fwupd'; trust = 'vendor'; status = 'current'; note = 'fwupd reports no pending updates' }
    }

    $model  = "$($Component.model)"
    $vendor = "$($Component.vendor)"

    $match = $updates.Devices | Where-Object {
        ($_.Name -and $model -and $_.Name -match [regex]::Escape($model)) -or
        ($_.Vendor -and $vendor -and $_.Vendor -match [regex]::Escape($vendor) -and
         $_.Name -match $Component.category)
    } | Select-Object -First 1

    if (-not $match) {
        return [ordered]@{ source = 'fwupd'; trust = 'vendor'; status = 'current'; note = 'no fwupd update matches this component' }
    }

    $release = if ($match.Releases) { $match.Releases | Select-Object -First 1 } else { $null }
    if (-not $release) {
        return [ordered]@{ source = 'fwupd'; trust = 'vendor'; status = 'current'; note = 'fwupd device found but no release available' }
    }

    $installedVer = if ($Component.current.firmware) { $Component.current.firmware }
                    else { $Component.current.driver_version }
    $installedVer = if (-not $installedVer -and $match.Version) { $match.Version } else { $installedVer }

    return [ordered]@{
        source            = 'fwupd'
        trust             = 'vendor'
        status            = 'update-available'
        compared_by       = 'fwupd'
        installed_version = $installedVer
        latest_version    = "$($release.Version)"
        latest_date       = "$($release.Created)"
        latest_name       = "$($match.Name) — $($release.Summary)"
        download_url      = "$($release.Uri)"
        fwupd_device_id   = "$($match.DeviceId)"
        install_hint      = "sudo fwupdmgr update"
    }
}
