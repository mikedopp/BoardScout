[CmdletBinding()]
param(
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$exe = Join-Path $PSScriptRoot "build\portable\$runtime\BoardScout.exe"

if ($Rebuild -or -not (Test-Path -LiteralPath $exe)) {
    & (Join-Path $PSScriptRoot 'Build-Portable.ps1') -Runtime $runtime
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Portable build was not created: $exe"
}

Start-Process -FilePath $exe
