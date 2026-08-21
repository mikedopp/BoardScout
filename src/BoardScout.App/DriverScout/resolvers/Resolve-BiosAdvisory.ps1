<#
    DriverScout resolver: motherboard BIOS (trust=oem)

    Board-vendor BIOS pages are JS-rendered and WAF-walled (ASRock = Incapsula),
    so a plain HTTP fetch only gets a bot challenge. When a headless browser is
    available (lib\Invoke-HeadlessFetch.ps1 -> Edge/Chrome), this resolver renders
    the real ASRock BIOS page, parses the available versions, and compares the
    newest to the installed BIOS -> a genuine update-available / current verdict.

    If headless fetch is unavailable (no Edge/Chrome) or the vendor isn't handled,
    it degrades gracefully to an advisory 'manual-check' with a direct link.

    Convention functions:
        Test-Resolver_BiosAdvisory   <component>
        Invoke-Resolver_BiosAdvisory <component> -CacheDir <dir> -OsContext <obj>
#>

function Test-Resolver_BiosAdvisory {
    param($Component)
    return ($Component.category -eq 'bios')
}

# Normalize a BIOS version string ("P3.40", "3.40") to a comparable [version].
function ConvertTo-BiosVersion {
    param([string]$Raw)
    if (-not $Raw) { return $null }
    $m = [regex]::Match($Raw, '(\d+)\.(\d+)')
    if (-not $m.Success) { return $null }
    try { return [version]("{0}.{1}" -f $m.Groups[1].Value, $m.Groups[2].Value) } catch { return $null }
}

# Pull the newest BIOS version from a rendered ASRock BIOS page DOM.
function Get-AsrockLatestBios {
    param([string]$Dom)
    if (-not $Dom) { return $null }
    $versions = [regex]::Matches($Dom, '\b(\d\.\d{2})\b') | ForEach-Object {
        try { [version]$_.Groups[1].Value } catch { $null }
    } | Where-Object { $_ }
    if (-not $versions) { return $null }
    return ($versions | Sort-Object -Descending | Select-Object -First 1)
}

function Invoke-Resolver_BiosAdvisory {
    param($Component, [string]$CacheDir, $OsContext)

    $vendor = "$($Component.vendor)"
    $model  = "$($Component.model)"
    $installedRaw = $Component.current.firmware
    $installed    = ConvertTo-BiosVersion $installedRaw

    # --- real lookup path: ASRock via headless browser --------------------
    if ($vendor -match 'ASRock' -and (Get-Command Get-HeadlessDom -ErrorAction SilentlyContinue)) {
        $enc = ($model -replace ' ', '%20')
        foreach ($platform in @('AMD', 'Intel')) {       # platform unknown; try both
            $url = "https://www.asrock.com/mb/$platform/$enc/bios.html"
            $dom = Get-HeadlessDom -Url $url -WaitMs 15000
            if (-not $dom -or $dom -notmatch 'BIOS') { continue }
            $latest = Get-AsrockLatestBios $dom
            if (-not $latest) { continue }
            $status =
                if (-not $installed)        { 'unknown' }
                elseif ($latest -gt $installed) { 'update-available' }
                else                        { 'current' }
            return [ordered]@{
                source            = 'asrock'
                trust             = 'oem'
                status            = $status
                installed_version = $installedRaw
                latest_version    = "$latest"
                download_url      = $url
                method            = 'headless-render'
            }
        }
    }

    # --- advisory fallback (no headless browser, or non-ASRock vendor) -----
    $url = if ($vendor -match 'ASRock') {
        'https://www.asrock.com/support/index.asp?cat=BIOS'
    } else {
        'https://www.google.com/search?q=' + [uri]::EscapeDataString("$vendor $model BIOS download")
    }
    return [ordered]@{
        source            = 'oem-bios'
        trust             = 'oem'
        status            = 'manual-check'
        installed_version = $installedRaw
        note              = "BIOS auto-check needs Edge/Chrome (headless). Installed $installedRaw. Verify latest:"
        download_url      = $url
    }
}
