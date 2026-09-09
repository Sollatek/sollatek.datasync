<#
.SYNOPSIS
Validates or deploys the latest canonical DataSync master commit to the production daily job.

.DESCRIPTION
Fetches github-work/master, verifies the exact Sollatek GitHub repository and the async-export
recovery fix, tests the commit in an isolated worktree, validates the exact Azure production target,
and prepares an immutable image tag. Add -Deploy to publish the image and update only the existing
Container Apps Job image. The script never starts a job execution or reads or replaces job secrets.

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -TenantId <tenant-guid> -SubscriptionId <subscription-guid>

.EXAMPLE
.\Invoke-DataSyncProductionRelease.ps1 -TenantId <tenant-guid> -SubscriptionId <subscription-guid> -Deploy
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Guid] $TenantId,

    [Parameter(Mandatory = $true)]
    [Guid] $SubscriptionId,

    [switch] $Deploy
)

$ErrorActionPreference = "Stop"

$expectedResourceGroup = "rg-sollatek-datasync-prod"
$expectedLocation = "westeurope"
$expectedAcrName = "acrsollatekdatasync001"
$expectedImageName = "sollatek-datasync"
$expectedJobName = "sollatek-datasync-daily"
$expectedCronExpression = "0 1 * * *"
$requiredFixCommit = "25a04bc5cd886fb3465c867dba6b45538db0cb19"
$canonicalRemote = "github-work"
$expectedCanonicalRemoteUrl = "git@github-work:Sollatek/sollatek.datasync.git"
$canonicalBranch = "master"

$stage = "initialization"
$exitCode = 1
$releaseWorktree = $null
$releaseTemp = $null

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

