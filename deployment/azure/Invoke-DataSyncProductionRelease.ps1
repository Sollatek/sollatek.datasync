<#
.SYNOPSIS
Validates or deploys a published DataSync release and can start an isolated historical run.

.DESCRIPTION
Resolves a tested, immutable release published from the Sollatek DataSync master branch and validates
the exact Azure production target. The default PrebuiltImage delivery imports the published image by
digest into the existing Azure Container Registry and updates only the Container Apps Job image.
Binary delivery downloads and verifies the compiled .NET 10 release, then assembles it with the
selected compatible runtime base image in ACR without compiling source code. Git, Docker, source code,
and a local .NET SDK are not required.

Add -FreshHistoricalRun to plan or create a new private, timestamped Blob container for both exports
and state. Add -StartHistoricalRun with -Deploy and -FreshHistoricalRun to start the first execution.
The existing Blob container is never deleted or modified, and existing job secret references are
verified before and after the configuration update without reading their values.

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -SubscriptionId <subscription-guid>

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -SubscriptionId <subscription-guid> -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -SubscriptionId <subscription-guid> -DeliveryMode Binary -BaseImage mcr.microsoft.com/dotnet/runtime:10.0 -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -SubscriptionId <subscription-guid> -FreshHistoricalRun

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -SubscriptionId <subscription-guid> -Deploy -FreshHistoricalRun -StartHistoricalRun -HistoricalStartFrom 2025-01-01 -FreshRunLabel datasync
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Guid] $SubscriptionId,

    [switch] $Deploy,

    [ValidateSet("PrebuiltImage", "Binary")]
    [string] $DeliveryMode = "PrebuiltImage",

    [ValidatePattern("^latest$|^release-[0-9a-f]{40}$")]
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

