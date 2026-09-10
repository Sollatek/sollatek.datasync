[CmdletBinding()]
param(
    [string] $ArtifactRoot = (Join-Path (Get-Location) '.artifacts/release')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactsBoundary = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts'))
$artifactRootPath = [System.IO.Path]::GetFullPath($ArtifactRoot)
$pathComparison = if ([System.OperatingSystem]::IsWindows()) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}
$boundaryPrefix = $artifactsBoundary.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
) + [System.IO.Path]::DirectorySeparatorChar
if (-not $artifactRootPath.StartsWith($boundaryPrefix, $pathComparison)) {
    throw "ArtifactRoot must be a child of the repository .artifacts directory: $artifactsBoundary"
}

if (Test-Path -LiteralPath $artifactRootPath) {
    Remove-Item -LiteralPath $artifactRootPath -Recurse -Force
}

$portableRoot = Join-Path $artifactRootPath 'datasync'
$selfContainedRoot = Join-Path $artifactRootPath 'self-contained'
$licensePath = Join-Path $artifactRootPath 'LICENCE.md'

New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
New-Item -ItemType Directory -Path $selfContainedRoot -Force | Out-Null
Copy-Item -LiteralPath 'LICENCE.md' -Destination $licensePath -Force

dotnet publish 'Sollatek.DataSync/Sollatek.DataSync.csproj' `
    -c Release `
    -o $portableRoot `
    --no-self-contained `
    --disable-build-servers `
    -m:1 `
    -p:DataSyncProvider=all `
    -p:DataSyncMonitoringProvider=none `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "DataSync publish failed with exit code $LASTEXITCODE."
}

Remove-Item -LiteralPath (Join-Path $portableRoot 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $portableRoot -Filter '*.pdb' -File -Recurse | Remove-Item -Force
Copy-Item -LiteralPath 'deployment/publication/appsettings.public.json' -Destination (Join-Path $portableRoot 'appsettings.json') -Force
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $portableRoot 'LICENCE.md') -Force
Copy-Item -LiteralPath 'deployment/publication/Dockerfile.binary' -Destination (Join-Path $artifactRootPath 'Dockerfile.binary') -Force

tar --create --gzip --file (Join-Path $artifactRootPath 'sollatek-datasync-net10.tar.gz') --directory $portableRoot .
if ($LASTEXITCODE -ne 0) {
    throw "Portable release archive creation failed with exit code $LASTEXITCODE."
}

$targets = @(
    @{ Rid = 'linux-x64'; Archive = 'sollatek-datasync-linux-x64.tar.gz'; Windows = $false },
    @{ Rid = 'linux-arm64'; Archive = 'sollatek-datasync-linux-arm64.tar.gz'; Windows = $false },
    @{ Rid = 'win-x64'; Archive = 'sollatek-datasync-win-x64.zip'; Windows = $true },
    @{ Rid = 'win-arm64'; Archive = 'sollatek-datasync-win-arm64.zip'; Windows = $true },
    @{ Rid = 'osx-x64'; Archive = 'sollatek-datasync-osx-x64.tar.gz'; Windows = $false },
    @{ Rid = 'osx-arm64'; Archive = 'sollatek-datasync-osx-arm64.tar.gz'; Windows = $false }
)

foreach ($target in $targets) {
    $targetRoot = Join-Path $selfContainedRoot $target.Rid
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    dotnet publish 'Sollatek.DataSync/Sollatek.DataSync.csproj' `
        -c Release `
        -o $targetRoot `
        --runtime $target.Rid `
        --self-contained true `
        --disable-build-servers `
        -m:1 `
        -p:UseAppHost=true `
        -p:DataSyncProvider=all `
        -p:DataSyncMonitoringProvider=none `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "DataSync self-contained publish for $($target.Rid) failed with exit code $LASTEXITCODE."
    }

    Remove-Item -LiteralPath (Join-Path $targetRoot 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $targetRoot -Filter '*.pdb' -File -Recurse | Remove-Item -Force
    Copy-Item -LiteralPath 'deployment/publication/appsettings.public.json' -Destination (Join-Path $targetRoot 'appsettings.json') -Force
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $targetRoot 'LICENCE.md') -Force

    $archivePath = Join-Path $artifactRootPath $target.Archive
    if ($target.Windows) {
        Compress-Archive -Path (Join-Path $targetRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force
    } else {
        chmod +x -- (Join-Path $targetRoot 'Sollatek.DataSync')
        if ($LASTEXITCODE -ne 0) {
            throw "Could not mark the $($target.Rid) entry point as executable."
        }
        tar --create --gzip --file $archivePath --directory $targetRoot .
        if ($LASTEXITCODE -ne 0) {
            throw "Release archive creation for $($target.Rid) failed with exit code $LASTEXITCODE."
        }
    }
}

Write-Host "DataSync release bundle created at $artifactRootPath"
