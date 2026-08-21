<#
    DriverScout resolver: Microsoft Update Catalog (trust=baseline)

    The catalog is a *litmus / baseline* source, NOT the source of truth. It ships
    WHQL "good-enough" drivers that often lag the vendor release (and sometimes lag
    the driver you already have). It is queryable by PCI hardware ID, which makes it
    a useful broad-coverage sanity check for devices that have no workable
    vendor-direct path (e.g. Intel NICs, Realtek LAN, where vendor sites are
    bot-protected / API-less).

    Because vendors use incompatible version schemes, this resolver compares by
    RELEASE DATE (captured at scan time), not by version string. The version string
    is reported for reference only. trust=baseline ensures it never outranks a
    vendor candidate or your installed driver in the orchestrator.

    Convention functions:
        Test-Resolver_MsCatalog   <component>
        Invoke-Resolver_MsCatalog <component> -CacheDir <dir> -OsContext <obj>
#>

function Test-Resolver_MsCatalog {
    param($Component)
    # Apply where we have a precise PCI id to query and an installed date to compare.
    return ($Component.category -in @('network') -and
            $Component.lookup_hints.pci_vendor -and $Component.lookup_hints.pci_device)
}

function Get-MsCatalogRows {
    param([string]$Query, [string]$CacheDir)
    # 6h cache per query keeps repeat checks off the network.
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $key  = ($Query -replace '[^\w]', '_')
    $path = Join-Path $CacheDir "mscat_$key.json"
    if ((Test-Path $path) -and ((Get-Date) - (Get-Item $path).LastWriteTime).TotalHours -lt 6) {
        return (Get-Content $path -Raw | ConvertFrom-Json)
    }
    $u = 'https://www.catalog.update.microsoft.com/Search.aspx?q=' + [uri]::EscapeDataString($Query)
    $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 25
    $rows = [regex]::Matches($r.Content, '(?s)<tr[^>]*id="[^"]*_R\d+"[^>]*>(.*?)</tr>')
    $out = foreach ($m in $rows) {
        $cells = [regex]::Matches($m.Groups[1].Value, '(?s)<td[^>]*>(.*?)</td>')
        if ($cells.Count -lt 5) { continue }
        $clean = { param($h) ($h -replace '(?s)<[^>]+>', '' -replace '&amp;', '&').Trim() }
        $title = & $clean $cells[1].Groups[1].Value
        $ver   = if ($title -match '\(([\d.]+)\)') { $Matches[1] } else { $null }
        [ordered]@{ title = $title; version = $ver; updated = (& $clean $cells[4].Groups[1].Value) }
    }
    $out = @($out)
    $out | ConvertTo-Json -Depth 5 | Set-Content -Path $path -Encoding UTF8
    return $out
}

function Invoke-Resolver_MsCatalog {
    param($Component, [string]$CacheDir, $OsContext)

    $query = "PCI\VEN_$($Component.lookup_hints.pci_vendor)&DEV_$($Component.lookup_hints.pci_device)"
    $rows  = Get-MsCatalogRows -Query $query -CacheDir $CacheDir
    if (-not $rows -or @($rows).Count -eq 0) {
        return [ordered]@{ source = 'ms-catalog'; trust = 'baseline'; status = 'unknown'; note = "no catalog entries for $query" }
    }

    # Parse the 'updated' date on each row, keep the newest.
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $toDate = { param($s) try { [datetime]::Parse($s, $inv) } catch { $null } }
    $parsed = foreach ($r in $rows) {
        $d = & $toDate $r.updated
        if ($d) { [pscustomobject]@{ title = $r.title; version = $r.version; date = $d } }
    }
    $parsed = @($parsed | Sort-Object date -Descending)
    if ($parsed.Count -eq 0) {
        return [ordered]@{ source = 'ms-catalog'; trust = 'baseline'; status = 'unknown'; note = 'no parseable catalog dates' }
    }
    $latest = $parsed[0]

    # Compare by release date vs the installed driver date.
    $installedDate = $null
    if ($Component.current.driver_date) {
        $installedDate = & $toDate $Component.current.driver_date
    }
    $status =
        if (-not $installedDate)             { 'unknown' }
        elseif ($latest.date -gt $installedDate.AddDays(1)) { 'update-available' }  # 1-day grace
        else                                 { 'current' }

    $searchUrl = 'https://www.catalog.update.microsoft.com/Search.aspx?q=' + [uri]::EscapeDataString($query)
    return [ordered]@{
        source            = 'ms-catalog'
        trust             = 'baseline'
        status            = $status
        compared_by       = 'release-date'
        installed_version = $Component.current.driver_version
        installed_date    = $Component.current.driver_date
        latest_version    = $latest.version
        latest_date       = $latest.date.ToString('yyyy-MM-dd')
        catalog_title     = $latest.title
        query             = $query
        download_url      = $searchUrl   # catalog has no direct file link; this is the review/download page
    }
}
