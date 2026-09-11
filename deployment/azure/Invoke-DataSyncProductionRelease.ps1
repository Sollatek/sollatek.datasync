<#
.SYNOPSIS
Validates or deploys a published DataSync release and can start an isolated historical run.

.DESCRIPTION
Resolves a tested, immutable release published from an approved Sollatek DataSync release branch and validates
an existing Azure DataSync target. The target is discovered from the selected subscription, with
explicit resource-group, job, registry, and image-repository parameters available when discovery is
ambiguous. The default PrebuiltImage delivery imports the published image by digest into the existing
Azure Container Registry and updates only the Container Apps Job image.
Binary delivery downloads and verifies the compiled .NET 10 release, then assembles it with the
selected compatible runtime base image in ACR without compiling source code. Git, Docker, source code,
and a local .NET SDK are not required.

The OS-specific self-contained executable archives published with each release are intended for
direct Linux, Windows, or macOS hosting. This Azure Container Apps Job script deliberately uses the
portable compiled archive for Binary delivery so the selected base image supplies a known-compatible
.NET runtime and native operating-system dependencies.

Add -FreshHistoricalRun to plan or create a new private, timestamped Blob container for both exports
and state. Add -StartHistoricalRun with -Deploy and -FreshHistoricalRun to start the first execution.
The existing Blob container is never deleted or modified, and existing job secret references are
verified before and after the configuration update without reading their values.

.PARAMETER Subscription
Azure subscription name or ID containing the existing DataSync resources. The script uses the current
Azure CLI login and does not require a tenant parameter. SubscriptionId remains an alias for backward
compatibility.

.PARAMETER ResourceGroup
Optional existing Container Apps Job resource group. Supply it with JobName to select the target
directly, or supply it alone to limit automatic discovery to that resource group.

.PARAMETER JobName
Optional existing Container Apps Job name. Supply it with ResourceGroup to select the target directly.

.PARAMETER RegistryName
Optional existing Azure Container Registry name. By default, the script derives the registry from the
current job image and verifies it in the selected subscription.

.PARAMETER ImageRepository
Optional repository path inside the Azure Container Registry. By default, the script preserves the
repository path used by the current job image.

.PARAMETER Deploy
Applies the planned image update. Without this switch, the script validates the release and exact Azure
target and reports the plan without changing the job.

.PARAMETER DeliveryMode
PrebuiltImage imports the Sollatek-published multi-platform image by immutable digest and is recommended.
Binary downloads the checksum-verified portable compiled release and assembles it in ACR with BaseImage.
Neither mode clones the repository or compiles DataSync source code in the customer environment.

.PARAMETER ReleaseVersion
Published semantic version such as 1.0.0 or v1.0.0. latest resolves the newest published release when
the command runs; it does not update an existing Azure job until this script is run again with Deploy.

.PARAMETER BaseImage
Runtime base image used only with DeliveryMode Binary. It must provide a compatible .NET 10 runtime and
the native libraries required by DataSync. The default is mcr.microsoft.com/dotnet/runtime:10.0.

.PARAMETER FreshHistoricalRun
Plans or creates a new private Blob container and isolated export/state roots for a historical run.

.PARAMETER StartHistoricalRun
Starts the first execution after deployment. Requires both Deploy and FreshHistoricalRun.

.PARAMETER HistoricalStartFrom
UTC day boundary used as the beginning of a fresh historical run.

.PARAMETER FreshRunLabel
Short lowercase label included in the generated Blob container name.

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>'

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>' -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>' -ResourceGroup '<resource-group>' -JobName '<job-name>' -RegistryName '<registry-name>' -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>' -ReleaseVersion 1.0.0 -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>' -DeliveryMode Binary -BaseImage mcr.microsoft.com/dotnet/runtime:10.0 -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>' -FreshHistoricalRun

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -Subscription '<subscription-name-or-guid>' -Deploy -FreshHistoricalRun -StartHistoricalRun -HistoricalStartFrom 2025-01-01 -FreshRunLabel datasync
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Alias("SubscriptionId")]
    [ValidateNotNullOrEmpty()]
    [string] $Subscription,

    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroup,

    [ValidateNotNullOrEmpty()]
    [string] $JobName,

    [ValidateNotNullOrEmpty()]
    [string] $RegistryName,

    [ValidatePattern("^[a-z0-9]+(?:[._/-][a-z0-9]+)*$")]
    [string] $ImageRepository,

    [switch] $Deploy,

    [ValidateSet("PrebuiltImage", "Binary")]
    [string] $DeliveryMode = "PrebuiltImage",

    [ValidatePattern("^latest$|^release-[0-9a-f]{40}$|^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    [string] $ReleaseVersion = "latest",

    [ValidatePattern("^[A-Za-z0-9][A-Za-z0-9._/:@-]{0,255}$")]
    [string] $BaseImage = "mcr.microsoft.com/dotnet/runtime:10.0",

    [switch] $FreshHistoricalRun,

    [switch] $StartHistoricalRun,

    [DateTimeOffset] $HistoricalStartFrom = [DateTimeOffset]::Parse("2025-01-01T00:00:00Z"),

    [ValidatePattern("^(?!.*--)[a-z0-9](?:[a-z0-9-]{0,18}[a-z0-9])?$")]
    [string] $FreshRunLabel = "datasync"
)

$ErrorActionPreference = "Stop"

$releaseRepository = "Sollatek/sollatek.datasync"
$releaseBaseUri = "https://github.com/$releaseRepository/releases"
$supportedReleaseContracts = @("datasync-public-v1", "datasync-public-v2")
$expectedSourceImageRepository = "ghcr.io/sollatek/sollatek.datasync"
$expectedArtifactName = "sollatek-datasync-net10.tar.gz"
$expectedArtifactFramework = "net10.0"
$expectedArtifactEntryPoint = "Sollatek.DataSync.dll"

$stage = "initialization"
$exitCode = 1
$releaseTemp = $null
$freshContainerName = $null
$freshContainerCreated = $false

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList,

        [switch] $CaptureOutput
    )

    if ($CaptureOutput) {
        $output = & $Command @ArgumentList
    } else {
        & $Command @ArgumentList
        $output = $null
    }

    $commandExitCode = $LASTEXITCODE
    if ($commandExitCode -ne 0) {
        throw "Command '$Command' failed with exit code $commandExitCode."
    }

    if ($CaptureOutput) {
        return $output
    }
}

