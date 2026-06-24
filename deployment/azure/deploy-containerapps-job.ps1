[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConfigPath,

    [switch] $SkipBuild,

    [switch] $PlanOnly,

    [switch] $NoCleanup
)

$ErrorActionPreference = "Stop"

function Get-ScriptRoot {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return $PSScriptRoot
    }

    Split-Path -Parent $MyInvocation.ScriptName
}

function Join-RelativePath([string] $Root, [string] $RelativePath) {
    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Path must be relative: $RelativePath"
    }

    $path = $Root
    foreach ($part in ($RelativePath -split '[\\/]+')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -eq ".") {
            continue
        }

        if ($part -eq "..") {
            throw "Relative path must not traverse outside its root: $RelativePath"
        }

        $path = Join-Path $path $part
    }

    $path
}

function Convert-ToAcrRelativePath([string] $RelativePath) {
    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "ACR Dockerfile path must be relative to the build context: $RelativePath"
    }

    if ($RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "ACR Dockerfile path must not traverse outside the build context: $RelativePath"
    }

    ($RelativePath -replace '\\', '/').TrimStart('/')
}

function Get-RepoRoot {
    $scriptRoot = Get-ScriptRoot
    $current = Resolve-Path $scriptRoot

    while ($null -ne $current) {
        $candidate = $current.Path
        if ((Test-Path -LiteralPath (Join-Path $candidate "Sollatek.DataSync.sln")) -and
            (Test-Path -LiteralPath (Join-RelativePath $candidate "Sollatek.DataSync/Dockerfile"))) {
            return $current
        }

        $parent = Split-Path -Parent $candidate
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $candidate) {
            break
        }

        $current = Resolve-Path $parent
    }

    throw "Could not locate the DataSync repository root from script path '$scriptRoot'."
}

function Read-DeploymentConfig([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Config file not found: $Path"
    }

    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Has-Property($Object, [string] $Name) {
    $null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]
}

function Get-RequiredString($Object, [string] $Name, [string] $Path) {
    if (-not (Has-Property $Object $Name)) {
        throw "Missing required configuration value: $Path.$Name"
    }

    $value = [string] $Object.$Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Configuration value cannot be empty: $Path.$Name"
    }

    $value
}

function Get-OptionalValue($Object, [string] $Name, $DefaultValue) {
    if (Has-Property $Object $Name) {
        return $Object.$Name
    }

    $DefaultValue
}

function Get-OptionalString($Object, [string] $Name, [string] $DefaultValue) {
    [string] (Get-OptionalValue $Object $Name $DefaultValue)
}

function Get-OptionalInt($Object, [string] $Name, [int] $DefaultValue) {
    [int] (Get-OptionalValue $Object $Name $DefaultValue)
}

function Get-OptionalBool($Object, [string] $Name, [bool] $DefaultValue) {
    [bool] (Get-OptionalValue $Object $Name $DefaultValue)
}

function Get-TagArgs($Tags) {
    $args = @()
    if ($null -eq $Tags) {
        return $args
    }

    foreach ($property in $Tags.PSObject.Properties) {
        if ($null -ne $property.Value -and -not [string]::IsNullOrWhiteSpace([string] $property.Value)) {
            $args += "$($property.Name)=$($property.Value)"
        }
    }

    if ($args.Count -gt 0) {
        @("--tags") + $args
    } else {
        @()
    }
}

