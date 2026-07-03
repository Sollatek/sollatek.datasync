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
    Invoke-AzCli -Arguments (@($Arguments) + @("--output", "tsv"))
}

function Resolve-DatabricksCliCommand {
    if (-not [string]::IsNullOrWhiteSpace($script:DatabricksCliExecutable)) {
        return
    }

    $command = Get-Command databricks -ErrorAction Stop
    $script:DatabricksCliExecutable = if (-not [string]::IsNullOrWhiteSpace($command.Source)) {
        $command.Source
    } else {
        $command.Definition
    }
}

function Get-DatabricksProfileArgs($Config) {
    $profile = Get-OptionalString $Config.databricks "profile" ""
    if ([string]::IsNullOrWhiteSpace($profile)) {
        return @()
    }

    @("--profile", $profile)
}

function Invoke-DatabricksCli([string[]] $Arguments, [string[]] $ProfileArgs) {
    Resolve-DatabricksCliCommand
    $effectiveArguments = @($Arguments) + @($ProfileArgs)
    $display = "databricks $($effectiveArguments -join ' ')"
    Write-Host ">> $display"
    $output = & $script:DatabricksCliExecutable @effectiveArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $display`n$output"
    }

    $output
}

function Test-DatabricksResource([string[]] $Arguments, [string[]] $ProfileArgs) {
    Resolve-DatabricksCliCommand
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $effectiveArguments = @($Arguments) + @($ProfileArgs)
        & $script:DatabricksCliExecutable @effectiveArguments 1>$null 2>$null
        return $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Join-AdlsPath([string] $AccountName, [string] $ContainerName, [string] $Path) {
    $url = "abfss://$ContainerName@$AccountName.dfs.core.windows.net"
    $trimmedPath = $Path.Trim("/")
    if ([string]::IsNullOrWhiteSpace($trimmedPath)) {
        return "$url/"
    }

    "$url/$trimmedPath/"
}

function Get-StorageUrl($StorageConfig) {
    $configuredUrl = Get-OptionalString $StorageConfig "url" ""
    if (-not [string]::IsNullOrWhiteSpace($configuredUrl)) {
        return $configuredUrl.TrimEnd("/") + "/"
    }

    $accountName = Get-RequiredString $StorageConfig "accountName" "storage"
    $containerName = Get-RequiredString $StorageConfig "containerName" "storage"
    $path = Get-OptionalString $StorageConfig "path" ""
    Join-AdlsPath -AccountName $accountName -ContainerName $containerName -Path $path
}

function Get-ContainerScope([string] $StorageAccountId, [string] $ContainerName) {
    "$StorageAccountId/blobServices/default/containers/$ContainerName"
}

function Resolve-AccessConnectorId($AccessConnectorConfig) {
    $id = Get-OptionalString $AccessConnectorConfig "id" ""
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        return $id
    }

    $resourceGroup = Get-RequiredString $AccessConnectorConfig "resourceGroup" "accessConnector"
    $name = Get-RequiredString $AccessConnectorConfig "name" "accessConnector"
    Invoke-AzCliTsv -Arguments @(
        "resource", "show",
        "--resource-group", $resourceGroup,
        "--name", $name,
        "--resource-type", "Microsoft.Databricks/accessConnectors",
        "--query", "id"
    )
}

function Resolve-AccessConnectorPrincipalId($AccessConnectorConfig, [string] $AccessConnectorId) {
    $configuredPrincipalId = Get-OptionalString $AccessConnectorConfig "managedIdentityPrincipalId" ""
    if (-not [string]::IsNullOrWhiteSpace($configuredPrincipalId)) {
        return $configuredPrincipalId
    }

    $managedIdentityId = Get-OptionalString $AccessConnectorConfig "managedIdentityId" ""
    if (-not [string]::IsNullOrWhiteSpace($managedIdentityId)) {
        return Invoke-AzCliTsv -Arguments @(
            "identity", "show",
            "--ids", $managedIdentityId,
            "--query", "principalId"
        )
    }

    Invoke-AzCliTsv -Arguments @(
        "resource", "show",
        "--ids", $AccessConnectorId,
        "--query", "identity.principalId"
    )
}

function Resolve-AccessConnectorTenantId($AccessConnectorConfig) {
    $tenantId = Get-OptionalString $AccessConnectorConfig "tenantId" ""
    if (-not [string]::IsNullOrWhiteSpace($tenantId)) {
        return $tenantId
    }

    Invoke-AzCliTsv -Arguments @("account", "show", "--query", "tenantId")
}

function Ensure-StorageContainer($StorageConfig) {
    if (-not (Get-OptionalBool $StorageConfig "createContainer" $false)) {
        return
    }

    $accountName = Get-RequiredString $StorageConfig "accountName" "storage"
    $containerName = Get-RequiredString $StorageConfig "containerName" "storage"
    $authMode = Get-OptionalString $StorageConfig "containerSetupAuth" "login"

    $exists = Invoke-AzCliTsv -Arguments @(
        "storage", "container", "exists",
        "--account-name", $accountName,
        "--name", $containerName,
        "--auth-mode", $authMode,
        "--query", "exists"
    )

    if ([string]::Equals($exists, "true", [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Blob container already exists: $containerName"
        return
    }

    Invoke-AzCli -Arguments @(
        "storage", "container", "create",
        "--account-name", $accountName,
        "--name", $containerName,
        "--auth-mode", $authMode
    ) | Out-Null
}

function Ensure-AccessConnectorResourceRule(
    [string] $StorageResourceGroup,
    [string] $StorageAccount,
    [string] $AccessConnectorId,
    [string] $TenantId) {

    $rulesJson = Invoke-AzCli -Arguments @(
        "storage", "account", "show",
        "--resource-group", $StorageResourceGroup,
        "--name", $StorageAccount,
        "--query", "networkRuleSet.resourceAccessRules"
    )

    $rules = if ([string]::IsNullOrWhiteSpace($rulesJson)) { @() } else { @($rulesJson | ConvertFrom-Json) }
    foreach ($rule in $rules) {
        if ([string]::Equals([string] $rule.resourceId, $AccessConnectorId, [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string] $rule.tenantId, $TenantId, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Storage resource-instance network rule already exists: $AccessConnectorId"
            return
        }
    }

    Invoke-AzCli -Arguments @(
        "storage", "account", "network-rule", "add",
        "--resource-group", $StorageResourceGroup,
        "--account-name", $StorageAccount,
        "--resource-id", $AccessConnectorId,
        "--tenant-id", $TenantId
    ) | Out-Null
}

function Ensure-RoleAssignment([string] $PrincipalId, [string] $Role, [string] $Scope) {
    if ([string]::IsNullOrWhiteSpace($PrincipalId)) {
        throw "Access connector managed identity principal id could not be resolved."
    }

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

function Write-JsonBodyFile($Body) {
    $path = [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(),
        "sollatek-datasync-databricks-catalog-$([System.Guid]::NewGuid().ToString('N')).json")
    $json = $Body | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $path -Value $json -NoNewline -Encoding UTF8
    $path
}

function Ensure-StorageCredential($UnityCatalogConfig, [string] $AccessConnectorId, [string[]] $ProfileArgs) {
    $name = Get-RequiredString $UnityCatalogConfig "storageCredentialName" "unityCatalog"
    if (Test-DatabricksResource -Arguments @("storage-credentials", "get", $name) -ProfileArgs $ProfileArgs) {
        Write-Host "Storage credential already exists: $name"
        return
    }

    $managedIdentityId = Get-OptionalString $UnityCatalogConfig "managedIdentityId" ""
    $identity = [ordered] @{
        access_connector_id = $AccessConnectorId
    }
    if (-not [string]::IsNullOrWhiteSpace($managedIdentityId)) {
        $identity.managed_identity_id = $managedIdentityId
    }

    $bodyPath = Write-JsonBodyFile ([ordered] @{
        name = $name
        azure_managed_identity = $identity
    })
    try {
        Invoke-DatabricksCli -Arguments @(
            "storage-credentials", "create",
            "--json", "@$bodyPath"
        ) -ProfileArgs $ProfileArgs | Out-Null
    } finally {
        if (Test-Path -LiteralPath $bodyPath) {
            Remove-Item -LiteralPath $bodyPath -Force
        }
    }
}

function Ensure-ExternalLocation($UnityCatalogConfig, [string] $StorageUrl, [string[]] $ProfileArgs) {
    $name = Get-RequiredString $UnityCatalogConfig "externalLocationName" "unityCatalog"
    if (Test-DatabricksResource -Arguments @("external-locations", "get", $name) -ProfileArgs $ProfileArgs) {
        Write-Host "External location already exists: $name"
        return
    }

    $credentialName = Get-RequiredString $UnityCatalogConfig "storageCredentialName" "unityCatalog"
    $args = @("external-locations", "create", $name, $StorageUrl, $credentialName)

    $comment = Get-OptionalString $UnityCatalogConfig "externalLocationComment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    if (Get-OptionalBool $UnityCatalogConfig "readOnly" $true) {
        $args += "--read-only"
    }

    if (Get-OptionalBool $UnityCatalogConfig "skipValidation" $false) {
        $args += "--skip-validation"
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Ensure-Catalog($UnityCatalogConfig, [string[]] $ProfileArgs) {
    $catalogName = Get-RequiredString $UnityCatalogConfig "catalogName" "unityCatalog"
    if (Test-DatabricksResource -Arguments @("catalogs", "get", $catalogName) -ProfileArgs $ProfileArgs) {
        Write-Host "Catalog already exists: $catalogName"
        return
    }

    $args = @("catalogs", "create", $catalogName)
    $comment = Get-OptionalString $UnityCatalogConfig "catalogComment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    $storageRoot = Get-OptionalString $UnityCatalogConfig "catalogStorageRoot" ""
    if (-not [string]::IsNullOrWhiteSpace($storageRoot)) {
        $args += @("--storage-root", $storageRoot.TrimEnd("/") + "/")
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Ensure-Schema($UnityCatalogConfig, [string[]] $ProfileArgs) {
    $schemaName = Get-OptionalString $UnityCatalogConfig "schemaName" ""
    if ([string]::IsNullOrWhiteSpace($schemaName)) {
        return
    }

    $catalogName = Get-RequiredString $UnityCatalogConfig "catalogName" "unityCatalog"
    $fullName = "$catalogName.$schemaName"
    if (Test-DatabricksResource -Arguments @("schemas", "get", $fullName) -ProfileArgs $ProfileArgs) {
        Write-Host "Schema already exists: $fullName"
        return
    }

    $args = @("schemas", "create", $schemaName, $catalogName)
    $comment = Get-OptionalString $UnityCatalogConfig "schemaComment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Ensure-ExternalVolume($UnityCatalogConfig, [string] $StorageUrl, [string[]] $ProfileArgs) {
    $schemaName = Get-OptionalString $UnityCatalogConfig "schemaName" ""
    $volumeName = Get-OptionalString $UnityCatalogConfig "volumeName" ""
    if ([string]::IsNullOrWhiteSpace($schemaName) -or [string]::IsNullOrWhiteSpace($volumeName)) {
        return
    }

    $catalogName = Get-RequiredString $UnityCatalogConfig "catalogName" "unityCatalog"
    $fullName = "$catalogName.$schemaName.$volumeName"
    if (Test-DatabricksResource -Arguments @("volumes", "read", $fullName) -ProfileArgs $ProfileArgs) {
        Write-Host "Volume already exists: $fullName"
        return
    }

    $volumeStorageUrl = Get-OptionalString $UnityCatalogConfig "volumeStorageUrl" $StorageUrl
    $args = @(
        "volumes", "create",
        $catalogName,
        $schemaName,
        $volumeName,
        "EXTERNAL",
        "--storage-location", $volumeStorageUrl.TrimEnd("/") + "/"
    )

    $comment = Get-OptionalString $UnityCatalogConfig "volumeComment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Write-Plan($Config) {
    $storage = $Config.storage
    $accessConnector = $Config.accessConnector
    $unityCatalog = $Config.unityCatalog
    $storageUrl = Get-StorageUrl $storage
    $accessConnectorId = Get-OptionalString $accessConnector "id" ""
    if ([string]::IsNullOrWhiteSpace($accessConnectorId)) {
        $accessConnectorId = "<resolved from resourceGroup/name>"
    }

    Write-Host "Plan only. No Azure or Databricks changes will be made."
    Write-Host "Subscription: $(Get-OptionalString $Config 'subscription' '<current az account>')"
    Write-Host "Storage account: $(Get-RequiredString $storage 'resourceGroup' 'storage')/$(Get-RequiredString $storage 'accountName' 'storage')"
    Write-Host "Storage container: $(Get-RequiredString $storage 'containerName' 'storage') create=$(Get-OptionalBool $storage 'createContainer' $false)"
    Write-Host "Storage URL: $storageUrl"
    Write-Host "Access connector id: $accessConnectorId"
    Write-Host "Access connector resource: $(Get-OptionalString $accessConnector 'resourceGroup' '')/$(Get-OptionalString $accessConnector 'name' '')"
    Write-Host "Add access connector as storage resource instance: $(Get-OptionalBool $accessConnector 'addStorageResourceRule' $true)"
    Write-Host "Assign Blob RBAC to access connector identity: $(Get-OptionalBool $accessConnector 'assignBlobRole' $true)"
    Write-Host "Blob RBAC role: $(Get-OptionalString $accessConnector 'blobRole' 'Storage Blob Data Contributor')"
    Write-Host "Databricks profile: $(Get-OptionalString $Config.databricks 'profile' 'DEFAULT')"
    Write-Host "Storage credential: $(Get-RequiredString $unityCatalog 'storageCredentialName' 'unityCatalog')"
    Write-Host "External location: $(Get-RequiredString $unityCatalog 'externalLocationName' 'unityCatalog')"
    Write-Host "Catalog: $(Get-RequiredString $unityCatalog 'catalogName' 'unityCatalog')"
    Write-Host "Catalog storage root: $(Get-OptionalString $unityCatalog 'catalogStorageRoot' '')"
    Write-Host "Schema: $(Get-OptionalString $unityCatalog 'schemaName' '')"
    Write-Host "External volume: $(Get-OptionalString $unityCatalog 'volumeName' '')"
}

$config = Read-DeploymentConfig $ConfigPath

if (-not (Has-Property $config "storage")) {
    throw "Missing required setting 'storage'."
}

if (-not (Has-Property $config "accessConnector")) {
    throw "Missing required setting 'accessConnector'."
}

if (-not (Has-Property $config "databricks")) {
    throw "Missing required setting 'databricks'."
}

if (-not (Has-Property $config "unityCatalog")) {
    throw "Missing required setting 'unityCatalog'."
}

if ($PlanOnly) {
    Write-Plan $config
    return
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required. Install Azure CLI or run this script from Azure Cloud Shell."
}

if (-not (Get-Command databricks -ErrorAction SilentlyContinue)) {
    throw "Databricks CLI is required. Install it and authenticate with a profile before running this script."
}

$subscription = Get-OptionalString $config "subscription" ""
if (-not [string]::IsNullOrWhiteSpace($subscription)) {
    Invoke-AzCli -Arguments @("account", "set", "--subscription", $subscription) | Out-Null
}

$storage = $config.storage
$accessConnector = $config.accessConnector
$unityCatalog = $config.unityCatalog
$storageResourceGroup = Get-RequiredString $storage "resourceGroup" "storage"
$storageAccount = Get-RequiredString $storage "accountName" "storage"
$storageContainer = Get-RequiredString $storage "containerName" "storage"
$storageUrl = Get-StorageUrl $storage
$accessConnectorId = Resolve-AccessConnectorId $accessConnector
$accessConnectorPrincipalId = Resolve-AccessConnectorPrincipalId -AccessConnectorConfig $accessConnector -AccessConnectorId $accessConnectorId
$tenantId = Resolve-AccessConnectorTenantId $accessConnector
$profileArgs = Get-DatabricksProfileArgs $config

Ensure-StorageContainer -StorageConfig $storage

if (Get-OptionalBool $accessConnector "addStorageResourceRule" $true) {
    Ensure-AccessConnectorResourceRule `
        -StorageResourceGroup $storageResourceGroup `
        -StorageAccount $storageAccount `
        -AccessConnectorId $accessConnectorId `
        -TenantId $tenantId
}

if (Get-OptionalBool $accessConnector "assignBlobRole" $true) {
    $storageId = Invoke-AzCliTsv -Arguments @(
        "storage", "account", "show",
        "--resource-group", $storageResourceGroup,
        "--name", $storageAccount,
        "--query", "id"
    )
    $scope = Get-ContainerScope -StorageAccountId $storageId -ContainerName $storageContainer
    $role = Get-OptionalString $accessConnector "blobRole" "Storage Blob Data Contributor"
    Ensure-RoleAssignment -PrincipalId $accessConnectorPrincipalId -Role $role -Scope $scope
}

Ensure-StorageCredential -UnityCatalogConfig $unityCatalog -AccessConnectorId $accessConnectorId -ProfileArgs $profileArgs
Ensure-ExternalLocation -UnityCatalogConfig $unityCatalog -StorageUrl $storageUrl -ProfileArgs $profileArgs
Ensure-Catalog -UnityCatalogConfig $unityCatalog -ProfileArgs $profileArgs
Ensure-Schema -UnityCatalogConfig $unityCatalog -ProfileArgs $profileArgs
Ensure-ExternalVolume -UnityCatalogConfig $unityCatalog -StorageUrl $storageUrl -ProfileArgs $profileArgs

Write-Host "Databricks blob catalog setup complete."