function ConvertFrom-CommandJson {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Output
    )

    $json = ($Output -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "Expected JSON command output, but the command returned no data."
    }

    $json | ConvertFrom-Json
}

function Assert-ExpectedValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [AllowEmptyString()]
        [string] $Actual,

        [Parameter(Mandatory = $true)]
        [string] $Expected
    )

    if ($Actual -ne $Expected) {
        throw "Unexpected $Name. Expected '$Expected', found '$Actual'."
    }
}

function Get-PublishedReleaseManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Version
    )

    $requestedRelease = if ($Version -eq "latest" -or $Version -like "release-*") {
        $Version
    } elseif ($Version.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        "v$($Version.Substring(1))"
    } else {
        "v$Version"
    }

    $manifestUri = if ($requestedRelease -eq "latest") {
        "$releaseBaseUri/latest/download/sollatek-datasync-release.json"
    } else {
        "$releaseBaseUri/download/$requestedRelease/sollatek-datasync-release.json"
    }

    try {
        $manifest = Invoke-RestMethod `
            -Method Get `
            -Uri $manifestUri `
            -Headers @{ "User-Agent" = "Sollatek-DataSync-Release" }
    } catch {
        throw "Could not download the published DataSync release manifest from '$manifestUri': $($_.Exception.Message)"
    }

    $contract = [string] $manifest.contract
    if ($contract -notin $supportedReleaseContracts) {
        throw "Unsupported release contract '$contract'."
    }

    $expectedSchemaVersion = if ($contract -eq "datasync-public-v2") { "2" } else { "1" }
    Assert-ExpectedValue -Name "release manifest schema" -Actual ([string] $manifest.schemaVersion) -Expected $expectedSchemaVersion
    Assert-ExpectedValue -Name "release repository" -Actual ([string] $manifest.repository) -Expected $releaseRepository

    $commit = [string] $manifest.commit
    if ($commit -notmatch "^[0-9a-f]{40}$") {
        throw "The release manifest commit is missing or invalid."
    }

    if ($contract -eq "datasync-public-v2") {
        $semanticVersion = [string] $manifest.version
        if ($semanticVersion -notmatch "^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$") {
            throw "The release manifest semantic version is missing or invalid."
        }
        $releaseTag = "v$semanticVersion"
    } else {
        $releaseTag = "release-$commit"
    }

    Assert-ExpectedValue -Name "release tag" -Actual ([string] $manifest.release) -Expected $releaseTag
    if ($requestedRelease -ne "latest") {
        Assert-ExpectedValue -Name "requested release" -Actual $releaseTag -Expected $requestedRelease
    }

    Assert-ExpectedValue `
        -Name "published image repository" `
        -Actual ([string] $manifest.image.repository) `
        -Expected $expectedSourceImageRepository
    if ($contract -eq "datasync-public-v2") {
        Assert-ExpectedValue `
            -Name "published image version tag" `
            -Actual ([string] $manifest.image.tag) `
            -Expected $semanticVersion
        Assert-ExpectedValue `
            -Name "published image immutable tag" `
            -Actual ([string] $manifest.image.immutableTag) `
            -Expected "sha-$commit"

        $imagePlatforms = @($manifest.image.platforms)
        if ($imagePlatforms.Count -ne 2 -or
            "linux/amd64" -notin $imagePlatforms -or
            "linux/arm64" -notin $imagePlatforms) {
            throw "The published image platforms must be exactly linux/amd64 and linux/arm64."
        }
    } else {
        Assert-ExpectedValue `
            -Name "published image tag" `
            -Actual ([string] $manifest.image.tag) `
            -Expected "sha-$commit"
        Assert-ExpectedValue -Name "published image platform" -Actual ([string] $manifest.image.platform) -Expected "linux/amd64"
    }

    if (([string] $manifest.image.digest) -notmatch "^sha256:[0-9a-f]{64}$") {
        throw "The published image digest is missing or invalid."
    }

    Assert-ExpectedValue -Name "release artifact name" -Actual ([string] $manifest.artifact.name) -Expected $expectedArtifactName
    Assert-ExpectedValue -Name "release artifact framework" -Actual ([string] $manifest.artifact.framework) -Expected $expectedArtifactFramework
    Assert-ExpectedValue -Name "release artifact deployment mode" -Actual ([string] $manifest.artifact.deploymentMode) -Expected "framework-dependent"
    Assert-ExpectedValue -Name "release artifact entry point" -Actual ([string] $manifest.artifact.entryPoint) -Expected $expectedArtifactEntryPoint
    if (([string] $manifest.artifact.sha256) -notmatch "^[0-9a-f]{64}$") {
        throw "The release artifact SHA-256 is missing or invalid."
    }

    if ($contract -eq "datasync-public-v2") {
        $expectedExecutables = [ordered] @{
            "linux-x64" = @{ Name = "sollatek-datasync-linux-x64.tar.gz"; EntryPoint = "Sollatek.DataSync"; ArchiveFormat = "tar.gz" }
            "linux-arm64" = @{ Name = "sollatek-datasync-linux-arm64.tar.gz"; EntryPoint = "Sollatek.DataSync"; ArchiveFormat = "tar.gz" }
            "win-x64" = @{ Name = "sollatek-datasync-win-x64.zip"; EntryPoint = "Sollatek.DataSync.exe"; ArchiveFormat = "zip" }
            "win-arm64" = @{ Name = "sollatek-datasync-win-arm64.zip"; EntryPoint = "Sollatek.DataSync.exe"; ArchiveFormat = "zip" }
            "osx-x64" = @{ Name = "sollatek-datasync-osx-x64.tar.gz"; EntryPoint = "Sollatek.DataSync"; ArchiveFormat = "tar.gz" }
            "osx-arm64" = @{ Name = "sollatek-datasync-osx-arm64.tar.gz"; EntryPoint = "Sollatek.DataSync"; ArchiveFormat = "tar.gz" }
        }
        $executables = @($manifest.executables)
        if ($executables.Count -ne $expectedExecutables.Count) {
            throw "The release manifest must contain exactly $($expectedExecutables.Count) self-contained executables."
        }

        foreach ($expectedExecutable in $expectedExecutables.GetEnumerator()) {
            $matches = @($executables | Where-Object { [string] $_.rid -eq $expectedExecutable.Key })
            if ($matches.Count -ne 1) {
                throw "The release manifest must contain exactly one '$($expectedExecutable.Key)' executable."
            }

            $executable = $matches[0]
            Assert-ExpectedValue -Name "$($expectedExecutable.Key) executable name" -Actual ([string] $executable.name) -Expected $expectedExecutable.Value.Name
            Assert-ExpectedValue -Name "$($expectedExecutable.Key) executable framework" -Actual ([string] $executable.framework) -Expected $expectedArtifactFramework
            Assert-ExpectedValue -Name "$($expectedExecutable.Key) executable deployment mode" -Actual ([string] $executable.deploymentMode) -Expected "self-contained"
            Assert-ExpectedValue -Name "$($expectedExecutable.Key) executable entry point" -Actual ([string] $executable.entryPoint) -Expected $expectedExecutable.Value.EntryPoint
            Assert-ExpectedValue -Name "$($expectedExecutable.Key) executable archive format" -Actual ([string] $executable.archiveFormat) -Expected $expectedExecutable.Value.ArchiveFormat
            if (([string] $executable.sha256) -notmatch "^[0-9a-f]{64}$") {
                throw "The $($expectedExecutable.Key) executable SHA-256 is missing or invalid."
            }
        }
    }

    $manifest
}

function Get-TextSha256Prefix {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Text,

        [ValidateRange(8, 64)]
        [int] $Length = 12
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        $hash = $algorithm.ComputeHash($bytes)
        $hex = -join ($hash | ForEach-Object { $_.ToString("x2") })
        $hex.Substring(0, $Length)
    } finally {
        $algorithm.Dispose()
    }
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ParentPath
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParentPath = [System.IO.Path]::GetFullPath(
        $ParentPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    )

    if (-not $fullPath.StartsWith($fullParentPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate on '$fullPath' because it is outside '$fullParentPath'."
    }

    $fullPath
}

function Get-ContainerAppsJobState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Subscription,

        [Parameter(Mandatory = $true)]
        [string] $ResourceGroup,

        [Parameter(Mandatory = $true)]
        [string] $JobName
    )

    ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "containerapp", "job", "show",
        "--subscription", $Subscription,
        "--resource-group", $ResourceGroup,
        "--name", $JobName,
        "--query", "{id:id,name:name,resourceGroup:resourceGroup,location:location,triggerType:configuration.triggerType,cron:configuration.scheduleTriggerConfig.cronExpression,image:template.containers[0].image,identityPrincipalId:identity.principalId}",
        "--output", "json",
        "--only-show-errors"
    ))
}

function Test-DataSyncJobCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Candidate
    )

    $candidateName = [string] $Candidate.name
    $candidateImage = [string] $Candidate.image
    $environmentNames = @($Candidate.environment | ForEach-Object { [string] $_.name })

    $candidateName -match "(?i)data[-.]?sync" -or
        $candidateImage -match "(?i)(^|/)(sollatek[.-]?datasync)(?=[:@/]|$)" -or
        (
            "SOL_Settings__oauthUrl" -in $environmentNames -and
            "SOL_Sync__transferMode" -in $environmentNames
        )
}

function Resolve-ContainerAppsJobTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Subscription,

        [string] $ResourceGroup,

        [string] $JobName
    )

    if (-not [string]::IsNullOrWhiteSpace($ResourceGroup) -and
        -not [string]::IsNullOrWhiteSpace($JobName)) {
        return Get-ContainerAppsJobState `
            -Subscription $Subscription `
            -ResourceGroup $ResourceGroup `
            -JobName $JobName
    }

    $listArguments = @(
        "containerapp", "job", "list",
        "--subscription", $Subscription
    )
    if (-not [string]::IsNullOrWhiteSpace($ResourceGroup)) {
        $listArguments += @("--resource-group", $ResourceGroup)
    }
    $listArguments += @(
        "--query", "[].{name:name,resourceGroup:resourceGroup,location:location,image:template.containers[0].image,environment:template.containers[0].env}",
        "--output", "json",
        "--only-show-errors"
    )

    try {
        $jobs = @(ConvertFrom-CommandJson -Output @(
            Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList $listArguments
        ))
    } catch {
        throw "Automatic Container Apps Job discovery failed. Supply both -ResourceGroup and -JobName to use a direct lookup. $($_.Exception.Message)"
    }

    if (-not [string]::IsNullOrWhiteSpace($JobName)) {
        $matches = @($jobs | Where-Object { [string] $_.name -eq $JobName })
    } else {
        $matches = @($jobs | Where-Object { Test-DataSyncJobCandidate -Candidate $_ })
    }

    if ($matches.Count -ne 1) {
        if ($matches.Count -gt 0) {
            Write-Host "Matching Container Apps Jobs:"
            foreach ($candidate in $matches) {
                Write-Host "  $([string] $candidate.resourceGroup)/$([string] $candidate.name) [$([string] $candidate.location)] image=$([string] $candidate.image)"
            }
        }

        if ($matches.Count -eq 0) {
            throw "No existing DataSync Container Apps Job could be identified in the selected scope. Supply -ResourceGroup and -JobName."
        }

        throw "More than one DataSync Container Apps Job matched. Supply -ResourceGroup and -JobName to select exactly one."
    }

    Get-ContainerAppsJobState `
        -Subscription $Subscription `
        -ResourceGroup ([string] $matches[0].resourceGroup) `
        -JobName ([string] $matches[0].name)
}

function Resolve-ContainerRegistryTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Subscription,

        [Parameter(Mandatory = $true)]
        [string] $CurrentImage,

        [string] $RegistryName
    )

    if (-not [string]::IsNullOrWhiteSpace($RegistryName)) {
        return ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "acr", "show",
            "--subscription", $Subscription,
            "--name", $RegistryName,
            "--query", "{id:id,name:name,resourceGroup:resourceGroup,loginServer:loginServer}",
            "--output", "json",
            "--only-show-errors"
        ))
    }

    $currentRegistryHost = ($CurrentImage -split "/", 2)[0]
    if ($currentRegistryHost -notmatch "(?i)\.azurecr\.io$") {
        throw "The current job image does not identify an Azure Container Registry. Supply -RegistryName explicitly."
    }

    try {
        $registries = @(ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "acr", "list",
            "--subscription", $Subscription,
            "--query", "[].{id:id,name:name,resourceGroup:resourceGroup,loginServer:loginServer}",
            "--output", "json",
            "--only-show-errors"
        )))
    } catch {
        throw "Automatic Azure Container Registry discovery failed. Supply -RegistryName to use a direct lookup. $($_.Exception.Message)"
    }

    $matches = @($registries | Where-Object {
        [string] $_.loginServer -eq $currentRegistryHost
    })
    if ($matches.Count -ne 1) {
        throw "Could not map current image registry '$currentRegistryHost' to exactly one registry in the selected subscription. Supply -RegistryName."
    }

    $matches[0]
}

function Resolve-ImageRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CurrentImage,

        [Parameter(Mandatory = $true)]
        [string] $RegistryLoginServer,

        [string] $ImageRepository
    )

    if (-not [string]::IsNullOrWhiteSpace($ImageRepository)) {
        return $ImageRepository
    }

    $prefix = "$RegistryLoginServer/"
    if (-not $CurrentImage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The current job image is not hosted in '$RegistryLoginServer'. Supply -ImageRepository explicitly."
    }

    $repositoryWithReference = $CurrentImage.Substring($prefix.Length)
    $repository = ($repositoryWithReference -split "@", 2)[0]
    $lastSlash = $repository.LastIndexOf("/")
    $lastColon = $repository.LastIndexOf(":")
    if ($lastColon -gt $lastSlash) {
        $repository = $repository.Substring(0, $lastColon)
    }

    if ([string]::IsNullOrWhiteSpace($repository)) {
        throw "Could not derive the image repository from the current job image. Supply -ImageRepository explicitly."
    }

    $repository
}

function Resolve-StorageAccountTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Subscription,

        [Parameter(Mandatory = $true)]
        [string] $AccountName
    )

    $accounts = @(ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "storage", "account", "list",
        "--subscription", $Subscription,
        "--query", "[].{id:id,name:name,resourceGroup:resourceGroup}",
        "--output", "json",
        "--only-show-errors"
    )))
    $matches = @($accounts | Where-Object { [string] $_.name -eq $AccountName })
    if ($matches.Count -ne 1) {
        throw "Could not map storage account '$AccountName' to exactly one account in the selected subscription."
    }

    $matches[0]
}

function Get-JobEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResourceGroup,

        [Parameter(Mandatory = $true)]
        [string] $JobName,

        [Parameter(Mandatory = $true)]
        [string] $Subscription
    )

    $environment = ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "containerapp", "job", "show",
        "--subscription", $Subscription,
        "--resource-group", $ResourceGroup,
        "--name", $JobName,
        "--query", "template.containers[0].env",
        "--output", "json",
        "--only-show-errors"
    ))

    @($environment)
}

function Get-JobEnvironmentEntry {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Environment,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $matches = @($Environment | Where-Object { [string] $_.name -eq $Name })
    if ($matches.Count -gt 1) {
        throw "The Container Apps Job has duplicate environment variable '$Name' entries."
    }

    if ($matches.Count -eq 0) {
        return $null
    }

    $matches[0]
}

function Get-RunningExecutionNames {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResourceGroup,

        [Parameter(Mandatory = $true)]
        [string] $JobName,

        [Parameter(Mandatory = $true)]
        [string] $Subscription
    )

    $names = @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "containerapp", "job", "execution", "list",
        "--subscription", $Subscription,
        "--resource-group", $ResourceGroup,
        "--name", $JobName,
        "--query", "[?properties.status=='Running'].name",
        "--output", "tsv",
        "--only-show-errors"
    ))

    @($names | Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) })
}

function Assert-NoRunningExecution {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResourceGroup,

        [Parameter(Mandatory = $true)]
        [string] $JobName,

        [Parameter(Mandatory = $true)]
        [string] $Subscription
    )

    $runningExecutions = @(Get-RunningExecutionNames `
        -ResourceGroup $ResourceGroup `
        -JobName $JobName `
        -Subscription $Subscription)

    if ($runningExecutions.Count -gt 0) {
        throw "The Container Apps Job already has a running execution: $($runningExecutions -join ', ')."
    }
}

