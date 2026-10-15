# DataSync maintenance

## Local files

Copy `Sollatek.DataSync/appsettings.Development.example.json` to `Sollatek.DataSync/appsettings.Development.json`. Complete that ignored file with the designated test-environment settings obtained from the project maintainer. No instance-specific values are provided in this repository guide.

Select the Development environment when using the local file. Before starting the worker, confirm the configured target is disposable and the synchronization plan is limited to the intended test entities. A worker run can write data.

## Build and tests

`global.json` selects SDK `10.0.301` with `latestFeature` roll-forward. Main projects target `net10.0`.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet restore .\Sollatek.DataSync.sln
dotnet build .\Sollatek.DataSync.sln -c Release --no-restore
dotnet test .\Sollatek.DataSync.sln --no-restore --verbosity minimal
dotnet run --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
```

Local integration tests need their designated fixtures. Copy `.env.example` to the ignored `.env` and complete it before starting `docker-compose.datasync-tests.yml`. Copy `scripts/local-provider-tests.example.ps1` to `scripts/local-provider-tests.local.ps1`; complete the provider settings there and enable its run flag only for the disposable fixtures you intend to test. Dot-source that local script in the test shell. The provider tests have no embedded connection defaults.

```powershell
docker compose -f .\docker-compose.datasync-tests.yml config --quiet
docker compose -f .\docker-compose.datasync-tests.yml up -d
. .\scripts\local-provider-tests.local.ps1
dotnet test .\tests\Sollatek.DataSync.Tests\Sollatek.DataSync.Tests.csproj --filter FullyQualifiedName~LocalProviderIntegrationTests
```

The Compose commands create local containers, networks and persistent volumes. Starting the worker is separate from running these tests. Stop the fixtures with `docker compose -f .\docker-compose.datasync-tests.yml down`; retain volumes unless you intend to discard their data.

## Source ownership

The worker coordinates scheduling and API reads. `Sollatek.DataSync.Abstractions` defines common contracts. `Sollatek.DataSync.Export` contains export behaviour. `Stores/` contains persistence implementations; `Monitoring/` contains optional monitoring integrations. `Platform.ApiClient` contains the Platform client. `tests/` separates worker and provider regression tests.

Keep provider-specific behaviour in its provider. Preserve incremental state, watermark behaviour, output layout and cancellation/retry semantics unless the contract intentionally changes. `SyncPlan` chooses entities explicitly; do not broaden it silently.

## Contract and release changes

Use `scripts/generate-platform-api-client.ps1` with the pinned generator and intended local Swagger revision. Review the client diff and run worker/provider tests.

The public build restores from public NuGet and builds the modules in this solution; company package credentials are not required. For worker releases, record source revision, included providers, runtime identifier and configuration contract. Validate the intended provider in its designated environment and keep compatible preceding artifacts/state for rollback.
