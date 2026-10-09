[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\BoardScout.App\BoardScout.App.csproj'
# The version comes from the project, so artifact names can never drift from the app again.
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }

$buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build'))
$portable = Join-Path $buildRoot "portable\$Runtime"
$standalone = Join-Path $buildRoot "standalone\$Runtime"
$zip = Join-Path $buildRoot "BoardScout-$version-$Runtime.zip"
$exe = Join-Path $buildRoot "BoardScout-$version-$Runtime.exe"
$sums = Join-Path $buildRoot "BoardScout-$version-SHA256SUMS.txt"

foreach ($dir in $portable, $standalone) {
    if (-not ([IO.Path]::GetFullPath($dir)).StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unexpected publish output: $dir"
    }
    # Clear the old build but keep Data: running BoardScout from here keeps its settings and scans there.
    if (Test-Path -LiteralPath $dir) {
        Get-ChildItem -LiteralPath $dir -Force | Where-Object Name -ne 'Data' | Remove-Item -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

function Publish-BoardScout([string]$Output, [string[]]$Extra) {
    $publishArgs = @(
        'publish', $project,
        '--configuration', $Configuration,
        '--runtime', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=false',
        '--output', $Output
    ) + $Extra
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

# Portable folder: BoardScout.exe with Assets, DriverScout, LICENSE, and notices beside it.
Publish-BoardScout $portable @()
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
# Never ship a Data folder: scans hold hostnames and hardware identifiers.
Compress-Archive -Path (Get-ChildItem -LiteralPath $portable -Force | Where-Object Name -ne 'Data').FullName -DestinationPath $zip -CompressionLevel Optimal

# Standalone: one exe with everything packed inside it.
Publish-BoardScout $standalone @('-p:Standalone=true')
Copy-Item -LiteralPath (Join-Path $standalone 'BoardScout.exe') -Destination $exe -Force

Get-FileHash -Algorithm SHA256 -LiteralPath $zip, $exe |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } |
    Set-Content -LiteralPath $sums -Encoding ascii

$portableExe = Join-Path $portable 'BoardScout.exe'
Write-Host "Portable app:     $portableExe" -ForegroundColor Green
Write-Host "Distribution zip: $zip" -ForegroundColor Green
Write-Host "Standalone exe:   $exe" -ForegroundColor Green
Write-Host "Checksums:        $sums" -ForegroundColor Green
return $portableExe