try {
    $stage = "preflight"
    foreach ($commandName in @("git", "dotnet", "az")) {
        if ($null -eq (Get-Command $commandName -ErrorAction SilentlyContinue)) {
            throw "Required command is not available: $commandName"
        }
    }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    $hostRoot = Split-Path -Parent $repoRoot
    $worktreeRoot = Join-Path $hostRoot ".worktrees"
    $sourceConfigPath = Join-Path $repoRoot "deployment\azure\deploy.daily.azure-containerapps-job.json"

    if (-not (Test-Path -LiteralPath $sourceConfigPath -PathType Leaf)) {
        throw "Deployment config was not found: $sourceConfigPath"
    }

    $stage = "validating canonical GitHub remote"
    $canonicalRemoteUrl = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $repoRoot, "remote", "get-url", $canonicalRemote
    )) -join "").Trim()
    Assert-ExpectedValue -Name "canonical GitHub remote" -Actual $canonicalRemoteUrl -Expected $expectedCanonicalRemoteUrl

    $stage = "fetching canonical GitHub branch"
    $canonicalRef = "refs/remotes/$canonicalRemote/$canonicalBranch"
    $canonicalRefspec = "${canonicalBranch}:$canonicalRef"

    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "-C", $repoRoot, "fetch", $canonicalRemote, $canonicalRefspec
    )

    $canonicalCommit = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $repoRoot, "rev-parse", $canonicalRef
    )) -join "").Trim()

    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "-C", $repoRoot, "merge-base", "--is-ancestor", $requiredFixCommit, $canonicalCommit
    )

    $stage = "creating isolated release worktree"
    if (-not (Test-Path -LiteralPath $worktreeRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $worktreeRoot | Out-Null
    }

    $shortCommit = $canonicalCommit.Substring(0, 12)
    $releaseStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
    $releaseWorktree = Join-Path $worktreeRoot "datasync-production-$shortCommit-$releaseStamp"
    $releaseWorktree = Assert-ChildPath -Path $releaseWorktree -ParentPath $worktreeRoot

    if (Test-Path -LiteralPath $releaseWorktree) {
        throw "Release worktree already exists: $releaseWorktree"
    }

    Invoke-CheckedCommand -Command "git" -ArgumentList @(
        "-C", $repoRoot, "worktree", "add", "--detach", $releaseWorktree, $canonicalCommit
    )

    $checkedOutCommit = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $releaseWorktree, "rev-parse", "HEAD"
    )) -join "").Trim()
    Assert-ExpectedValue -Name "release commit" -Actual $checkedOutCommit -Expected $canonicalCommit

    $worktreeStatus = ((Invoke-CheckedCommand -Command "git" -CaptureOutput -ArgumentList @(
        "-C", $releaseWorktree, "status", "--porcelain"
    )) -join [Environment]::NewLine).Trim()
    if (-not [string]::IsNullOrWhiteSpace($worktreeStatus)) {
        throw "The isolated release worktree is not clean."
    }

    $stage = "running DataSync release tests"
    Invoke-CheckedCommand -Command "dotnet" -ArgumentList @(
        "test",
        (Join-Path $releaseWorktree "Sollatek.DataSync.sln"),
        "-c", "Release",
        "--disable-build-servers",
        "-m:1",
        "--verbosity", "minimal"
    )

    $stage = "validating deployment definition"
    $releaseSourceConfigPath = Join-Path $releaseWorktree "deployment\azure\deploy.daily.azure-containerapps-job.json"
    $config = Get-Content -LiteralPath $releaseSourceConfigPath -Raw | ConvertFrom-Json

    Assert-ExpectedValue -Name "resource group" -Actual ([string] $config.resourceGroup) -Expected $expectedResourceGroup
    Assert-ExpectedValue -Name "region" -Actual ([string] $config.location) -Expected $expectedLocation
    Assert-ExpectedValue -Name "registry" -Actual ([string] $config.containerRegistry.name) -Expected $expectedAcrName
    Assert-ExpectedValue -Name "image name" -Actual ([string] $config.image.name) -Expected $expectedImageName
    Assert-ExpectedValue -Name "job name" -Actual ([string] $config.containerApps.jobName) -Expected $expectedJobName
    Assert-ExpectedValue -Name "cron expression" -Actual ([string] $config.containerApps.cronExpression) -Expected $expectedCronExpression

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
        "--query", "{triggerType:configuration.triggerType,cron:configuration.scheduleTriggerConfig.cronExpression,image:template.containers[0].image}",
        "--output", "json",
        "--only-show-errors"
    ))
    Assert-ExpectedValue -Name "job trigger" -Actual ([string] $jobState.triggerType) -Expected "Schedule"
    Assert-ExpectedValue -Name "live cron expression" -Actual ([string] $jobState.cron) -Expected $expectedCronExpression

    $previousImage = [string] $jobState.image
    if ([string]::IsNullOrWhiteSpace($previousImage)) {
        throw "Could not determine the current production image for rollback."
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

    $releaseTemp = Join-Path ([System.IO.Path]::GetTempPath()) (
        "datasync-production-release-" + [Guid]::NewGuid().ToString("N")
    )
    New-Item -ItemType Directory -Path $releaseTemp | Out-Null
    $releaseTemp = Assert-ChildPath -Path $releaseTemp -ParentPath ([System.IO.Path]::GetTempPath())
    $releaseConfigPath = Join-Path $releaseTemp "deploy.daily.azure-containerapps-job.json"

    $config.subscription = $subscriptionText
    $config.image.tag = $imageTag
    $config.containerApps.updateExistingJob = $false
    $config.containerApps.runNow = $false
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $releaseConfigPath -Encoding utf8

    $deployScript = Join-Path $releaseWorktree "deployment\azure\deploy-containerapps-job.ps1"
    & $deployScript -ConfigPath $releaseConfigPath -ImageOnly -PlanOnly

    if (-not $Deploy) {
        Write-Host ""
        Write-Host "SUCCESS: validation and release tests passed. Nothing was deployed."
        Write-Host "Commit: $canonicalCommit"
        Write-Host "Planned image: $expectedImage"
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

        Write-Host ""
        Write-Host "SUCCESS: production job image was published and deployed."
        Write-Host "Commit: $canonicalCommit"
        Write-Host "Image: $expectedImage"
        Write-Host "Digest: $imageDigest"
        Write-Host "Previous image: $previousImage"
        Write-Host "Schedule: $expectedCronExpression UTC"
        Write-Host "The job was not started manually; the next scheduled execution will use the new image."
        $exitCode = 0
    }
} catch {
    Write-Error "FAILED during '$stage': $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
} finally {
    if (-not [string]::IsNullOrWhiteSpace($releaseWorktree) -and (Test-Path -LiteralPath $releaseWorktree)) {
        try {
            $releaseWorktree = Assert-ChildPath -Path $releaseWorktree -ParentPath $worktreeRoot
            Invoke-CheckedCommand -Command "git" -ArgumentList @(
                "-C", $repoRoot, "worktree", "remove", $releaseWorktree
            )
        } catch {
            Write-Error "Cleanup failed for release worktree '$releaseWorktree': $($_.Exception.Message)" -ErrorAction Continue
            $exitCode = 1
        }
    }

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
