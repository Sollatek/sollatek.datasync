param(
    [Parameter(Mandatory = $false)]
    [uri]$SwaggerUrl = "http://127.0.0.1:15700/swagger/data-v1/swagger.json",

    [Parameter(Mandatory = $false)]
    [switch]$AllowRemoteSwagger
)

$ErrorActionPreference = "Stop"

$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$TemporaryRoot = Join-Path $RepositoryRoot ".tmp\platform-api-client-generation"
$TemporaryClients = Join-Path $TemporaryRoot "ApiClients.cs"
$TemporaryModels = Join-Path $TemporaryRoot "ApiModels.cs"
$ClientProject = Join-Path $RepositoryRoot "Platform.ApiClient"
$TrackedClients = Join-Path $ClientProject "ApiClients.cs"
$TrackedModels = Join-Path $ClientProject "ApiModels.cs"
$BackupClients = Join-Path $TemporaryRoot "ApiClients.cs.backup"
$BackupModels = Join-Path $TemporaryRoot "ApiModels.cs.backup"

if ($SwaggerUrl.Scheme -notin @("http", "https")) {
    throw "SwaggerUrl must use HTTP or HTTPS."
}

$isLoopback = $SwaggerUrl.IsLoopback -or $SwaggerUrl.Host -eq "localhost"
if (-not $isLoopback -and -not $AllowRemoteSwagger) {
    throw "Remote Swagger is disabled by default. Pass -AllowRemoteSwagger to opt in explicitly."
}

if (Test-Path $TemporaryRoot) {
    Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $TemporaryRoot | Out-Null
Push-Location $RepositoryRoot

try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore the pinned NSwag tool."
    }

    dotnet tool run nswag openapi2csclient `
        "/Input:$SwaggerUrl" `
        "/Output:$TemporaryClients" `
        "/ContractsOutput:$TemporaryModels" `
        "/GenerateContractsOutput:true" `
        "/Namespace:Platform.ApiClient" `
        "/ContractsNamespace:Platform.ApiClient.Models" `
        "/ClientBaseClass:BaseClient" `
        "/ConfigurationClass:IClientSettings" `
        "/GenerateClientClasses:true" `
        "/GenerateClientInterfaces:true" `
        "/InjectHttpClient:true" `
        "/DisposeHttpClient:false" `
        "/UseBaseUrl:false" `
        "/GenerateBaseUrlProperty:false" `
        "/GeneratePrepareRequestAndProcessResponseAsAsyncMethods:true" `
        "/ExposeJsonSerializerSettings:true" `
        "/GenerateOptionalParameters:true" `
        "/WrapResponses:true" `
        "/GenerateResponseClasses:true" `
        "/ResponseClass:ApiResponse" `
        "/GenerateExceptionClasses:true" `
        "/ExceptionClass:ApiException" `
        "/OperationGenerationMode:MultipleClientsFromOperationId" `
        "/ClassName:{controller}Client" `
        "/GenerateDtoTypes:true" `
        "/NewLineBehavior:CRLF"
    if ($LASTEXITCODE -ne 0) {
        throw "NSwag client generation failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $TemporaryClients) -or -not (Test-Path $TemporaryModels)) {
        throw "NSwag did not produce both expected client files."
    }

    Copy-Item -LiteralPath $TrackedClients -Destination $BackupClients
    Copy-Item -LiteralPath $TrackedModels -Destination $BackupModels
    try {
        Copy-Item -LiteralPath $TemporaryClients -Destination $TrackedClients -Force
        Copy-Item -LiteralPath $TemporaryModels -Destination $TrackedModels -Force
    }
    catch {
        Copy-Item -LiteralPath $BackupClients -Destination $TrackedClients -Force
        Copy-Item -LiteralPath $BackupModels -Destination $TrackedModels -Force
        throw
    }
    Write-Host "Platform.ApiClient regenerated from $SwaggerUrl with pinned NSwag 14.7.1."
}
finally {
    Pop-Location
    if (Test-Path $TemporaryRoot) {
        Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force
    }
}
