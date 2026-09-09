<#
.SYNOPSIS
Validates or deploys the latest canonical DataSync master commit and can start an isolated historical run.

.DESCRIPTION
Clones the public Sollatek DataSync master branch from GitHub over HTTPS, verifies the async-export
recovery fix and the Keycloak realm token endpoint change, tests the commit in an isolated temporary
checkout, validates the exact Azure production target, and prepares an immutable image tag. Add
-Deploy to publish the image and update the existing Container Apps Job image.

Add -FreshHistoricalRun to plan or create a new private, timestamped Blob container for both exports
and state. Add -StartHistoricalRun with -Deploy and -FreshHistoricalRun to start the first execution.
The existing Blob container is never deleted or modified, and existing job secret references are
verified before and after the configuration update without reading their values.

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -TenantId <tenant-guid> -SubscriptionId <subscription-guid>

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -TenantId <tenant-guid> -SubscriptionId <subscription-guid> -Deploy

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -TenantId <tenant-guid> -SubscriptionId <subscription-guid> -FreshHistoricalRun

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -TenantId <tenant-guid> -SubscriptionId <subscription-guid> -Deploy -FreshHistoricalRun -StartHistoricalRun -HistoricalStartFrom 2025-01-01 -FreshRunLabel eccbc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Guid] $TenantId,

    [Parameter(Mandatory = $true)]
    [Guid] $SubscriptionId,

    [switch] $Deploy,

    [switch] $FreshHistoricalRun,

    [switch] $StartHistoricalRun,

    [DateTimeOffset] $HistoricalStartFrom = [DateTimeOffset]::Parse("2025-01-01T00:00:00Z"),

    [ValidatePattern("^(?!.*--)[a-z0-9](?:[a-z0-9-]{0,18}[a-z0-9])?$")]
    [string] $FreshRunLabel = "eccbc"
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
$requiredFixCommit = "25a04bc5cd886fb3465c867dba6b45538db0cb19"
$requiredKeycloakCommit = "3d54f9c3e9f1c930df7581bb03c08e690097aae5"
$requiredTokenEndpointPath = "/realms/platform/protocol/openid-connect/token"
$canonicalRepositoryUrl = "https://github.com/Sollatek/sollatek.datasync.git"
$canonicalBranch = "master"

