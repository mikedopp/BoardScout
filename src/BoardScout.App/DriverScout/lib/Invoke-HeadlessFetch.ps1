<#
    DriverScout library: headless-browser fetch for JS-rendered / WAF-walled pages.

    Many vendor pages (ASRock behind Incapsula, Intel, Realtek) are JavaScript-
    rendered and/or block plain HTTP clients. A real headless browser renders the
    JS and, crucially, passes the anti-bot checks that block Invoke-WebRequest.

    Uses Microsoft Edge (present on every Win10/11 box) or Chrome if found, via the
    classic `--headless --dump-dom` mode. A dedicated user-data-dir is required so
    it doesn't collide with the user's running browser profile.

    Get-HeadlessBrowser           -> path to msedge.exe / chrome.exe, or $null
    Get-HeadlessDom -Url [-WaitMs] -> rendered DOM HTML string, or $null
#>

function Get-HeadlessBrowser {
    $candidates = @(
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    )
    return ($candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1)
}

function Get-HeadlessDom {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$WaitMs = 12000
    )
    $browser = Get-HeadlessBrowser
    if (-not $browser) { return $null }
    $profileDir = Join-Path $env:TEMP 'ds_headless_profile'
    $ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36'
    try {
        $dom = & $browser --headless --disable-gpu --no-sandbox --log-level=3 `
            --user-data-dir="$profileDir" --virtual-time-budget=$WaitMs `
            "--user-agent=$ua" --dump-dom $Url 2>$null | Out-String
        if ($dom -and $dom.Length -gt 0) { return $dom }
        return $null
    } catch { return $null }
}
