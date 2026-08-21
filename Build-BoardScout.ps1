<#
.SYNOPSIS
    Builds the standalone BoardScout dashboard.
.PARAMETER ScanFile
    Optional BoardScout scan to embed in the generated HTML.
.PARAMETER OutputDir
    Build output directory. Defaults to .\build.
#>
[CmdletBinding()]
param(
    [string]$ScanFile,
    [string]$OutputDir = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'src\web\index.html'
if (-not (Test-Path -LiteralPath $templatePath)) {
    throw "Dashboard template not found: $templatePath"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$utf8 = [Text.Encoding]::UTF8
$html = [IO.File]::ReadAllText($templatePath, $utf8)
$marker = '<!-- BOARD_SCOUT_SCAN_DATA -->'
if (-not $html.Contains($marker)) {
    throw 'Dashboard template is missing the scan-data marker.'
}

$injection = ''
if ($ScanFile) {
    $resolvedScan = (Resolve-Path -LiteralPath $ScanFile).Path
    $rawJson = [IO.File]::ReadAllText($resolvedScan, $utf8).TrimStart([char]0xFEFF)
    $scan = $rawJson | ConvertFrom-Json
    if (-not $scan.system -or -not $scan.components) {
        throw "Not a compatible BoardScout scan: $resolvedScan"
    }

    # Prevent a device name containing '<' from terminating the script element.
    $safeJson = $rawJson.Replace('<', '\u003c')
    $injection = "<script>window.SCAN_DATA=$safeJson;</script>"
}

$outputPath = Join-Path $OutputDir 'index.html'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($outputPath, $html.Replace($marker, $injection), $utf8NoBom)
Write-Output (Resolve-Path -LiteralPath $outputPath).Path
