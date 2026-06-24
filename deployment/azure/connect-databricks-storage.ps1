[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConfigPath,

    [switch] $PlanOnly
)

$ErrorActionPreference = "Stop"

function Get-ScriptRoot {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return $PSScriptRoot
    }

    Split-Path -Parent $MyInvocation.ScriptName
}

function Resolve-ConfigPath([string] $Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    $candidate = Join-Path (Get-Location) $Path
    if (Test-Path -LiteralPath $candidate) {
        return (Resolve-Path -LiteralPath $candidate).Path
    }

    $scriptRelative = Join-Path (Get-ScriptRoot) $Path
    if (Test-Path -LiteralPath $scriptRelative) {
        return (Resolve-Path -LiteralPath $scriptRelative).Path
    }

    throw "Config file not found: $Path"
}

function Read-DeploymentConfig([string] $Path) {
    $resolved = Resolve-ConfigPath $Path
    Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
}

function Has-Property($Object, [string] $Name) {
    $null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]
}

function Get-RequiredString($Object, [string] $Name, [string] $Path) {
    if (-not (Has-Property $Object $Name)) {
        throw "Missing required setting '$Path.$Name'."
    }

    $value = [string] $Object.$Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Setting '$Path.$Name' must not be empty."
    }

    $value
}

function Get-OptionalString($Object, [string] $Name, [string] $DefaultValue) {
    if (Has-Property $Object $Name) { [string] $Object.$Name } else { $DefaultValue }
}

