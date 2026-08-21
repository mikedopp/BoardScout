<#
.SYNOPSIS
    Scans this Windows PC, builds the BoardScout dashboard, and opens it.
.PARAMETER ScanFile
    Build from an existing scan instead of scanning this PC.
.PARAMETER NoLaunch
    Build the dashboard without opening it.
#>
[CmdletBinding()]
param(
    [string]$ScanFile,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$scanDir = Join-Path $PSScriptRoot 'data\scans'
$buildDir = Join-Path $PSScriptRoot 'build'

if (-not $ScanFile) {
    New-Item -ItemType Directory -Force -Path $scanDir | Out-Null
    $scanner = Join-Path $PSScriptRoot 'src\Invoke-BoardScan.ps1'
    $ScanFile = & $scanner -OutDir $scanDir -Quiet | Select-Object -Last 1
}

$builder = Join-Path $PSScriptRoot 'Build-BoardScout.ps1'
$dashboard = & $builder -ScanFile $ScanFile -OutputDir $buildDir | Select-Object -Last 1
Write-Host "BoardScout dashboard: $dashboard" -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process -FilePath $dashboard
}

return $dashboard