$expectedResourceGroup = "rg-sollatek-datasync-prod"
$expectedLocation = "westeurope"
$expectedAcrName = "acrsollatekdatasync001"
$expectedImageName = "sollatek-datasync"
$expectedJobName = "sollatek-datasync-daily"
$expectedStorageAccountName = "stsollatekdsync001"
$expectedStorageContainerName = "sollatek-datasync"
$expectedCronExpression = "0 1 * * *"
$requiredTokenEndpointPath = "/realms/platform/protocol/openid-connect/token"
$releaseRepository = "Sollatek/sollatek.datasync"
$releaseBaseUri = "https://github.com/$releaseRepository/releases"
$requiredReleaseContract = "datasync-public-v1"
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

    $manifestUri = if ($Version -eq "latest") {
        "$releaseBaseUri/latest/download/sollatek-datasync-release.json"
    } else {
        "$releaseBaseUri/download/$Version/sollatek-datasync-release.json"
    }

    try {
        $manifest = Invoke-RestMethod `
            -Method Get `
            -Uri $manifestUri `
            -Headers @{ "User-Agent" = "Sollatek-DataSync-Release" }
    } catch {
        throw "Could not download the published DataSync release manifest from '$manifestUri': $($_.Exception.Message)"
    }

    Assert-ExpectedValue -Name "release manifest schema" -Actual ([string] $manifest.schemaVersion) -Expected "1"
    Assert-ExpectedValue -Name "release contract" -Actual ([string] $manifest.contract) -Expected $requiredReleaseContract
    Assert-ExpectedValue -Name "release repository" -Actual ([string] $manifest.repository) -Expected $releaseRepository

    $commit = [string] $manifest.commit
    if ($commit -notmatch "^[0-9a-f]{40}$") {
        throw "The release manifest commit is missing or invalid."
    }

    $releaseTag = "release-$commit"
    Assert-ExpectedValue -Name "release tag" -Actual ([string] $manifest.release) -Expected $releaseTag
    if ($Version -ne "latest") {
        Assert-ExpectedValue -Name "requested release" -Actual $releaseTag -Expected $Version
    }

    Assert-ExpectedValue `
        -Name "published image repository" `
        -Actual ([string] $manifest.image.repository) `
        -Expected $expectedSourceImageRepository
    Assert-ExpectedValue `
        -Name "published image tag" `
        -Actual ([string] $manifest.image.tag) `
        -Expected "sha-$commit"
    Assert-ExpectedValue -Name "published image platform" -Actual ([string] $manifest.image.platform) -Expected "linux/amd64"
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
            "SOL_Settings__oauthTokenEndpointPath" = $requiredTokenEndpointPath
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

    $stage = "validating the current Azure login and production subscription"
    $subscriptionText = $SubscriptionId.ToString()

    Invoke-CheckedCommand -Command "az" -ArgumentList @(
        "account", "set", "--subscription", $subscriptionText
    )

    $account = ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "account", "show",
        "--subscription", $subscriptionText,
        "--query", "{id:id,name:name}",
        "--output", "json",
        "--only-show-errors"
    ))
    Assert-ExpectedValue -Name "Azure subscription" -Actual ([string] $account.id) -Expected $subscriptionText

    Invoke-CheckedCommand -Command "az" -ArgumentList @(
        "extension", "add", "--name", "containerapp", "--upgrade", "--only-show-errors"
    )

    $actualLocation = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "group", "show",
        "--name", $expectedResourceGroup,
        "--query", "location",
        "--output", "tsv",
        "--only-show-errors"
    )) -join "").Trim()
    Assert-ExpectedValue -Name "production resource-group region" -Actual $actualLocation -Expected $expectedLocation

    $acrName = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "acr", "show",
        "--resource-group", $expectedResourceGroup,
        "--name", $expectedAcrName,
        "--query", "name",
        "--output", "tsv",
        "--only-show-errors"
    )) -join "").Trim()
    Assert-ExpectedValue -Name "production registry" -Actual $acrName -Expected $expectedAcrName

    $jobState = ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "containerapp", "job", "show",
        "--resource-group", $expectedResourceGroup,
        "--name", $expectedJobName,
        "--query", "{triggerType:configuration.triggerType,cron:configuration.scheduleTriggerConfig.cronExpression,image:template.containers[0].image,identityPrincipalId:identity.principalId}",
        "--output", "json",
        "--only-show-errors"
    ))
    Assert-ExpectedValue -Name "job trigger" -Actual ([string] $jobState.triggerType) -Expected "Schedule"
    Assert-ExpectedValue -Name "live cron expression" -Actual ([string] $jobState.cron) -Expected $expectedCronExpression

    $previousImage = [string] $jobState.image
    if ([string]::IsNullOrWhiteSpace($previousImage)) {
        throw "Could not determine the current production image for rollback."
    }

    if ($FreshHistoricalRun) {
        $stage = "validating fresh historical-run prerequisites"
        Assert-NoRunningExecution `
            -ResourceGroup $expectedResourceGroup `
            -JobName $expectedJobName `
            -Subscription $subscriptionText

        $jobEnvironmentBefore = @(Get-JobEnvironment `
            -ResourceGroup $expectedResourceGroup `
            -JobName $expectedJobName `
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
        Assert-ExpectedValue -Name "live storage account" -Actual $liveStorageAccount -Expected $expectedStorageAccountName
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

        $storageAccountId = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "storage", "account", "show",
            "--subscription", $subscriptionText,
            "--resource-group", $expectedResourceGroup,
            "--name", $expectedStorageAccountName,
            "--query", "id",
            "--output", "tsv",
            "--only-show-errors"
        )) -join "").Trim()
        if ([string]::IsNullOrWhiteSpace($storageAccountId)) {
            throw "Could not determine the production storage account resource ID."
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
            "--resource-group", $expectedResourceGroup,
            "--storage-account", $expectedStorageAccountName,
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
    $expectedImage = "${expectedAcrName}.azurecr.io/${expectedImageName}:$imageTag"

    $existingTag = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "acr", "repository", "show-tags",
        "--name", $expectedAcrName,
        "--repository", $expectedImageName,
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
            "--name", $expectedAcrName,
            "--image", "${expectedImageName}:$imageTag",
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
        if ($DeliveryMode -eq "PrebuiltImage") {
            Write-Host "Published image: $publishedImage"
        } else {
            Write-Host "Published binary: $artifactUri"
            Write-Host "Selected base image: $BaseImage"
        }
        Write-Host "Planned image: $expectedImage"
        if ($FreshHistoricalRun) {
            Write-Host "Planned historical start: $historicalStartText"
            Write-Host "Planned new Blob container: https://$expectedStorageAccountName.blob.core.windows.net/$freshContainerName"
            Write-Host "Planned export path in new container: $freshExportRoot"
            Write-Host "Planned state path in new container: $freshStateRoot"
            Write-Host "Existing container '$liveStorageContainer' will not be modified or deleted."
        }
        Write-Host "Run again with -Deploy to import or assemble the tested release and update only the production job image."
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
                    "--resource-group", $expectedResourceGroup,
                    "--name", $expectedAcrName,
                    "--source", $publishedImage,
                    "--image", "${expectedImageName}:$imageTag",
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
                    "--resource-group", $expectedResourceGroup,
                    "--registry", $expectedAcrName,
                    "--image", "${expectedImageName}:$imageTag",
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
            "--name", $expectedAcrName,
            "--image", "${expectedImageName}:$imageTag",
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

        $stage = "updating only the production job image"
        Invoke-CheckedCommand -Command "az" -ArgumentList @(
            "containerapp", "job", "update",
            "--subscription", $subscriptionText,
            "--resource-group", $expectedResourceGroup,
            "--name", $expectedJobName,
            "--image", $expectedImage,
            "--output", "none",
            "--only-show-errors"
        )

        $stage = "verifying the deployed image"
        $deployedState = ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "containerapp", "job", "show",
            "--resource-group", $expectedResourceGroup,
            "--name", $expectedJobName,
            "--query", "{triggerType:configuration.triggerType,cron:configuration.scheduleTriggerConfig.cronExpression,image:template.containers[0].image}",
            "--output", "json",
            "--only-show-errors"
        ))
        Assert-ExpectedValue -Name "deployed image" -Actual ([string] $deployedState.image) -Expected $expectedImage
        Assert-ExpectedValue -Name "post-deployment trigger" -Actual ([string] $deployedState.triggerType) -Expected "Schedule"
        Assert-ExpectedValue -Name "post-deployment cron expression" -Actual ([string] $deployedState.cron) -Expected $expectedCronExpression

        $historicalExecutionStarted = $false
        if ($FreshHistoricalRun) {
            $stage = "checking for active executions before the fresh historical update"
            Assert-NoRunningExecution `
                -ResourceGroup $expectedResourceGroup `
                -JobName $expectedJobName `
                -Subscription $subscriptionText

            $stage = "creating the fresh Blob container"
            Invoke-CheckedCommand -Command "az" -ArgumentList @(
                "storage", "container-rm", "create",
                "--subscription", $subscriptionText,
                "--resource-group", $expectedResourceGroup,
                "--storage-account", $expectedStorageAccountName,
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
                "--resource-group", $expectedResourceGroup,
                "--storage-account", $expectedStorageAccountName,
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
                "--resource-group", $expectedResourceGroup,
                "--name", $expectedJobName,
                "--set-env-vars"
            )
            foreach ($entry in $freshEnvironmentValues.GetEnumerator()) {
                $freshEnvironmentArguments += "$($entry.Key)=$($entry.Value)"
            }
            $freshEnvironmentArguments += @("--output", "none", "--only-show-errors")
            Invoke-CheckedCommand -Command "az" -ArgumentList $freshEnvironmentArguments

            $stage = "verifying the fresh historical configuration"
            $jobEnvironmentAfter = @(Get-JobEnvironment `
                -ResourceGroup $expectedResourceGroup `
                -JobName $expectedJobName `
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
                    -ResourceGroup $expectedResourceGroup `
                    -JobName $expectedJobName `
                    -Subscription $subscriptionText

                $stage = "starting the fresh historical run"
                Invoke-CheckedCommand -Command "az" -ArgumentList @(
                    "containerapp", "job", "start",
                    "--subscription", $subscriptionText,
                    "--resource-group", $expectedResourceGroup,
                    "--name", $expectedJobName,
                    "--output", "none",
                    "--only-show-errors"
                )
                $historicalExecutionStarted = $true
            }
        }

        Write-Host ""
        Write-Host "SUCCESS: the tested release image was deployed to the production job."
        Write-Host "Release: $releaseTag"
        Write-Host "Commit: $canonicalCommit"
        Write-Host "Delivery mode: $DeliveryMode"
        Write-Host "Image: $expectedImage"
        Write-Host "Digest: $imageDigest"
        Write-Host "Previous image: $previousImage"
        Write-Host "Schedule: $expectedCronExpression UTC"
        if ($FreshHistoricalRun) {
            Write-Host "Historical start: $historicalStartText"
            Write-Host "New Blob container: https://$expectedStorageAccountName.blob.core.windows.net/$freshContainerName"
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
