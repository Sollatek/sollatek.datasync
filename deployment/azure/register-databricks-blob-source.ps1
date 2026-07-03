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

function Get-DatabricksProfileArgs($Config) {
    $profile = Get-OptionalString $Config.databricks "profile" ""
    if ([string]::IsNullOrWhiteSpace($profile)) {
        return @()
    }

    @("--profile", $profile)
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

function Write-JsonBodyFile($Body) {
    $path = [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(),
        "sollatek-datasync-databricks-source-$([System.Guid]::NewGuid().ToString('N')).json")
    $json = $Body | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $path -Value $json -NoNewline -Encoding UTF8
    $path
}

function Ensure-StorageCredential($UnityCatalogConfig, [string[]] $ProfileArgs) {
    $name = Get-RequiredString $UnityCatalogConfig "storageCredentialName" "unityCatalog"
    if (Test-DatabricksResource -Arguments @("storage-credentials", "get", $name) -ProfileArgs $ProfileArgs) {
        Write-Host "Storage credential already exists: $name"
        return
    }

    $accessConnectorId = Get-RequiredString $UnityCatalogConfig "accessConnectorId" "unityCatalog"
    $managedIdentityId = Get-OptionalString $UnityCatalogConfig "managedIdentityId" ""
    $identity = [ordered] @{
        access_connector_id = $accessConnectorId
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

    $comment = Get-OptionalString $UnityCatalogConfig "comment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    if (Get-OptionalBool $UnityCatalogConfig "readOnly" $true) {
        $args += "--read-only"
    }

    if (Get-OptionalBool $UnityCatalogConfig "skipValidation" $false) {
        $args += "--skip-validation"
    }

    if (Get-OptionalBool $UnityCatalogConfig "enableFileEvents" $false) {
        $args += "--enable-file-events"
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Ensure-Catalog($UnityCatalogConfig, [string[]] $ProfileArgs) {
    $catalogName = Get-OptionalString $UnityCatalogConfig "catalogName" ""
    if ([string]::IsNullOrWhiteSpace($catalogName)) {
        return
    }

    if (-not (Get-OptionalBool $UnityCatalogConfig "createCatalog" $true)) {
        Write-Host "Catalog creation disabled: $catalogName"
        return
    }

    if (Test-DatabricksResource -Arguments @("catalogs", "get", $catalogName) -ProfileArgs $ProfileArgs) {
        Write-Host "Catalog already exists: $catalogName"
        return
    }

    $args = @("catalogs", "create", $catalogName)
    $comment = Get-OptionalString $UnityCatalogConfig "catalogComment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Ensure-Schema($UnityCatalogConfig, [string[]] $ProfileArgs) {
    $catalogName = Get-OptionalString $UnityCatalogConfig "catalogName" ""
    $schemaName = Get-OptionalString $UnityCatalogConfig "schemaName" ""
    if ([string]::IsNullOrWhiteSpace($catalogName) -or [string]::IsNullOrWhiteSpace($schemaName)) {
        return
    }

    if (-not (Get-OptionalBool $UnityCatalogConfig "createSchema" $true)) {
        Write-Host "Schema creation disabled: $catalogName.$schemaName"
        return
    }

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
    $catalogName = Get-OptionalString $UnityCatalogConfig "catalogName" ""
    $schemaName = Get-OptionalString $UnityCatalogConfig "schemaName" ""
    $volumeName = Get-OptionalString $UnityCatalogConfig "volumeName" ""
    if ([string]::IsNullOrWhiteSpace($catalogName) -or
        [string]::IsNullOrWhiteSpace($schemaName) -or
        [string]::IsNullOrWhiteSpace($volumeName)) {
        return
    }

    if (-not (Get-OptionalBool $UnityCatalogConfig "createVolume" $true)) {
        Write-Host "Volume creation disabled: $catalogName.$schemaName.$volumeName"
        return
    }

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
        "--storage-location", $volumeStorageUrl
    )

    $comment = Get-OptionalString $UnityCatalogConfig "volumeComment" ""
    if (-not [string]::IsNullOrWhiteSpace($comment)) {
        $args += @("--comment", $comment)
    }

    Invoke-DatabricksCli -Arguments $args -ProfileArgs $ProfileArgs | Out-Null
}

function Write-Plan($Config) {
    $storage = $Config.storage
    $databricks = $Config.databricks
    $unityCatalog = $Config.unityCatalog
    $storageUrl = Get-StorageUrl $storage

    Write-Host "Plan only. No Databricks changes will be made."
    Write-Host "Databricks profile: $(Get-OptionalString $databricks 'profile' 'DEFAULT')"
    Write-Host "Storage URL: $storageUrl"
    Write-Host "Storage credential: $(Get-RequiredString $unityCatalog 'storageCredentialName' 'unityCatalog')"
    Write-Host "Access connector: $(Get-RequiredString $unityCatalog 'accessConnectorId' 'unityCatalog')"
    Write-Host "Managed identity: $(Get-OptionalString $unityCatalog 'managedIdentityId' '')"
    Write-Host "External location: $(Get-RequiredString $unityCatalog 'externalLocationName' 'unityCatalog')"
    Write-Host "External location read-only: $(Get-OptionalBool $unityCatalog 'readOnly' $true)"
    Write-Host "Skip external location validation: $(Get-OptionalBool $unityCatalog 'skipValidation' $false)"
    Write-Host "Enable file events: $(Get-OptionalBool $unityCatalog 'enableFileEvents' $false)"

    $catalogName = Get-OptionalString $unityCatalog "catalogName" ""
    $schemaName = Get-OptionalString $unityCatalog "schemaName" ""
    $volumeName = Get-OptionalString $unityCatalog "volumeName" ""
    if (-not [string]::IsNullOrWhiteSpace($catalogName)) {
        Write-Host "Catalog: $catalogName create=$(Get-OptionalBool $unityCatalog 'createCatalog' $true)"
    }

    if (-not [string]::IsNullOrWhiteSpace($schemaName)) {
        Write-Host "Schema: $catalogName.$schemaName create=$(Get-OptionalBool $unityCatalog 'createSchema' $true)"
    }

    if (-not [string]::IsNullOrWhiteSpace($volumeName)) {
        $volumeStorageUrl = Get-OptionalString $unityCatalog "volumeStorageUrl" $storageUrl
        Write-Host "External volume: $catalogName.$schemaName.$volumeName create=$(Get-OptionalBool $unityCatalog 'createVolume' $true) storage=$volumeStorageUrl"
    }
}

$config = Read-DeploymentConfig $ConfigPath

if (-not (Has-Property $config "storage")) {
    throw "Missing required setting 'storage'."
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

if (-not (Get-Command databricks -ErrorAction SilentlyContinue)) {
    throw "Databricks CLI is required. Install it and authenticate with a profile before running this script."
}

$profileArgs = Get-DatabricksProfileArgs $config
$storageUrl = Get-StorageUrl $config.storage
$unityCatalog = $config.unityCatalog

Ensure-StorageCredential -UnityCatalogConfig $unityCatalog -ProfileArgs $profileArgs
Ensure-ExternalLocation -UnityCatalogConfig $unityCatalog -StorageUrl $storageUrl -ProfileArgs $profileArgs
Ensure-Catalog -UnityCatalogConfig $unityCatalog -ProfileArgs $profileArgs
Ensure-Schema -UnityCatalogConfig $unityCatalog -ProfileArgs $profileArgs
Ensure-ExternalVolume -UnityCatalogConfig $unityCatalog -StorageUrl $storageUrl -ProfileArgs $profileArgs

Write-Host "Databricks blob source registration complete."