function Resolve-AzCliCommand {
    if (-not [string]::IsNullOrWhiteSpace($script:AzCliExecutable)) {
        return
    }

    $azCommand = Get-Command az -ErrorAction Stop
    $azPath = if (-not [string]::IsNullOrWhiteSpace($azCommand.Source)) {
        $azCommand.Source
    } else {
        $azCommand.Definition
    }

    $script:AzCliExecutable = $azPath
    $script:AzCliPrefixArguments = @()

    $isWindowsHost = [string]::Equals($env:OS, "Windows_NT", [StringComparison]::OrdinalIgnoreCase)
    if (-not $isWindowsHost -or [string]::IsNullOrWhiteSpace($azPath) -or -not $azPath.EndsWith(".cmd", [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $cliRoot = Split-Path -Parent (Split-Path -Parent $azPath)
    $pythonPath = Join-Path $cliRoot "python.exe"
    if (Test-Path -LiteralPath $pythonPath -PathType Leaf) {
        $script:AzCliExecutable = $pythonPath
        $script:AzCliPrefixArguments = @("-m", "azure.cli")
    }
}

function Invoke-AzCli([string[]] $Arguments, [switch] $Sensitive) {
    Resolve-AzCliCommand
    $effectiveArguments = @($Arguments)
    if ($effectiveArguments -notcontains "--only-show-errors") {
        $effectiveArguments += "--only-show-errors"
    }

    $display = if ($Sensitive) {
        "az <sensitive arguments omitted>"
    } else {
        "az " + ($effectiveArguments -join " ")
    }

    Write-Host ">> $display"
    $processArguments = @($script:AzCliPrefixArguments) + $effectiveArguments
    $output = & $script:AzCliExecutable @processArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $display`n$output"
    }

    ($output -join [Environment]::NewLine)
}

function Invoke-AzCliTsv([string[]] $Arguments, [switch] $Sensitive) {
    (Invoke-AzCli -Arguments ($Arguments + @("--output", "tsv")) -Sensitive:$Sensitive).Trim()
}

function Test-AzResource([string[]] $Arguments) {
    Resolve-AzCliCommand
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $processArguments = @($script:AzCliPrefixArguments) + $Arguments + @("--only-show-errors")
        & $script:AzCliExecutable @processArguments 1>$null 2>$null
        return $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Test-AcrImageTag([string] $AcrName, [string] $ImageName, [string] $ImageTag) {
    Test-AzResource -Arguments @(
        "acr", "repository", "show",
        "--name", $AcrName,
        "--image", "${ImageName}:$ImageTag"
    )
}

function Remove-DirectoryWithRetry([string] $Path, [int] $Attempts = 6, [int] $DelaySeconds = 5) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq $Attempts) {
                Write-Warning "Could not remove temporary directory '$Path': $($_.Exception.Message)"
                return
            }

            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

function Resolve-SecretValue($SecretConfig, [string] $SecretName) {
    if ($SecretName.Length -gt 20) {
        throw "Container Apps Job secret name '$SecretName' is longer than 20 characters."
    }

    if ($SecretConfig -is [string]) {
        return $SecretConfig
    }

    if (Has-Property $SecretConfig "value") {
        return [string] $SecretConfig.value
    }

    if (Has-Property $SecretConfig "environmentVariable") {
        $envName = [string] $SecretConfig.environmentVariable
        $value = [Environment]::GetEnvironmentVariable($envName, "Process")
        if ([string]::IsNullOrEmpty($value)) {
            $value = [Environment]::GetEnvironmentVariable($envName, "User")
        }
        if ([string]::IsNullOrEmpty($value)) {
            $value = [Environment]::GetEnvironmentVariable($envName, "Machine")
        }
        if ([string]::IsNullOrEmpty($value)) {
            throw "Environment variable '$envName' for secret '$SecretName' is not set."
        }
        return $value
    }

    if (Has-Property $SecretConfig "keyVaultUrl") {
        $identity = Get-OptionalString $SecretConfig "identity" "system"
        return "keyvaultref:$($SecretConfig.keyVaultUrl),identityref:$identity"
    }

    throw "Secret '$SecretName' must define value, environmentVariable, or keyVaultUrl."
}

function Get-EnvironmentVariableArg($Value) {
    if ($Value -is [string]) {
        return $Value
    }

    if ($Value -is [datetime]) {
        return $Value.ToUniversalTime().ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    }

    if ($Value -is [datetimeoffset]) {
        return $Value.ToUniversalTime().ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    }

    if (Has-Property $Value "secretRef") {
        return "secretref:$($Value.secretRef)"
    }

    [string] $Value
}

function New-BuildContext($Config, [string] $RepoRoot, [string] $JobName) {
    if (-not (Has-Property $Config "appsettings")) {
        return [pscustomobject]@{
            Path = $RepoRoot
            IsTemporary = $false
        }
    }

    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "sollatek-datasync-deploy"
    $target = Join-Path $tempRoot "$JobName-$stamp"
    $resolvedRoot = (Resolve-Path $RepoRoot).Path
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $resolvedParent = (Resolve-Path $tempRoot).Path

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $resolvedTarget = (Resolve-Path $target).Path
    if (-not $resolvedTarget.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Temporary build context is outside deployment temp root: $resolvedTarget"
    }

    Write-Host "Preparing temporary build context: $resolvedTarget"
    $excludedDirectories = @(".git", ".artifacts", "bin", "obj", ".vs", ".idea", "TestResults", "BenchmarkDotNet.Artifacts")
    $excludedFiles = @("*.user", "*.suo")

    Get-ChildItem -LiteralPath $resolvedRoot -Force |
        Where-Object { $excludedDirectories -notcontains $_.Name } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $resolvedTarget -Recurse -Force -Exclude $excludedFiles
        }

    Get-ChildItem -LiteralPath $resolvedTarget -Directory -Recurse -Force |
        Where-Object { $excludedDirectories -contains $_.Name } |
        Sort-Object FullName -Descending |
        ForEach-Object {
            $resolvedChild = (Resolve-Path $_.FullName).Path
            if (-not $resolvedChild.StartsWith($resolvedTarget, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove directory outside build context: $resolvedChild"
            }
            Remove-Item -LiteralPath $resolvedChild -Recurse -Force
        }

    Get-ChildItem -LiteralPath $resolvedTarget -File -Recurse -Force |
        Where-Object {
            $name = $_.Name
            $excludedFiles | Where-Object { $name -like $_ }
        } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
        }

    $appsettingsPath = Join-RelativePath $resolvedTarget "Sollatek.DataSync/appsettings.json"
    if (-not (Test-Path -LiteralPath $appsettingsPath)) {
        throw "Could not find appsettings.json in temporary build context: $appsettingsPath"
    }

    $Config.appsettings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $appsettingsPath -Encoding UTF8

    [pscustomobject]@{
        Path = $resolvedTarget
        IsTemporary = $true
    }
}

function Resolve-AcrDockerfile([object] $BuildContext, [string] $Dockerfile) {
    $dockerfileInContext = Join-RelativePath $BuildContext.Path $Dockerfile
    if (-not (Test-Path -LiteralPath $dockerfileInContext -PathType Leaf)) {
        throw "Dockerfile '$Dockerfile' was not found in build context '$($BuildContext.Path)'. Check image.dockerfile and the repository layout."
    }

    if (-not $BuildContext.IsTemporary) {
        return $Dockerfile
    }

    $acrDockerfile = "Dockerfile.acr"
    Copy-Item -LiteralPath $dockerfileInContext -Destination (Join-Path $BuildContext.Path $acrDockerfile) -Force
    $acrDockerfile
}

function Ensure-RoleAssignment([string] $PrincipalId, [string] $Role, [string] $Scope) {
    $existing = Invoke-AzCliTsv -Arguments @(
        "role", "assignment", "list",
        "--assignee", $PrincipalId,
        "--role", $Role,
        "--scope", $Scope,
        "--query", "[].id"
    )

    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        Write-Host "Role assignment already exists: $Role on $Scope"
        return
    }

    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            Invoke-AzCli -Arguments @(
                "role", "assignment", "create",
                "--assignee-object-id", $PrincipalId,
                "--assignee-principal-type", "ServicePrincipal",
                "--role", $Role,
                "--scope", $Scope
            ) | Out-Null
            return
        } catch {
            if ($attempt -eq 6) {
                throw
            }
            Write-Host "Role assignment failed, retrying after identity propagation ($attempt/6): $Role"
            Start-Sleep -Seconds 10
        }
    }
}

function Get-JobPrincipalId([string] $ResourceGroup, [string] $JobName) {
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $principalId = Invoke-AzCliTsv -Arguments @(
            "containerapp", "job", "show",
            "--resource-group", $ResourceGroup,
            "--name", $JobName,
            "--query", "identity.principalId"
        )

        if (-not [string]::IsNullOrWhiteSpace($principalId)) {
            return $principalId
        }

        Write-Host "Waiting for job system identity principal id ($attempt/12)..."
        Start-Sleep -Seconds 5
    }

    throw "Container Apps Job system identity did not produce a principal id."
}

function Get-NetworkOptions($Config, [string] $StorageAccount) {
    if (-not (Has-Property $Config "network")) {
        return [pscustomobject]@{
            Create = $false
            SkipSetup = $true
        }
    }

    $network = $Config.network
    $create = if (Has-Property $network "skipSetup") {
        -not (Get-OptionalBool $network "skipSetup" $true)
    } else {
        Get-OptionalBool $network "create" $false
    }

    if (-not $create) {
        return [pscustomobject]@{
            Create = $false
            SkipSetup = $true
        }
    }

    $vnetName = Get-OptionalString $network "vnetName" "vnet-$StorageAccount"
    $containerAppsSubnetName = Get-OptionalString $network "containerAppsSubnetName" "snet-containerapps"

    [pscustomobject]@{
        Create = $true
        SkipSetup = $false
        VnetName = $vnetName
        AddressPrefix = Get-OptionalString $network "addressPrefix" "10.70.0.0/16"
        ContainerAppsSubnetName = $containerAppsSubnetName
        ContainerAppsSubnetPrefix = Get-OptionalString $network "containerAppsSubnetPrefix" "10.70.0.0/23"
        StorageServiceEndpoint = Get-OptionalString $network "storageServiceEndpoint" "Microsoft.Storage"
        ContainerAppsInternalOnly = Get-OptionalBool $network "containerAppsInternalOnly" $true
    }
}

function Ensure-VirtualNetwork($NetworkOptions, [string] $ResourceGroup, [string] $Location, $Tags) {
    if (-not $NetworkOptions.Create) {
        return $null
    }

    if (-not (Test-AzResource -Arguments @("network", "vnet", "show", "--resource-group", $ResourceGroup, "--name", $NetworkOptions.VnetName))) {
        Invoke-AzCli -Arguments (@(
            "network", "vnet", "create",
            "--resource-group", $ResourceGroup,
            "--name", $NetworkOptions.VnetName,
            "--location", $Location,
            "--address-prefixes", $NetworkOptions.AddressPrefix
        ) + (Get-TagArgs $Tags)) | Out-Null
    } else {
        Write-Host "Virtual network already exists: $($NetworkOptions.VnetName)"
    }

    Invoke-AzCliTsv -Arguments @(
        "network", "vnet", "show",
        "--resource-group", $ResourceGroup,
        "--name", $NetworkOptions.VnetName,
        "--query", "id"
    )
}

function Ensure-Subnet([string] $ResourceGroup, [string] $VnetName, [string] $SubnetName, [string] $AddressPrefix, [string] $Delegation) {
    $exists = Test-AzResource -Arguments @(
        "network", "vnet", "subnet", "show",
        "--resource-group", $ResourceGroup,
        "--vnet-name", $VnetName,
        "--name", $SubnetName
    )

    if (-not $exists) {
        $args = @(
            "network", "vnet", "subnet", "create",
            "--resource-group", $ResourceGroup,
            "--vnet-name", $VnetName,
            "--name", $SubnetName,
            "--address-prefixes", $AddressPrefix
        )

        if (-not [string]::IsNullOrWhiteSpace($Delegation)) {
            $args += @("--delegations", $Delegation)
        }

        Invoke-AzCli -Arguments $args | Out-Null
    } else {
        Write-Host "Subnet already exists: $SubnetName"
    }

    Invoke-AzCliTsv -Arguments @(
        "network", "vnet", "subnet", "show",
        "--resource-group", $ResourceGroup,
        "--vnet-name", $VnetName,
        "--name", $SubnetName,
        "--query", "id"
    )
}

function Ensure-SubnetServiceEndpoint([string] $ResourceGroup, [string] $VnetName, [string] $SubnetName, [string] $ServiceEndpoint) {
    if ([string]::IsNullOrWhiteSpace($ServiceEndpoint)) {
        return
    }

    $existing = Invoke-AzCliTsv -Arguments @(
        "network", "vnet", "subnet", "show",
        "--resource-group", $ResourceGroup,
        "--vnet-name", $VnetName,
        "--name", $SubnetName,
        "--query", "serviceEndpoints[].service"
    )

    $existingServices = @($existing -split "\s+" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($existingServices -contains $ServiceEndpoint) {
        return
    }

    $serviceEndpoints = @($existingServices + $ServiceEndpoint | Select-Object -Unique)
    $updateArgs = @(
        "network", "vnet", "subnet", "update",
        "--resource-group", $ResourceGroup,
        "--vnet-name", $VnetName,
        "--name", $SubnetName,
        "--service-endpoints"
    ) + $serviceEndpoints

    Invoke-AzCli -Arguments $updateArgs | Out-Null
}

function Ensure-StorageServiceEndpointRule($NetworkOptions, [string] $ResourceGroup, [string] $StorageAccount) {
    if (-not $NetworkOptions.Create) {
        return
    }

    Ensure-SubnetServiceEndpoint `
        -ResourceGroup $ResourceGroup `
        -VnetName $NetworkOptions.VnetName `
        -SubnetName $NetworkOptions.ContainerAppsSubnetName `
        -ServiceEndpoint $NetworkOptions.StorageServiceEndpoint

    $subnetId = Invoke-AzCliTsv -Arguments @(
        "network", "vnet", "subnet", "show",
        "--resource-group", $ResourceGroup,
        "--vnet-name", $NetworkOptions.VnetName,
        "--name", $NetworkOptions.ContainerAppsSubnetName,
        "--query", "id"
    )

    $existingRules = Invoke-AzCliTsv -Arguments @(
        "storage", "account", "show",
        "--resource-group", $ResourceGroup,
        "--name", $StorageAccount,
        "--query", "networkRuleSet.virtualNetworkRules[].virtualNetworkResourceId"
    )

    if (($existingRules -split "\s+") -contains $subnetId) {
        return
    }

    Invoke-AzCli -Arguments @(
        "storage", "account", "network-rule", "add",
        "--resource-group", $ResourceGroup,
        "--account-name", $StorageAccount,
        "--subnet", $subnetId
    ) | Out-Null
}

function Write-Plan($Config) {
    $acrName = Get-RequiredString $Config.containerRegistry "name" "containerRegistry"
    $storageName = Get-RequiredString $Config.storage "accountName" "storage"
    $containerName = Get-RequiredString $Config.storage "containerName" "storage"
    $skipContainerSetup = Get-OptionalBool $Config.storage "skipContainerSetup" $false
    $networkOptions = Get-NetworkOptions -Config $Config -StorageAccount $storageName
    $environmentName = Get-RequiredString $Config.containerApps "environmentName" "containerApps"
    $jobName = Get-RequiredString $Config.containerApps "jobName" "containerApps"
    $imageName = Get-RequiredString $Config.image "name" "image"
    $imageTag = Get-RequiredString $Config.image "tag" "image"
    $replicaTimeout = Get-OptionalInt $Config.containerApps "replicaTimeout" 82800

    Write-Host "Plan only. No Azure changes will be made."
    Write-Host "Subscription: $($Config.subscription)"
    Write-Host "Resource group: $($Config.resourceGroup)"
    Write-Host "Location: $($Config.location)"
    Write-Host "ACR: $acrName"
    Write-Host "Image: $acrName.azurecr.io/${imageName}:$imageTag"
    Write-Host "Storage account/container: $storageName/$containerName"
    Write-Host "Skip storage container setup: $skipContainerSetup"
    Write-Host "Network setup skipped: $($networkOptions.SkipSetup)"
    if ($networkOptions.Create) {
        Write-Host "Virtual network: $($networkOptions.VnetName) $($networkOptions.AddressPrefix)"
        Write-Host "Container Apps subnet: $($networkOptions.ContainerAppsSubnetName) $($networkOptions.ContainerAppsSubnetPrefix)"
        Write-Host "Storage service endpoint: $($networkOptions.StorageServiceEndpoint)"
        Write-Host "Storage network rule: Container Apps subnet"
        Write-Host "Container Apps internal only: $($networkOptions.ContainerAppsInternalOnly)"
    }
    Write-Host "Container Apps environment: $environmentName"
    Write-Host "Container Apps job: $jobName"
    Write-Host "Schedule: $($Config.containerApps.cronExpression)"
    Write-Host "Replica timeout: $replicaTimeout seconds"
    Write-Host "Update existing job: $(Get-OptionalBool $Config.containerApps 'updateExistingJob' $false)"
    $hasAppsettingsOverride = Has-Property $Config "appsettings"
    Write-Host "Build appsettings override: $hasAppsettingsOverride"
    Write-Host "Reuse existing image tag when present: $(-not $hasAppsettingsOverride)"
    if ($hasAppsettingsOverride) {
        Write-Host "Image build policy: rebuild because appsettings are baked into the image."
    }
}

$repoRoot = (Get-RepoRoot).Path
$config = Read-DeploymentConfig $ConfigPath

if ($PlanOnly) {
    Write-Plan $config
    return
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required. Install Azure CLI or run this script from Azure Cloud Shell."
}

$subscription = Get-RequiredString $config "subscription" "root"
$location = Get-RequiredString $config "location" "root"
$resourceGroup = Get-RequiredString $config "resourceGroup" "root"
$tags = Get-OptionalValue $config "tags" $null

$acrName = Get-RequiredString $config.containerRegistry "name" "containerRegistry"
$acrSku = Get-OptionalString $config.containerRegistry "sku" "Basic"

$storageAccount = Get-RequiredString $config.storage "accountName" "storage"
$storageContainer = Get-RequiredString $config.storage "containerName" "storage"
$storageSku = Get-OptionalString $config.storage "sku" "Standard_LRS"
$storageKind = Get-OptionalString $config.storage "kind" "StorageV2"
$storageContainerAuth = Get-OptionalString $config.storage "containerSetupAuth" "key"
$skipStorageContainerSetup = Get-OptionalBool $config.storage "skipContainerSetup" $false
$networkOptions = Get-NetworkOptions -Config $config -StorageAccount $storageAccount

$environmentName = Get-RequiredString $config.containerApps "environmentName" "containerApps"
$jobName = Get-RequiredString $config.containerApps "jobName" "containerApps"
$cronExpression = Get-RequiredString $config.containerApps "cronExpression" "containerApps"
$logsDestination = Get-OptionalString $config.containerApps "logsDestination" "none"
$cpu = [string] (Get-OptionalValue $config.containerApps "cpu" "1")
$memory = Get-OptionalString $config.containerApps "memory" "2Gi"
$replicaTimeout = Get-OptionalInt $config.containerApps "replicaTimeout" 82800
$replicaRetryLimit = Get-OptionalInt $config.containerApps "replicaRetryLimit" 0
$parallelism = Get-OptionalInt $config.containerApps "parallelism" 1
$replicaCompletionCount = Get-OptionalInt $config.containerApps "replicaCompletionCount" 1
$runNow = Get-OptionalBool $config.containerApps "runNow" $false
$updateExistingJob = Get-OptionalBool $config.containerApps "updateExistingJob" $false
$bootstrapImage = Get-OptionalString $config.containerApps "bootstrapImage" "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest"

$imageName = Get-RequiredString $config.image "name" "image"
$imageTag = Get-RequiredString $config.image "tag" "image"
$dockerfile = Get-OptionalString $config.image "dockerfile" "Sollatek.DataSync/Dockerfile"
$acrDockerfile = Convert-ToAcrRelativePath $dockerfile
$dataSyncProvider = Get-OptionalString $config.image "provider" "all"
$monitoringProvider = Get-OptionalString $config.image "monitoringProvider" "none"
$dotnetRuntimeImage = Get-OptionalString $config.image "dotnetRuntimeImage" ""
$image = "$acrName.azurecr.io/${imageName}:$imageTag"
$hasAppsettingsOverride = Has-Property $config "appsettings"

Invoke-AzCli -Arguments @("account", "set", "--subscription", $subscription) | Out-Null
Invoke-AzCli -Arguments @("extension", "add", "--name", "containerapp", "--upgrade", "--only-show-errors") | Out-Null

$groupExists = (Invoke-AzCliTsv -Arguments @("group", "exists", "--name", $resourceGroup)).ToLowerInvariant()
if ($groupExists -ne "true") {
    Invoke-AzCli -Arguments (@("group", "create", "--name", $resourceGroup, "--location", $location) + (Get-TagArgs $tags)) | Out-Null
} else {
    Write-Host "Resource group already exists: $resourceGroup"
}

$containerAppsSubnetId = $null
if ($networkOptions.Create) {
    Ensure-VirtualNetwork -NetworkOptions $networkOptions -ResourceGroup $resourceGroup -Location $location -Tags $tags | Out-Null
    $containerAppsSubnetId = Ensure-Subnet `
        -ResourceGroup $resourceGroup `
        -VnetName $networkOptions.VnetName `
        -SubnetName $networkOptions.ContainerAppsSubnetName `
        -AddressPrefix $networkOptions.ContainerAppsSubnetPrefix `
        -Delegation "Microsoft.App/environments"
}

if (-not (Test-AzResource -Arguments @("acr", "show", "--resource-group", $resourceGroup, "--name", $acrName))) {
    $acrCreateArgs = @(
        "acr", "create",
        "--resource-group", $resourceGroup,
        "--name", $acrName,
        "--sku", $acrSku,
        "--location", $location,
        "--admin-enabled", "false"
    )

    Invoke-AzCli -Arguments ($acrCreateArgs + (Get-TagArgs $tags)) | Out-Null
} else {
    Write-Host "Azure Container Registry already exists: $acrName"
}

$buildContext = $null
try {
    if (-not $SkipBuild) {
        $imageTagExists = Test-AcrImageTag -AcrName $acrName -ImageName $imageName -ImageTag $imageTag
        $shouldBuildImage = $true

        if ($imageTagExists -and -not $hasAppsettingsOverride) {
            Write-Host "Image tag already exists and appsettings are not baked into the image. Skipping image build: $image"
            $shouldBuildImage = $false
        } elseif ($imageTagExists -and $hasAppsettingsOverride) {
            Write-Host "Image tag already exists, but appsettings are baked into the image. Rebuilding image to keep image configuration current: $image"
        }

        if ($shouldBuildImage) {
            $buildContext = New-BuildContext -Config $config -RepoRoot $repoRoot -JobName $jobName
            $buildDockerfile = Resolve-AcrDockerfile -BuildContext $buildContext -Dockerfile $acrDockerfile

            $buildArgs = @(
                "acr", "build",
                "--resource-group", $resourceGroup,
                "--registry", $acrName,
                "--image", "${imageName}:$imageTag",
                "--file", $buildDockerfile,
                "--build-arg", "DATASYNC_PROVIDER=$dataSyncProvider",
                "--build-arg", "DATASYNC_MONITORING_PROVIDER=$monitoringProvider"
            )

            if (-not [string]::IsNullOrWhiteSpace($dotnetRuntimeImage)) {
                $buildArgs += @("--build-arg", "DOTNET_RUNTIME_IMAGE=$dotnetRuntimeImage")
            }

            if (Has-Property $config.image "buildArgs") {
                foreach ($property in $config.image.buildArgs.PSObject.Properties) {
                    $buildArgs += @("--build-arg", "$($property.Name)=$($property.Value)")
                }
            }

            $buildArgs += @(".")
            Push-Location -LiteralPath $buildContext.Path
            try {
                Invoke-AzCli -Arguments $buildArgs | Out-Null
            } finally {
                Pop-Location
            }
        }
    } else {
        Write-Host "Skipping image build. Expected image: $image"
    }
} finally {
    if ($buildContext -and $buildContext.IsTemporary -and -not $NoCleanup) {
        Write-Host "Removing temporary build context: $($buildContext.Path)"
        Remove-DirectoryWithRetry -Path $buildContext.Path
    }
}

if (-not (Test-AzResource -Arguments @("storage", "account", "show", "--resource-group", $resourceGroup, "--name", $storageAccount))) {
    $storageCreateArgs = @(
        "storage", "account", "create",
        "--resource-group", $resourceGroup,
        "--name", $storageAccount,
        "--location", $location,
        "--sku", $storageSku,
        "--kind", $storageKind,
        "--https-only", "true",
        "--min-tls-version", "TLS1_2",
        "--allow-blob-public-access", "false",
        "--default-action", "Deny",
        "--bypass", "None"
    )

    if ($networkOptions.Create) {
        $storageCreateArgs += @("--public-network-access", "Enabled")
    }

    Invoke-AzCli -Arguments ($storageCreateArgs + (Get-TagArgs $tags)) | Out-Null
} else {
    Write-Host "Storage account already exists: $storageAccount"
    if ($networkOptions.Create) {
        Write-Host "Existing storage account was not modified. Verify public network access allows selected networks, default network action is deny, and the Container Apps subnet is allowed."
    }
}

$storageId = Invoke-AzCliTsv -Arguments @("storage", "account", "show", "--resource-group", $resourceGroup, "--name", $storageAccount, "--query", "id")
if ($networkOptions.Create) {
    Ensure-StorageServiceEndpointRule `
        -NetworkOptions $networkOptions `
        -ResourceGroup $resourceGroup `
        -StorageAccount $storageAccount
}

if ($skipStorageContainerSetup) {
    Write-Host "Skipping storage container setup: $storageContainer. The container must already exist."
} elseif ($storageContainerAuth.ToLowerInvariant() -eq "login") {
    $containerExists = (Invoke-AzCliTsv -Arguments @(
        "storage", "container", "exists",
        "--account-name", $storageAccount,
        "--name", $storageContainer,
        "--auth-mode", "login",
        "--query", "exists"
    )).ToLowerInvariant()

    if ($containerExists -ne "true") {
        Invoke-AzCli -Arguments @(
            "storage", "container", "create",
            "--account-name", $storageAccount,
            "--name", $storageContainer,
            "--auth-mode", "login",
            "--public-access", "off"
        ) | Out-Null
    } else {
        Write-Host "Storage container already exists: $storageContainer"
    }
} else {
    $storageKey = Invoke-AzCliTsv -Arguments @(
        "storage", "account", "keys", "list",
        "--resource-group", $resourceGroup,
        "--account-name", $storageAccount,
        "--query", "[0].value"
    ) -Sensitive

    $containerExists = (Invoke-AzCliTsv -Arguments @(
        "storage", "container", "exists",
        "--account-name", $storageAccount,
        "--account-key", $storageKey,
        "--name", $storageContainer,
        "--query", "exists"
    ) -Sensitive).ToLowerInvariant()

    if ($containerExists -ne "true") {
        Invoke-AzCli -Arguments @(
            "storage", "container", "create",
            "--account-name", $storageAccount,
            "--account-key", $storageKey,
            "--name", $storageContainer,
            "--public-access", "off"
        ) -Sensitive | Out-Null
    } else {
        Write-Host "Storage container already exists: $storageContainer"
    }
}

if (-not (Test-AzResource -Arguments @("containerapp", "env", "show", "--resource-group", $resourceGroup, "--name", $environmentName))) {
    $containerAppEnvArgs = @(
        "containerapp", "env", "create",
        "--resource-group", $resourceGroup,
        "--name", $environmentName,
        "--location", $location,
        "--logs-destination", $logsDestination
    )

    if ($networkOptions.Create) {
        $containerAppEnvArgs += @("--infrastructure-subnet-resource-id", $containerAppsSubnetId)
        if ($networkOptions.ContainerAppsInternalOnly) {
            $containerAppEnvArgs += @("--internal-only", "true")
        }
    }

    Invoke-AzCli -Arguments ($containerAppEnvArgs + (Get-TagArgs $tags)) | Out-Null
} else {
    Write-Host "Container Apps environment already exists: $environmentName"
    if ($networkOptions.Create) {
        Write-Host "Verify existing Container Apps environment uses subnet: $containerAppsSubnetId"
    }
}

$jobExists = Test-AzResource -Arguments @("containerapp", "job", "show", "--resource-group", $resourceGroup, "--name", $jobName)
if (-not $jobExists) {
    Invoke-AzCli -Arguments (@(
        "containerapp", "job", "create",
        "--resource-group", $resourceGroup,
        "--name", $jobName,
        "--environment", $environmentName,
        "--trigger-type", "Schedule",
        "--cron-expression", $cronExpression,
        "--replica-timeout", [string] $replicaTimeout,
        "--replica-retry-limit", [string] $replicaRetryLimit,
        "--parallelism", [string] $parallelism,
        "--replica-completion-count", [string] $replicaCompletionCount,
        "--image", $bootstrapImage,
        "--cpu", $cpu,
        "--memory", $memory,
        "--mi-system-assigned"
    ) + (Get-TagArgs $tags)) | Out-Null
} else {
    Write-Host "Container Apps Job already exists: $jobName"
    if (-not $updateExistingJob) {
        Write-Host "Existing Container Apps Job was not modified. Set containerApps.updateExistingJob=true to update identity, role assignments, secrets, environment variables, image, schedule, and run settings."
        Write-Host "Deployment stopped before modifying existing job."
        return
    }

    Invoke-AzCli -Arguments @(
        "containerapp", "job", "identity", "assign",
        "--resource-group", $resourceGroup,
        "--name", $jobName,
        "--system-assigned"
    ) | Out-Null
}

$principalId = Get-JobPrincipalId -ResourceGroup $resourceGroup -JobName $jobName
$acrId = Invoke-AzCliTsv -Arguments @("acr", "show", "--resource-group", $resourceGroup, "--name", $acrName, "--query", "id")

Ensure-RoleAssignment -PrincipalId $principalId -Role "AcrPull" -Scope $acrId
Ensure-RoleAssignment -PrincipalId $principalId -Role "Storage Blob Data Contributor" -Scope $storageId

Invoke-AzCli -Arguments @(
    "containerapp", "job", "registry", "set",
    "--resource-group", $resourceGroup,
    "--name", $jobName,
    "--server", "$acrName.azurecr.io",
    "--identity", "system"
) | Out-Null

if (Has-Property $config "secrets") {
    $secretArgs = @()
    foreach ($property in $config.secrets.PSObject.Properties) {
        $secretName = $property.Name
        $secretValue = Resolve-SecretValue -SecretConfig $property.Value -SecretName $secretName
        $secretArgs += "$secretName=$secretValue"
    }

    if ($secretArgs.Count -gt 0) {
        Invoke-AzCli -Arguments (@(
            "containerapp", "job", "secret", "set",
            "--resource-group", $resourceGroup,
            "--name", $jobName,
            "--secrets"
        ) + $secretArgs) -Sensitive | Out-Null
    }
}

$envArgs = @()
if (Has-Property $config "environmentVariables") {
    foreach ($property in $config.environmentVariables.PSObject.Properties) {
        $envArgs += "$($property.Name)=$(Get-EnvironmentVariableArg $property.Value)"
    }
}

if ($envArgs.Count -gt 0) {
    Invoke-AzCli -Arguments (@(
        "containerapp", "job", "update",
        "--resource-group", $resourceGroup,
        "--name", $jobName,
        "--replace-env-vars"
    ) + $envArgs) | Out-Null
}

Invoke-AzCli -Arguments @(
    "containerapp", "job", "update",
    "--resource-group", $resourceGroup,
    "--name", $jobName,
    "--image", $image,
    "--cron-expression", $cronExpression,
    "--replica-timeout", [string] $replicaTimeout,
    "--replica-retry-limit", [string] $replicaRetryLimit,
    "--parallelism", [string] $parallelism,
    "--replica-completion-count", [string] $replicaCompletionCount,
    "--cpu", $cpu,
    "--memory", $memory
) | Out-Null

if ($runNow) {
    Invoke-AzCli -Arguments @(
        "containerapp", "job", "start",
        "--resource-group", $resourceGroup,
        "--name", $jobName
    ) | Out-Null
}

Write-Host "Deployment complete."
Write-Host "Image: $image"
Write-Host "Job: $jobName"
Write-Host "Storage: $storageAccount/$storageContainer"
