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
$html = Get-Content -LiteralPath $templatePath -Raw
$marker = '<!-- BOARD_SCOUT_SCAN_DATA -->'
if (-not $html.Contains($marker)) {
    throw 'Dashboard template is missing the scan-data marker.'
}

$injection = ''
if ($ScanFile) {
    $resolvedScan = (Resolve-Path -LiteralPath $ScanFile).Path
    $rawJson = (Get-Content -LiteralPath $resolvedScan -Raw).TrimStart([char]0xFEFF)
    $scan = $rawJson | ConvertFrom-Json
    if (-not $scan.system -or -not $scan.components) {
        throw "Not a compatible BoardScout scan: $resolvedScan"
    }

    # Prevent a device name containing '<' from terminating the script element.
    $safeJson = $rawJson.Replace('<', '\u003c')
    $injection = "<script>window.SCAN_DATA=$safeJson;</script>"
}

$outputPath = Join-Path $OutputDir 'index.html'
$html.Replace($marker, $injection) | Set-Content -LiteralPath $outputPath -Encoding UTF8
Write-Output (Resolve-Path -LiteralPath $outputPath).Path
