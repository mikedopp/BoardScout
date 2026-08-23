[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\BoardScout.App\BoardScout.App.csproj'
$output = Join-Path $PSScriptRoot "build\portable\$Runtime"
$archive = Join-Path $PSScriptRoot "build\BoardScout-0.8.0-$Runtime.zip"

$portableRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build\portable'))
$resolvedOutput = [IO.Path]::GetFullPath($output)
if (-not $resolvedOutput.StartsWith($portableRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected publish output: $resolvedOutput"
}
if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$publishArgs = @(
    'publish', $project,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '--output', $output
)
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal

$exe = Join-Path $output 'BoardScout.exe'
Write-Host "Portable app: $exe" -ForegroundColor Green
Write-Host "Distribution zip: $archive" -ForegroundColor Green
return $exe