$stage = "initialization"
$exitCode = 1
$releaseCheckout = $null
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
    foreach ($commandName in @("git", "dotnet", "az")) {
        if ($null -eq (Get-Command $commandName -ErrorAction SilentlyContinue)) {
            throw "Required command is not available: $commandName"
        }
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

    $releaseTemp = Join-Path ([System.IO.Path]::GetTempPath()) (
        "datasync-production-release-" + [Guid]::NewGuid().ToString("N")
    )
    New-Item -ItemType Directory -Path $releaseTemp | Out-Null
    $releaseTemp = Assert-ChildPath -Path $releaseTemp -ParentPath ([System.IO.Path]::GetTempPath())
    $releaseCheckout = Join-Path $releaseTemp "repository"
    $releaseCheckout = Assert-ChildPath -Path $releaseCheckout -ParentPath $releaseTemp

    $stage = "cloning the canonical public GitHub branch"
    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "clone",
        "--branch", $canonicalBranch,
        "--single-branch",
        "--no-checkout",
        $canonicalRepositoryUrl,
        $releaseCheckout
    )

    $canonicalRemoteUrl = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $releaseCheckout, "remote", "get-url", "origin"
    )) -join "").Trim()
    Assert-ExpectedValue -Name "canonical GitHub repository" -Actual $canonicalRemoteUrl -Expected $canonicalRepositoryUrl

    $canonicalRef = "refs/remotes/origin/$canonicalBranch"

    $canonicalCommit = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $releaseCheckout, "rev-parse", $canonicalRef
    )) -join "").Trim()

    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "-C", $releaseCheckout, "merge-base", "--is-ancestor", $requiredFixCommit, $canonicalCommit
    )
    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "-C", $releaseCheckout, "merge-base", "--is-ancestor", $requiredKeycloakCommit, $canonicalCommit
    )

    $shortCommit = $canonicalCommit.Substring(0, 12)
    $releaseStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
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
    $stage = "checking out the canonical GitHub commit"
    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "-C", $releaseCheckout, "checkout", "--detach", $canonicalCommit
    )

    $checkedOutCommit = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $releaseCheckout, "rev-parse", "HEAD"
    )) -join "").Trim()
    Assert-ExpectedValue -Name "release commit" -Actual $checkedOutCommit -Expected $canonicalCommit

    $worktreeStatus = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $releaseCheckout, "status", "--porcelain"
    )) -join [Environment]::NewLine).Trim()
    if (-not [string]::IsNullOrWhiteSpace($worktreeStatus)) {
        throw "The isolated release worktree is not clean."
    }

    $stage = "running DataSync release tests"
    Invoke-CheckedCommand -Command "dotnet" -ArgumentList @(
        "test",
        (Join-Path $releaseCheckout "Sollatek.DataSync.sln"),
        "-c", "Release",
        "--disable-build-servers",
        "-m:1",
        "--verbosity", "minimal"
    )

    $stage = "validating deployment definition"
    $releaseSourceConfigPath = Join-Path $releaseCheckout "deployment\azure\deploy.daily.azure-containerapps-job.json"
    $config = Get-Content -LiteralPath $releaseSourceConfigPath -Raw | ConvertFrom-Json

    Assert-ExpectedValue -Name "resource group" -Actual ([string] $config.resourceGroup) -Expected $expectedResourceGroup
    Assert-ExpectedValue -Name "region" -Actual ([string] $config.location) -Expected $expectedLocation
    Assert-ExpectedValue -Name "registry" -Actual ([string] $config.containerRegistry.name) -Expected $expectedAcrName
    Assert-ExpectedValue -Name "image name" -Actual ([string] $config.image.name) -Expected $expectedImageName
    Assert-ExpectedValue -Name "job name" -Actual ([string] $config.containerApps.jobName) -Expected $expectedJobName
    Assert-ExpectedValue -Name "storage account" -Actual ([string] $config.storage.accountName) -Expected $expectedStorageAccountName
    Assert-ExpectedValue -Name "storage container" -Actual ([string] $config.storage.containerName) -Expected $expectedStorageContainerName
    Assert-ExpectedValue -Name "cron expression" -Actual ([string] $config.containerApps.cronExpression) -Expected $expectedCronExpression
    Assert-ExpectedValue -Name "Keycloak token endpoint" -Actual ([string] $config.appsettings.Settings.oauthTokenEndpointPath) -Expected $requiredTokenEndpointPath

    $stage = "authenticating to the production Azure target"
    $tenantText = $TenantId.ToString()
    $subscriptionText = $SubscriptionId.ToString()

    Invoke-CheckedCommand -Command "az" -ArgumentList @(
        "login", "--tenant", $tenantText, "--output", "none"
    )
    Invoke-CheckedCommand -Command "az" -ArgumentList @(
        "account", "set", "--subscription", $subscriptionText
    )

    $account = ConvertFrom-CommandJson -Output @(Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
        "account", "show",
        "--query", "{tenantId:tenantId,id:id,name:name}",
        "--output", "json",
        "--only-show-errors"
    ))
    Assert-ExpectedValue -Name "Azure tenant" -Actual ([string] $account.tenantId) -Expected $tenantText
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

    $imageTag = "$releaseStamp-$shortCommit"
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
        throw "The immutable image tag already exists: $imageTag"
    }

    $releaseConfigPath = Join-Path $releaseTemp "deploy.daily.azure-containerapps-job.json"

    $config.subscription = $subscriptionText
    $config.image.tag = $imageTag
    $config.containerApps.updateExistingJob = $false
    $config.containerApps.runNow = $false
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $releaseConfigPath -Encoding utf8

    $deployScript = Join-Path $releaseCheckout "deployment\azure\deploy-containerapps-job.ps1"
    & $deployScript -ConfigPath $releaseConfigPath -ImageOnly -PlanOnly

    if (-not $Deploy) {
        Write-Host ""
        Write-Host "SUCCESS: validation and release tests passed. Nothing was deployed."
        Write-Host "Commit: $canonicalCommit"
        Write-Host "Planned image: $expectedImage"
        if ($FreshHistoricalRun) {
            Write-Host "Planned historical start: $historicalStartText"
            Write-Host "Planned new Blob container: https://$expectedStorageAccountName.blob.core.windows.net/$freshContainerName"
            Write-Host "Planned export path in new container: $freshExportRoot"
            Write-Host "Planned state path in new container: $freshStateRoot"
            Write-Host "Existing container '$liveStorageContainer' will not be modified or deleted."
        }
        Write-Host "Run again with -Deploy to publish and update the production job image."
        $exitCode = 0
    } else {
        $stage = "publishing and deploying the image"
        & $deployScript -ConfigPath $releaseConfigPath -ImageOnly

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

        $imageDigest = ((Invoke-CheckedCommand -Command "az" -CaptureOutput -ArgumentList @(
            "acr", "repository", "show",
            "--name", $expectedAcrName,
            "--image", "${expectedImageName}:$imageTag",
            "--query", "digest",
            "--output", "tsv",
            "--only-show-errors"
        )) -join "").Trim()
        if ([string]::IsNullOrWhiteSpace($imageDigest)) {
            throw "The deployed image exists, but its ACR digest could not be verified."
        }

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
        Write-Host "SUCCESS: production job image was published and deployed."
        Write-Host "Commit: $canonicalCommit"
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