function Get-OptionalBool($Object, [string] $Name, [bool] $DefaultValue) {
    if (Has-Property $Object $Name) { [bool] $Object.$Name } else { $DefaultValue }
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

function Invoke-AzCli([string[]] $Arguments) {
    Resolve-AzCliCommand
    $effectiveArguments = @($Arguments)
    if ($effectiveArguments -notcontains "--only-show-errors") {
        $effectiveArguments += "--only-show-errors"
    }

    $display = "az $($effectiveArguments -join ' ')"
    Write-Host ">> $display"
    $processArguments = @($script:AzCliPrefixArguments) + $effectiveArguments
    $output = & $script:AzCliExecutable @processArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $display`n$output"
    }

    $output
}

function Invoke-AzCliTsv([string[]] $Arguments) {
    $args = @($Arguments) + @("--output", "tsv")
    Invoke-AzCli -Arguments $args
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

function Get-SubnetNames($NetworkConfig) {
    if (-not (Has-Property $NetworkConfig "subnets")) {
        throw "Missing required setting 'databricks.network.subnets'."
    }

    $names = @()
    foreach ($subnet in @($NetworkConfig.subnets)) {
        if ($subnet -is [string]) {
            $name = $subnet
        } else {
            $name = Get-RequiredString $subnet "name" "databricks.network.subnets[]"
        }

        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $names += $name
        }
    }

    if ($names.Count -eq 0) {
        throw "Setting 'databricks.network.subnets' must contain at least one subnet name."
    }

    $names
}

function Get-StorageScope([string] $StorageId, [string] $ContainerName, [string] $ScopeKind) {
    switch ($ScopeKind.ToLowerInvariant()) {
        "storageaccount" { return $StorageId }
        "container" {
            if ([string]::IsNullOrWhiteSpace($ContainerName)) {
                throw "storage.containerName is required when an RBAC assignment uses scope 'container'."
            }

            return "$StorageId/blobServices/default/containers/$ContainerName"
        }
        default {
            throw "Unsupported RBAC scope '$ScopeKind'. Use 'storageAccount' or 'container'."
        }
    }
}

function Ensure-SubnetServiceEndpoint([string] $ResourceGroup, [string] $VnetName, [string] $SubnetName, [string] $ServiceEndpoint) {
    if (-not (Test-AzResource -Arguments @(
        "network", "vnet", "subnet", "show",
        "--resource-group", $ResourceGroup,
        "--vnet-name", $VnetName,
        "--name", $SubnetName
    ))) {
        throw "Subnet '$SubnetName' was not found in VNet '$VnetName' resource group '$ResourceGroup'."
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
        Write-Host "Subnet already has service endpoint '$ServiceEndpoint': $SubnetName"
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

function Ensure-StorageNetworkRule([string] $StorageResourceGroup, [string] $StorageAccount, [string] $SubnetId) {
    $existingRules = Invoke-AzCliTsv -Arguments @(
        "storage", "account", "show",
        "--resource-group", $StorageResourceGroup,
        "--name", $StorageAccount,
        "--query", "networkRuleSet.virtualNetworkRules[].virtualNetworkResourceId"
    )

    if (($existingRules -split "\s+") -contains $SubnetId) {
        Write-Host "Storage network rule already exists: $SubnetId"
        return
    }

    Invoke-AzCli -Arguments @(
        "storage", "account", "network-rule", "add",
        "--resource-group", $StorageResourceGroup,
        "--account-name", $StorageAccount,
        "--subnet", $SubnetId
    ) | Out-Null
}

function Ensure-StorageSelectedNetworks($StorageConfig, [string] $StorageResourceGroup, [string] $StorageAccount) {
    $enforce = Get-OptionalBool $StorageConfig "enforceSelectedNetworks" $false
    $network = Invoke-AzCli -Arguments @(
        "storage", "account", "show",
        "--resource-group", $StorageResourceGroup,
        "--name", $StorageAccount,
        "--query", "{publicNetworkAccess:publicNetworkAccess,defaultAction:networkRuleSet.defaultAction,bypass:networkRuleSet.bypass}"
    ) | ConvertFrom-Json

    if ($enforce) {
        Invoke-AzCli -Arguments @(
            "storage", "account", "update",
            "--resource-group", $StorageResourceGroup,
            "--name", $StorageAccount,
            "--public-network-access", "Enabled",
            "--default-action", "Deny",
            "--bypass", "None"
        ) | Out-Null
        return
    }

    if ($network.publicNetworkAccess -ne "Enabled") {
        throw "Storage account '$StorageAccount' has publicNetworkAccess '$($network.publicNetworkAccess)'. Service endpoint rules require publicNetworkAccess Enabled with defaultAction Deny. Set storage.enforceSelectedNetworks=true to let this script apply those storage network settings."
    }

    if ($network.defaultAction -ne "Deny") {
        Write-Warning "Storage account '$StorageAccount' default network action is '$($network.defaultAction)'. The Databricks subnet rule will be added, but selected-network restriction is not enforced until defaultAction is Deny."
    }
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

    Invoke-AzCli -Arguments @(
        "role", "assignment", "create",
        "--assignee-object-id", $PrincipalId,
        "--assignee-principal-type", "ServicePrincipal",
        "--role", $Role,
        "--scope", $Scope
    ) | Out-Null
}

function Write-Plan($Config) {
    $storage = $Config.storage
    $databricks = $Config.databricks
    $network = $databricks.network
    $subnets = Get-SubnetNames $network

    Write-Host "Plan only. No Azure changes will be made."
    Write-Host "Subscription: $($Config.subscription)"
    Write-Host "Storage account: $(Get-RequiredString $storage 'resourceGroup' 'storage')/$(Get-RequiredString $storage 'accountName' 'storage')"
    Write-Host "Storage container: $(Get-OptionalString $storage 'containerName' '')"
    Write-Host "Enforce selected-network storage settings: $(Get-OptionalBool $storage 'enforceSelectedNetworks' $false)"
    Write-Host "Databricks workspace: $(Get-OptionalString $databricks 'workspaceResourceGroup' '')/$(Get-OptionalString $databricks 'workspaceName' '')"
    Write-Host "Databricks VNet: $(Get-RequiredString $network 'resourceGroup' 'databricks.network')/$(Get-RequiredString $network 'vnetName' 'databricks.network')"
    Write-Host "Storage service endpoint: $(Get-OptionalString $network 'storageServiceEndpoint' 'Microsoft.Storage')"
    foreach ($subnet in $subnets) {
        Write-Host "Databricks subnet to allow: $subnet"
    }

    if (Has-Property $databricks "rbac") {
        foreach ($assignment in @($databricks.rbac)) {
            Write-Host "RBAC assignment: principal=$(Get-RequiredString $assignment 'principalId' 'databricks.rbac[]') role=$(Get-OptionalString $assignment 'role' 'Storage Blob Data Contributor') scope=$(Get-OptionalString $assignment 'scope' 'container')"
        }
    }
}

$config = Read-DeploymentConfig $ConfigPath

if ($PlanOnly) {
    Write-Plan $config
    return
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required. Install Azure CLI or run this script from Azure Cloud Shell."
}

$subscription = Get-RequiredString $config "subscription" "root"
$storage = $config.storage
$databricks = $config.databricks
$network = $databricks.network

$storageResourceGroup = Get-RequiredString $storage "resourceGroup" "storage"
$storageAccount = Get-RequiredString $storage "accountName" "storage"
$storageContainer = Get-OptionalString $storage "containerName" ""
$networkResourceGroup = Get-RequiredString $network "resourceGroup" "databricks.network"
$vnetName = Get-RequiredString $network "vnetName" "databricks.network"
$serviceEndpoint = Get-OptionalString $network "storageServiceEndpoint" "Microsoft.Storage"
$subnetNames = Get-SubnetNames $network

Invoke-AzCli -Arguments @("account", "set", "--subscription", $subscription) | Out-Null

if (-not (Test-AzResource -Arguments @("storage", "account", "show", "--resource-group", $storageResourceGroup, "--name", $storageAccount))) {
    throw "Storage account '$storageAccount' was not found in resource group '$storageResourceGroup'."
}

$workspaceName = Get-OptionalString $databricks "workspaceName" ""
$workspaceResourceGroup = Get-OptionalString $databricks "workspaceResourceGroup" ""
if (-not [string]::IsNullOrWhiteSpace($workspaceName) -and -not [string]::IsNullOrWhiteSpace($workspaceResourceGroup)) {
    if (-not (Test-AzResource -Arguments @(
        "resource", "show",
        "--resource-group", $workspaceResourceGroup,
        "--name", $workspaceName,
        "--resource-type", "Microsoft.Databricks/workspaces"
    ))) {
        throw "Databricks workspace '$workspaceName' was not found in resource group '$workspaceResourceGroup'."
    }
}

Ensure-StorageSelectedNetworks -StorageConfig $storage -StorageResourceGroup $storageResourceGroup -StorageAccount $storageAccount

$storageId = Invoke-AzCliTsv -Arguments @(
    "storage", "account", "show",
    "--resource-group", $storageResourceGroup,
    "--name", $storageAccount,
    "--query", "id"
)

foreach ($subnetName in $subnetNames) {
    Ensure-SubnetServiceEndpoint `
        -ResourceGroup $networkResourceGroup `
        -VnetName $vnetName `
        -SubnetName $subnetName `
        -ServiceEndpoint $serviceEndpoint

    $subnetId = Invoke-AzCliTsv -Arguments @(
        "network", "vnet", "subnet", "show",
        "--resource-group", $networkResourceGroup,
        "--vnet-name", $vnetName,
        "--name", $subnetName,
        "--query", "id"
    )

    Ensure-StorageNetworkRule `
        -StorageResourceGroup $storageResourceGroup `
        -StorageAccount $storageAccount `
        -SubnetId $subnetId
}

if (Has-Property $databricks "rbac") {
    foreach ($assignment in @($databricks.rbac)) {
        $principalId = Get-RequiredString $assignment "principalId" "databricks.rbac[]"
        $role = Get-OptionalString $assignment "role" "Storage Blob Data Contributor"
        $scopeKind = Get-OptionalString $assignment "scope" "container"
        $scope = Get-StorageScope -StorageId $storageId -ContainerName $storageContainer -ScopeKind $scopeKind
        Ensure-RoleAssignment -PrincipalId $principalId -Role $role -Scope $scope
    }
}

Write-Host "Databricks storage network connection complete."
