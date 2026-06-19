[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("all", "sql", "sqlserver", "mssql", "postgres", "mysql", "mongo", "mongodb", "filesystem", "files")]
    [string] $Provider,

    [Parameter(Position = 1)]
    [string] $Runtime,

    [Parameter(Position = 2)]
    [string] $Output,

    [string] $Configuration = "Release",

    [ValidateSet("none", "all", "otlp", "opentelemetry", "azuremonitor", "azure", "appinsights", "applicationinsights", "status", "statusendpoint", "http", "otlp-status", "azuremonitor-status", "otlp-azuremonitor", "otlp-azuremonitor-status")]
    [string] $MonitoringProvider = "none",

    [switch] $NoCache,

    [switch] $KeepImage
)

$ErrorActionPreference = "Stop"

function Normalize-Provider([string] $Value) {
    switch ($Value.ToLowerInvariant()) {
        "mssql" { "sqlserver" }
        "mongodb" { "mongo" }
        "files" { "filesystem" }
        default { $Value.ToLowerInvariant() }
    }
}

function Normalize-MonitoringProvider([string] $Value) {
    switch ($Value.ToLowerInvariant()) {
        "opentelemetry" { "otlp" }
        "azure" { "azuremonitor" }
        "appinsights" { "azuremonitor" }
        "applicationinsights" { "azuremonitor" }
        "statusendpoint" { "status" }
        "http" { "status" }
        default { $Value.ToLowerInvariant() }
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is required to publish DataSync without a local .NET SDK."
}

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $Runtime = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$providerName = Normalize-Provider $Provider
$monitoringProviderName = Normalize-MonitoringProvider $MonitoringProvider

if ([string]::IsNullOrWhiteSpace($Output)) {
    $outputName = if ($monitoringProviderName -eq "none") {
        "datasync-$providerName-$Runtime"
    } else {
        "datasync-$providerName-$monitoringProviderName-$Runtime"
    }
    $Output = Join-Path $repoRoot ".artifacts\publish\$outputName"
}

$imageTag = "sollatek-datasync-publish:$providerName-$monitoringProviderName-$Runtime-$([Guid]::NewGuid().ToString("N"))"
$dockerfile = Join-Path $scriptRoot "Dockerfile.publish"
$stagePath = Join-Path $repoRoot ".artifacts\publish\.tmp-$providerName-$monitoringProviderName-$Runtime-$([Guid]::NewGuid().ToString("N"))"

$buildArgs = @(
    "build",
    "--file", $dockerfile,
    "--target", "export",
    "--build-arg", "DATASYNC_PROVIDER=$providerName",
    "--build-arg", "DATASYNC_MONITORING_PROVIDER=$monitoringProviderName",
    "--build-arg", "RUNTIME_IDENTIFIER=$Runtime",
    "--build-arg", "CONFIGURATION=$Configuration",
    "--tag", $imageTag
)

if ($NoCache) {
    $buildArgs += "--no-cache"
}

$buildArgs += $repoRoot

docker @buildArgs

$containerId = docker create $imageTag
try {
    New-Item -ItemType Directory -Force -Path $stagePath | Out-Null
    docker cp "${containerId}:/out/." $stagePath

    if (Test-Path -LiteralPath $Output) {
        Remove-Item -LiteralPath $Output -Recurse -Force
    }

    $outputParent = Split-Path -Parent $Output
    if (-not [string]::IsNullOrWhiteSpace($outputParent)) {
        New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
    }

    Move-Item -LiteralPath $stagePath -Destination $Output
}
finally {
    if ($containerId) {
        docker rm $containerId | Out-Null
    }

    if (-not $KeepImage) {
        docker rmi $imageTag | Out-Null
    }

    if (Test-Path -LiteralPath $stagePath) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}

Write-Host "Published DataSync provider '$providerName' with monitoring '$monitoringProviderName' for runtime '$Runtime' to '$Output'."