function Assert-SecretReference {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Environment,

        [Parameter(Mandatory = $true)]
        [string] $Name,

        [string] $ExpectedSecretReference
    )

    $entry = Get-JobEnvironmentEntry -Environment $Environment -Name $Name
    if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string] $entry.secretRef)) {
        throw "The Container Apps Job environment variable '$Name' must use an existing secret reference."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSecretReference) -and
        [string] $entry.secretRef -ne $ExpectedSecretReference) {
        throw "The Container Apps Job secret reference for '$Name' changed unexpectedly."
    }

    [string] $entry.secretRef
}

try {
    $stage = "preflight"
    foreach ($commandName in @("az")) {
        if ($null -eq (Get-Command $commandName -ErrorAction SilentlyContinue)) {
            throw "Required command is not available: $commandName"
        }
    }

    if ($DeliveryMode -eq "Binary" -and $null -eq (Get-Command "tar" -ErrorAction SilentlyContinue)) {
        throw "Binary delivery requires the tar command available in Azure Cloud Shell."
    }

    if ($DeliveryMode -eq "PrebuiltImage" -and $PSBoundParameters.ContainsKey("BaseImage")) {
        throw "-BaseImage applies only when -DeliveryMode Binary is selected."
    }

    if ($StartHistoricalRun -and -not $FreshHistoricalRun) {
        throw "-StartHistoricalRun requires -FreshHistoricalRun."
    }

    if ($StartHistoricalRun -and -not $Deploy) {
        throw "-StartHistoricalRun requires -Deploy so the tested canonical image is deployed first."
    }

    $historicalStartUtc = $HistoricalStartFrom.ToUniversalTime()
    if ($FreshHistoricalRun) {
        if ($historicalStartUtc.UtcDateTime.TimeOfDay -ne [TimeSpan]::Zero) {
            throw "-HistoricalStartFrom must be a UTC day boundary for a daily historical run."
        }

        $nowUtc = [DateTimeOffset]::UtcNow
        $currentUtcDayStart = [DateTimeOffset]::new(
            $nowUtc.Year,
            $nowUtc.Month,
            $nowUtc.Day,
            0,
            0,
            0,
            [TimeSpan]::Zero
        )
        if ($historicalStartUtc -ge $currentUtcDayStart) {
            throw "-HistoricalStartFrom must be earlier than the current UTC day."
        }
    }

    $historicalStartText = $historicalStartUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

    $stage = "resolving the published DataSync release"
    $releaseManifest = Get-PublishedReleaseManifest -Version $ReleaseVersion
    $canonicalCommit = [string] $releaseManifest.commit
    $releaseTag = [string] $releaseManifest.release
    $shortCommit = $canonicalCommit.Substring(0, 12)
    $releaseStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
    $publishedImage = "$([string] $releaseManifest.image.repository)@$([string] $releaseManifest.image.digest)"
    $artifactUri = "$releaseBaseUri/download/$releaseTag/$expectedArtifactName"

    if ($FreshHistoricalRun) {
        $historicalStartKey = $historicalStartUtc.UtcDateTime.ToString("yyyyMMdd")
        $freshContainerName = "sollatek-datasync-$FreshRunLabel-$historicalStartKey-$releaseStamp"
        $freshExportRoot = "exports"
        $freshStateRoot = "_state"
        $freshEnvironmentValues = [ordered] @{
            "SOL_Sync__runOnStartup" = "historicalOnly"
            "SOL_Sync__stopWhenFinished" = "true"
            "SOL_Sync__schedule__mode" = "daily"
            "SOL_Sync__schedule__time" = "01:00:00"
            "SOL_Sync__startFrom" = $historicalStartText
            "SOL_Sync__transferMode" = "asyncExport"
            "SOL_Storage__containerName" = $freshContainerName
            "SOL_FileExport__rootPath" = $freshExportRoot
            "SOL_FileExport__folderFormat" = "yyyyMM"
            "SOL_FileExport__fileNameFormat" = "{entity}_{date:yyyyMMdd}.{format}"
            "SOL_FileExport__format" = "parquet"
            "SOL_State__provider" = "azureBlobStorage"
            "SOL_State__rootPath" = $freshStateRoot
            "SOL_FileExport__replaceExisting" = "true"
        }
    }

    $stage = "validating the current Azure login and subscription"
    $account = ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "account", "show",
        "--subscription", $Subscription,
        "--query", "{id:id,name:name}",
        "--output", "json",
        "--only-show-errors"
    ))
    $subscriptionText = [string] $account.id
    if ([string]::IsNullOrWhiteSpace($subscriptionText)) {
        throw "Could not resolve Azure subscription '$Subscription'."
    }

    Invoke-CheckedCommand -Command "az" -ArgumentList @(
        "extension", "add", "--name", "containerapp", "--upgrade", "--only-show-errors"
    )

    $stage = "discovering the existing DataSync Container Apps Job"
    $jobState = Resolve-ContainerAppsJobTarget `
        -Subscription $subscriptionText `
        -ResourceGroup $ResourceGroup `
        -JobName $JobName
    $targetResourceGroup = [string] $jobState.resourceGroup
    $targetJobName = [string] $jobState.name
    $targetLocation = [string] $jobState.location
    if ([string]::IsNullOrWhiteSpace($targetResourceGroup) -or
        [string]::IsNullOrWhiteSpace($targetJobName) -or
        [string]::IsNullOrWhiteSpace($targetLocation)) {
        throw "The selected Container Apps Job did not return a complete resource group, name, and location."
    }

    Assert-ExpectedValue -Name "job trigger" -Actual ([string] $jobState.triggerType) -Expected "Schedule"
    $existingCronExpression = [string] $jobState.cron
    if ([string]::IsNullOrWhiteSpace($existingCronExpression)) {
        throw "The selected scheduled Container Apps Job has no cron expression."
    }

    $previousImage = [string] $jobState.image
    if ([string]::IsNullOrWhiteSpace($previousImage)) {
        throw "Could not determine the current image for the selected Container Apps Job."
    }

    $stage = "discovering the existing Azure Container Registry"
    $registry = Resolve-ContainerRegistryTarget `
        -Subscription $subscriptionText `
        -CurrentImage $previousImage `
        -RegistryName $RegistryName
    $targetRegistryName = [string] $registry.name
    $targetRegistryResourceGroup = [string] $registry.resourceGroup
    $targetRegistryLoginServer = [string] $registry.loginServer
    if ([string]::IsNullOrWhiteSpace($targetRegistryName) -or
        [string]::IsNullOrWhiteSpace($targetRegistryResourceGroup) -or
        [string]::IsNullOrWhiteSpace($targetRegistryLoginServer)) {
        throw "The selected Azure Container Registry did not return a complete name, resource group, and login server."
    }

    $targetImageRepository = Resolve-ImageRepository `
        -CurrentImage $previousImage `
        -RegistryLoginServer $targetRegistryLoginServer `
        -ImageRepository $ImageRepository

    if ($FreshHistoricalRun) {
        $stage = "validating fresh historical-run prerequisites"
        Assert-NoRunningExecution `
            -ResourceGroup $targetResourceGroup `
            -JobName $targetJobName `
            -Subscription $subscriptionText

        $jobEnvironmentBefore = @(Get-JobEnvironment `
            -ResourceGroup $targetResourceGroup `
            -JobName $targetJobName `
            -Subscription $subscriptionText)
        $clientKeySecretReference = Assert-SecretReference `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Settings__clientKey"
        $clientSecretReference = Assert-SecretReference `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Settings__clientSecret"

        $liveStorageAccount = [string] (Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Storage__accountName").value
        if ([string]::IsNullOrWhiteSpace($liveStorageAccount)) {
            throw "The live Azure Blob storage account name is missing."
        }
        $liveStorageProvider = [string] (Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Storage__provider").value
        Assert-ExpectedValue -Name "live storage provider" -Actual $liveStorageProvider -Expected "azureBlobStorage"
        $liveStorageAuthentication = [string] (Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Storage__authentication").value
        Assert-ExpectedValue -Name "live storage authentication" -Actual $liveStorageAuthentication -Expected "defaultAzureCredential"
        $liveStorageContainer = [string] (Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Storage__containerName").value
        if ([string]::IsNullOrWhiteSpace($liveStorageContainer)) {
            throw "The live Blob storage container name is missing."
        }
        $liveContainerUriEntry = Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_Storage__containerUri"
        if ($null -ne $liveContainerUriEntry -and
            -not [string]::IsNullOrWhiteSpace([string] $liveContainerUriEntry.value)) {
            throw "SOL_Storage__containerUri must be empty because it would override the fresh container name."
        }

        $identityPrincipalId = [string] $jobState.identityPrincipalId
        if ([string]::IsNullOrWhiteSpace($identityPrincipalId)) {
            throw "The Container Apps Job must have a system-assigned managed identity."
        }

        $storageAccount = Resolve-StorageAccountTarget `
            -Subscription $subscriptionText `
            -AccountName $liveStorageAccount
        $storageAccountId = [string] $storageAccount.id
        $targetStorageResourceGroup = [string] $storageAccount.resourceGroup
        if ([string]::IsNullOrWhiteSpace($storageAccountId) -or
            [string]::IsNullOrWhiteSpace($targetStorageResourceGroup)) {
            throw "The live Azure Blob storage account did not return a complete resource ID and resource group."
        }

        $storageRoleCountText = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "role", "assignment", "list",
            "--subscription", $subscriptionText,
            "--assignee-object-id", $identityPrincipalId,
            "--scope", $storageAccountId,
            "--include-inherited",
            "--role", "Storage Blob Data Contributor",
            "--query", "length(@)",
            "--output", "tsv",
            "--only-show-errors"
        )) -join "").Trim()
        $storageRoleCount = 0
        if (-not [int]::TryParse($storageRoleCountText, [ref] $storageRoleCount) -or $storageRoleCount -lt 1) {
            throw "The Container Apps Job identity requires Storage Blob Data Contributor on the storage account or an inherited scope before a fresh container can be used."
        }

        $freshContainerExists = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "storage", "container-rm", "exists",
            "--subscription", $subscriptionText,
            "--resource-group", $targetStorageResourceGroup,
            "--storage-account", $liveStorageAccount,
            "--name", $freshContainerName,
            "--query", "exists",
            "--output", "tsv",
            "--only-show-errors"
        )) -join "").Trim()
        Assert-ExpectedValue -Name "fresh Blob container existence" -Actual $freshContainerExists.ToLowerInvariant() -Expected "false"

        $previousExportRoot = [string] (Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_FileExport__rootPath").value
        $previousStateRoot = [string] (Get-JobEnvironmentEntry `
            -Environment $jobEnvironmentBefore `
            -Name "SOL_State__rootPath").value
    }

    $imageAlreadyAvailable = $false
    if ($DeliveryMode -eq "PrebuiltImage") {
        $imageTag = [string] $releaseManifest.image.tag
    } else {
        $baseImageKey = Get-TextSha256Prefix -Text $BaseImage
        $imageTag = "$releaseStamp-$shortCommit-b$baseImageKey"
    }
    $expectedImage = "${targetRegistryLoginServer}/${targetImageRepository}:$imageTag"

    $existingTag = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "acr", "repository", "show-tags",
        "--subscription", $subscriptionText,
        "--name", $targetRegistryName,
        "--repository", $targetImageRepository,
        "--query", "[?@=='$imageTag'] | [0]",
        "--output", "tsv",
        "--only-show-errors"
    )) -join "").Trim()
    if (-not [string]::IsNullOrWhiteSpace($existingTag)) {
        if ($DeliveryMode -ne "PrebuiltImage") {
            throw "The immutable image tag already exists: $imageTag"
        }

        $existingDigest = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "acr", "repository", "show",
            "--subscription", $subscriptionText,
            "--name", $targetRegistryName,
            "--image", "${targetImageRepository}:$imageTag",
            "--query", "digest",
            "--output", "tsv",
            "--only-show-errors"
        )) -join "").Trim()
        Assert-ExpectedValue `
            -Name "existing immutable image digest" `
            -Actual $existingDigest `
            -Expected ([string] $releaseManifest.image.digest)
        $imageAlreadyAvailable = $true
    }

    if (-not $Deploy) {
        Write-Host ""
        Write-Host "SUCCESS: published release and Azure target validation passed. Nothing was deployed."
        Write-Host "Release: $releaseTag"
        Write-Host "Commit: $canonicalCommit"
        Write-Host "Delivery mode: $DeliveryMode"
        Write-Host "Subscription: $([string] $account.name) ($subscriptionText)"
        Write-Host "Target job: $targetResourceGroup/$targetJobName"
        Write-Host "Target location: $targetLocation"
        Write-Host "Target registry: $targetRegistryResourceGroup/$targetRegistryName"
        if ($DeliveryMode -eq "PrebuiltImage") {
            Write-Host "Published image: $publishedImage"
        } else {
            Write-Host "Published binary: $artifactUri"
            Write-Host "Selected base image: $BaseImage"
        }
        Write-Host "Planned image: $expectedImage"
        if ($FreshHistoricalRun) {
            Write-Host "Planned historical start: $historicalStartText"
            Write-Host "Planned new Blob container: https://$liveStorageAccount.blob.core.windows.net/$freshContainerName"
            Write-Host "Planned export path in new container: $freshExportRoot"
            Write-Host "Planned state path in new container: $freshStateRoot"
            Write-Host "Existing container '$liveStorageContainer' will not be modified or deleted."
        }
        Write-Host "Run again with -Deploy to import or assemble the tested release and update only the selected job image."
        $exitCode = 0
    } else {
        if ($DeliveryMode -eq "PrebuiltImage") {
            if ($imageAlreadyAvailable) {
                Write-Host "The verified immutable release image is already present in ACR: $expectedImage"
            } else {
                $stage = "importing the published immutable image"
                Invoke-CheckedCommand -Command "az" -ArgumentList @(
                    "acr", "import",
                    "--subscription", $subscriptionText,
                    "--resource-group", $targetRegistryResourceGroup,
                    "--name", $targetRegistryName,
                    "--source", $publishedImage,
                    "--image", "${targetImageRepository}:$imageTag",
                    "--force",
                    "--output", "none",
                    "--only-show-errors"
                )
            }
        } else {
            $stage = "downloading the published compiled release"
            $releaseTemp = Join-Path ([System.IO.Path]::GetTempPath()) (
                "datasync-binary-release-" + [Guid]::NewGuid().ToString("N")
            )
            New-Item -ItemType Directory -Path $releaseTemp | Out-Null
            $releaseTemp = Assert-ChildPath -Path $releaseTemp -ParentPath ([System.IO.Path]::GetTempPath())

            $archivePath = Join-Path $releaseTemp $expectedArtifactName
            Invoke-WebRequest `
                -Uri $artifactUri `
                -OutFile $archivePath `
                -Headers @{ "User-Agent" = "Sollatek-DataSync-Release" } `
                -UseBasicParsing

            $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-ExpectedValue `
                -Name "compiled release SHA-256" `
                -Actual $archiveHash `
                -Expected ([string] $releaseManifest.artifact.sha256)

            $datasyncPath = Join-Path $releaseTemp "datasync"
            New-Item -ItemType Directory -Path $datasyncPath | Out-Null
            Invoke-CheckedCommand -Command "tar" -ArgumentList @(
                "--extract",
                "--gzip",
                "--file", $archivePath,
                "--directory", $datasyncPath
            )

            foreach ($requiredFile in @(
                "Sollatek.DataSync.dll",
                "Sollatek.DataSync.deps.json",
                "Sollatek.DataSync.runtimeconfig.json",
                "appsettings.json"
            )) {
                if (-not (Test-Path -LiteralPath (Join-Path $datasyncPath $requiredFile) -PathType Leaf)) {
                    throw "The verified compiled release is missing required file '$requiredFile'."
                }
            }

            $binaryDockerfilePath = Join-Path $releaseTemp "Dockerfile.binary"
            @(
                "ARG BASE_IMAGE",
                'FROM ${BASE_IMAGE}',
                "WORKDIR /app",
                "COPY datasync/ .",
                "ENV FileExport__rootPath=/app/volumes/webapi/exports",
                'VOLUME ["/app/volumes/webapi/exports"]',
                'ENTRYPOINT ["dotnet", "Sollatek.DataSync.dll"]'
            ) | Set-Content -LiteralPath $binaryDockerfilePath -Encoding utf8

            $stage = "assembling the selected runtime image in ACR"
            Push-Location -LiteralPath $releaseTemp
            try {
                Invoke-CheckedCommand -Command "az" -ArgumentList @(
                    "acr", "build",
                    "--subscription", $subscriptionText,
                    "--resource-group", $targetRegistryResourceGroup,
                    "--registry", $targetRegistryName,
                    "--image", "${targetImageRepository}:$imageTag",
                    "--file", "Dockerfile.binary",
                    "--build-arg", "BASE_IMAGE=$BaseImage",
                    "."
                )
            } finally {
                Pop-Location
            }
        }

        $stage = "verifying the imported or assembled image"
        $imageDigest = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "acr", "repository", "show",
            "--subscription", $subscriptionText,
            "--name", $targetRegistryName,
            "--image", "${targetImageRepository}:$imageTag",
            "--query", "digest",
            "--output", "tsv",
            "--only-show-errors"
        )) -join "").Trim()
        if ([string]::IsNullOrWhiteSpace($imageDigest)) {
            throw "The release image exists, but its ACR digest could not be verified."
        }
        if ($DeliveryMode -eq "PrebuiltImage") {
            Assert-ExpectedValue `
                -Name "imported image digest" `
                -Actual $imageDigest `
                -Expected ([string] $releaseManifest.image.digest)
        }

        $stage = "updating only the selected job image"
        Invoke-CheckedCommand -Command "az" -ArgumentList @(
            "containerapp", "job", "update",
            "--subscription", $subscriptionText,
            "--resource-group", $targetResourceGroup,
            "--name", $targetJobName,
            "--image", $expectedImage,
            "--output", "none",
            "--only-show-errors"
        )

        $stage = "verifying the deployed image"
        $deployedState = Get-ContainerAppsJobState `
            -Subscription $subscriptionText `
            -ResourceGroup $targetResourceGroup `
            -JobName $targetJobName
        Assert-ExpectedValue -Name "deployed image" -Actual ([string] $deployedState.image) -Expected $expectedImage
        Assert-ExpectedValue -Name "post-deployment trigger" -Actual ([string] $deployedState.triggerType) -Expected "Schedule"
        Assert-ExpectedValue -Name "post-deployment cron expression" -Actual ([string] $deployedState.cron) -Expected $existingCronExpression
        Assert-ExpectedValue -Name "post-deployment location" -Actual ([string] $deployedState.location) -Expected $targetLocation

        $historicalExecutionStarted = $false
        if ($FreshHistoricalRun) {
            $stage = "checking for active executions before the fresh historical update"
            Assert-NoRunningExecution `
                -ResourceGroup $targetResourceGroup `
                -JobName $targetJobName `
                -Subscription $subscriptionText

            $stage = "creating the fresh Blob container"
            Invoke-CheckedCommand -Command "az" -ArgumentList @(
                "storage", "container-rm", "create",
                "--subscription", $subscriptionText,
                "--resource-group", $targetStorageResourceGroup,
                "--storage-account", $liveStorageAccount,
                "--name", $freshContainerName,
                "--public-access", "off",
                "--fail-on-exist",
                "--output", "none",
                "--only-show-errors"
            )
            $freshContainerCreated = $true

            $createdContainerExists = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
                "storage", "container-rm", "exists",
                "--subscription", $subscriptionText,
                "--resource-group", $targetStorageResourceGroup,
                "--storage-account", $liveStorageAccount,
                "--name", $freshContainerName,
                "--query", "exists",
                "--output", "tsv",
                "--only-show-errors"
            )) -join "").Trim()
            Assert-ExpectedValue -Name "created Blob container existence" -Actual $createdContainerExists.ToLowerInvariant() -Expected "true"

            $stage = "configuring the fresh historical run"
            $freshEnvironmentArguments = @(
                "containerapp", "job", "update",
                "--subscription", $subscriptionText,
                "--resource-group", $targetResourceGroup,
                "--name", $targetJobName,
                "--set-env-vars"
            )
            foreach ($entry in $freshEnvironmentValues.GetEnumerator()) {
                $freshEnvironmentArguments += "$($entry.Key)=$($entry.Value)"
            }
            $freshEnvironmentArguments += @("--output", "none", "--only-show-errors")
            Invoke-CheckedCommand -Command "az" -ArgumentList $freshEnvironmentArguments

            $stage = "verifying the fresh historical configuration"
            $jobEnvironmentAfter = @(Get-JobEnvironment `
                -ResourceGroup $targetResourceGroup `
                -JobName $targetJobName `
                -Subscription $subscriptionText)
            Assert-SecretReference `
                -Environment $jobEnvironmentAfter `
                -Name "SOL_Settings__clientKey" `
                -ExpectedSecretReference $clientKeySecretReference | Out-Null
            Assert-SecretReference `
                -Environment $jobEnvironmentAfter `
                -Name "SOL_Settings__clientSecret" `
                -ExpectedSecretReference $clientSecretReference | Out-Null

            foreach ($entry in $freshEnvironmentValues.GetEnumerator()) {
                $actualEntry = Get-JobEnvironmentEntry `
                    -Environment $jobEnvironmentAfter `
                    -Name $entry.Key
                $actualValue = if ($null -eq $actualEntry) { "" } else { [string] $actualEntry.value }
                Assert-ExpectedValue `
                    -Name "job environment variable $($entry.Key)" `
                    -Actual $actualValue `
                    -Expected ([string] $entry.Value)
            }

            if ($StartHistoricalRun) {
                $stage = "checking for active executions before starting the historical run"
                Assert-NoRunningExecution `
                    -ResourceGroup $targetResourceGroup `
                    -JobName $targetJobName `
                    -Subscription $subscriptionText

                $stage = "starting the fresh historical run"
                Invoke-CheckedCommand -Command "az" -ArgumentList @(
                    "containerapp", "job", "start",
                    "--subscription", $subscriptionText,
                    "--resource-group", $targetResourceGroup,
                    "--name", $targetJobName,
                    "--output", "none",
                    "--only-show-errors"
                )
                $historicalExecutionStarted = $true
            }
        }

        Write-Host ""
        Write-Host "SUCCESS: the tested release image was deployed to the selected job."
        Write-Host "Release: $releaseTag"
        Write-Host "Commit: $canonicalCommit"
        Write-Host "Delivery mode: $DeliveryMode"
        Write-Host "Subscription: $([string] $account.name) ($subscriptionText)"
        Write-Host "Target job: $targetResourceGroup/$targetJobName"
        Write-Host "Target location: $targetLocation"
        Write-Host "Target registry: $targetRegistryResourceGroup/$targetRegistryName"
        Write-Host "Image: $expectedImage"
        Write-Host "Digest: $imageDigest"
        Write-Host "Previous image: $previousImage"
        Write-Host "Schedule: $existingCronExpression UTC"
        if ($FreshHistoricalRun) {
            Write-Host "Historical start: $historicalStartText"
            Write-Host "New Blob container: https://$liveStorageAccount.blob.core.windows.net/$freshContainerName"
            Write-Host "Export path in new container: $freshExportRoot"
            Write-Host "State path in new container: $freshStateRoot"
            Write-Host "Previous Blob container: $liveStorageContainer"
            Write-Host "Previous export path: $previousExportRoot"
            Write-Host "Previous state path: $previousStateRoot"
            Write-Host "The previous container and its data were not modified or deleted."
        }
        if ($historicalExecutionStarted) {
            Write-Host "The first fresh historical execution was started."
        } else {
            Write-Host "The job was not started manually; the next scheduled execution will use the deployed configuration."
        }
        $exitCode = 0
    }
} catch {
    Write-Error "FAILED during '$stage': $($_.Exception.Message)" -ErrorAction Continue
    if ($freshContainerCreated) {
        Write-Warning "The new Blob container '$freshContainerName' was created and was not deleted automatically."
    }
    $exitCode = 1
} finally {
    if (-not [string]::IsNullOrWhiteSpace($releaseTemp) -and (Test-Path -LiteralPath $releaseTemp)) {
        try {
            $releaseTemp = Assert-ChildPath -Path $releaseTemp -ParentPath ([System.IO.Path]::GetTempPath())
            Remove-Item -LiteralPath $releaseTemp -Recurse -Force
        } catch {
            Write-Error "Cleanup failed for temporary release directory '$releaseTemp': $($_.Exception.Message)" -ErrorAction Continue
            $exitCode = 1
        }
    }
}

exit $exitCode
