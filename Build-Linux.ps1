[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string[]]$Runtime = @('linux-x64', 'linux-arm64'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

# Builds the Linux edition from Windows (or Linux): a portable folder packed as .tar.gz (executable bit set),
# a standalone single binary, and checksums, for each runtime.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\BoardScout.Linux\BoardScout.Linux.csproj'
$appProject = Join-Path $PSScriptRoot 'src\BoardScout.App\BoardScout.App.csproj'
# One version for both editions, kept in the Windows app's project.
$version = ([xml](Get-Content -LiteralPath $appProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $appProject" }

if (-not $SkipTests) {
    & dotnet test (Join-Path $PSScriptRoot 'tests\BoardScout.Linux.Tests') --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Linux tests failed with exit code $LASTEXITCODE" }
}

$buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build'))
$sums = Join-Path $buildRoot "BoardScout-$version-linux-SHA256SUMS.txt"
$artifacts = @()

function Publish-Linux([string]$Rid, [string]$Output, [string[]]$Extra) {
    $publishArgs = @(
        'publish', $project,
        '--configuration', $Configuration,
        '--runtime', $Rid,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=false',
        '--output', $Output
    ) + $Extra
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($Rid) failed with exit code $LASTEXITCODE" }
}

# A .tar.gz written with .NET's tar writer, so the program keeps its executable bit when built on Windows.
function Write-TarGz([string]$Source, [string]$Prefix, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $file = [IO.File]::Create($Destination)
    try {
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
        $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
        try {
            $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/')
            $directory = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$Prefix/")
            $directory.Mode = [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
            $writer.WriteEntry($directory)
            foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force | Sort-Object FullName) {
                $relative = $item.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                if ($relative -eq 'Data' -or $relative.StartsWith('Data/')) { continue }
                $name = "$Prefix/$relative"
                if ($item.PSIsContainer) {
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$name/")
                    $entry.Mode = [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
                    $writer.WriteEntry($entry)
                    continue
                }
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $name)
                $entry.Mode = if ($relative -eq 'boardscout') {
                    [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
                } else {
                    [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
                }
                $entry.ModificationTime = $item.LastWriteTimeUtc
                $stream = [IO.File]::OpenRead($item.FullName)
                try {
                    $entry.DataStream = $stream
                    $writer.WriteEntry($entry)
                } finally {
                    $stream.Dispose()
                }
            }
        } finally {
            $writer.Dispose()
            $gzip.Dispose()
        }
    } finally {
        $file.Dispose()
    }
}

foreach ($rid in $Runtime) {
    $portable = Join-Path $buildRoot "portable\$rid"
    $standalone = Join-Path $buildRoot "standalone\$rid"
    foreach ($dir in $portable, $standalone) {
        if (-not ([IO.Path]::GetFullPath($dir)).StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing unexpected publish output: $dir"
        }
        if (Test-Path -LiteralPath $dir) {
            Get-ChildItem -LiteralPath $dir -Force | Where-Object Name -ne 'Data' | Remove-Item -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    # Portable folder: boardscout with Assets, Shell, DriverScout data, LICENSE, and notices beside it.
    Publish-Linux $rid $portable @()
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\linux.md') -Destination (Join-Path $portable 'README.md') -Force
    $tar = Join-Path $buildRoot "BoardScout-$version-$rid.tar.gz"
    Write-TarGz $portable "BoardScout-$version-$rid" $tar
    $artifacts += $tar

    # Standalone: one binary with everything packed inside it.
    Publish-Linux $rid $standalone @('-p:Standalone=true')
    $binary = Join-Path $buildRoot "BoardScout-$version-$rid"
    Copy-Item -LiteralPath (Join-Path $standalone 'boardscout') -Destination $binary -Force
    $artifacts += $binary
}

Get-FileHash -Algorithm SHA256 -LiteralPath $artifacts |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } |
    Set-Content -LiteralPath $sums -Encoding ascii

foreach ($artifact in $artifacts) { Write-Host "Built: $artifact" -ForegroundColor Green }
Write-Host "Checksums: $sums" -ForegroundColor Green
