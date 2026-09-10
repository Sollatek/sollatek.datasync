# Update An Existing Azure Job From A Published Release

Use this procedure to update an existing Sollatek DataSync Container Apps Job from a tested public release. The customer does not need to clone the repository or install Git, Docker, or the .NET SDK.

Sollatek publishes these files and images only after QA on `release/vMAJOR.MINOR.PATCH`, an authorized manual publication run from that exact branch, and approval through the protected release environment described in [DataSync Release Process](../publication/RELEASE_PROCESS.md). Creating or updating the release branch runs validation only. The approved publication job creates the matching stable tag and public GitHub Release, after which the unchanged release branch is merged to `master`. Changes to `develop` or `master` do not create release assets or images.

This is an update procedure, not a first-time Azure landing-zone deployment. It discovers or directly selects the customer's existing resources, reads the job's actual Azure location and schedule, and validates the rollback image before it changes the job.

## File To Provide To The Customer

Provide this single file:

```text
Invoke-DataSyncProductionRelease.ps1
```

It can be uploaded to Azure Cloud Shell or copied to another PowerShell environment that has the Azure CLI and access to the target subscription. No repository checkout is required.

## Requirements

- An existing DataSync Container Apps Job and an Azure Container Registry containing its current image.
- A current Azure CLI login with access to the target subscription, ACR, Container Apps Job, and related validation resources.
- PowerShell and Azure CLI. Binary delivery also requires `tar`, which is available in Azure Cloud Shell.
- A public Sollatek DataSync GitHub release.
- Public anonymous access to the Sollatek GHCR package when using `PrebuiltImage`.

The script uses the current Azure CLI login. `-Subscription` accepts either the subscription name or ID; `-SubscriptionId` remains an alias for existing commands. It does not require `TenantId`.

## Target Discovery And Explicit Selection

The script contains no fixed resource group, Azure region, registry, storage-account, job, or schedule values.

- With only `-Subscription`, it lists accessible Container Apps Jobs and selects the single job whose name, current image, or DataSync environment-variable signature identifies it as DataSync.
- It reads the selected job's resource group, location, image, and schedule from Azure.
- It derives the destination ACR and image repository from the current job image.
- If subscription-level listing is not permitted, no job matches, or more than one job matches, provide `-ResourceGroup`, `-JobName`, and `-RegistryName` explicitly. `-ImageRepository` is also available if the repository path cannot be derived from the current image.
- Explicit values select existing resources only. The release script does not create a resource group, registry, or job.

## Choose A Delivery Mode

| Delivery mode | What the script uses | Build performed in customer Azure | Base-image choice |
| --- | --- | --- | --- |
| `PrebuiltImage` | Published Sollatek image pinned by digest | No source or application build; ACR imports the verified image | Fixed by the published image |
| `Binary` | Published portable compiled archive verified against the release manifest | ACR assembles the already-compiled files into a final image; DataSync source is not compiled | Customer selects a compatible .NET 10 runtime image |

`PrebuiltImage` is the recommended path because the published multi-platform image is the exact artifact tested by the release workflow. Use `Binary` only when customer policy requires choosing the final runtime base image.

The release also contains self-contained executables for Linux, Windows, and macOS on x64 and arm64. Those archives are intended for direct hosting or a customer-authored deployment and are not consumed by this guarded Azure update script. Using the portable archive in `Binary` mode keeps the .NET runtime and native operating-system dependencies explicit in the selected base image.

## Validate Without Changing Azure

Use a fixed semantic version for a repeatable deployment:

```powershell
.\Invoke-DataSyncProductionRelease.ps1 `
  -Subscription '<customer-subscription-name-or-guid>' `
  -ReleaseVersion 1.0.0
```

If automatic discovery is unavailable or ambiguous, select the existing resources explicitly:

```powershell
.\Invoke-DataSyncProductionRelease.ps1 `
  -Subscription '<customer-subscription-name-or-guid>' `
  -ResourceGroup '<existing-job-resource-group>' `
  -JobName '<existing-datasync-job-name>' `
  -RegistryName '<existing-acr-name>' `
  -ReleaseVersion 1.0.0
```

This validates the release, current Azure login, selected existing job image, actual location, unchanged schedule, and planned target image. It does not deploy because `-Deploy` is absent.

## Deploy The Published Image

```powershell
.\Invoke-DataSyncProductionRelease.ps1 `
  -Subscription '<customer-subscription-name-or-guid>' `
  -ReleaseVersion 1.0.0 `
  -DeliveryMode PrebuiltImage `
  -Deploy
```

The script imports the published image by immutable digest into the existing ACR and updates only the Container Apps Job image. It verifies the imported digest and confirms that the job schedule remains unchanged.

## Assemble The Published Binary With A Selected Runtime Image

```powershell
.\Invoke-DataSyncProductionRelease.ps1 `
  -Subscription '<customer-subscription-name-or-guid>' `
  -ReleaseVersion 1.0.0 `
  -DeliveryMode Binary `
  -BaseImage mcr.microsoft.com/dotnet/runtime:10.0 `
  -Deploy
```

The script downloads the portable compiled archive, verifies its SHA-256 hash from the release manifest, and asks ACR to assemble the final image. `BaseImage` must provide a compatible .NET 10 runtime and the native libraries required by DataSync. The customer owns the security, availability, and compatibility of any non-default base image it selects.

## Version Selection

- `1.0.0` and `v1.0.0` select the same immutable release.
- `latest` resolves the newest published release when the command runs.
- `latest` does not automatically update a deployed Azure job and does not force a running host to pull a new image. The operator must run the script again with `-Deploy`.
- Keep the previous image reported by the script as the rollback identity.

## What The Command Does Not Change

Unless historical-run switches are supplied, the command does not create a Blob container, delete state, alter the export prefix, start a manual execution, or change the job schedule. Existing secret references remain in Azure and are not printed.

## Direct Executable Downloads

Each semantic release publishes these self-contained targets:

- `linux-x64`
- `linux-arm64`
- `win-x64`
- `win-arm64`
- `osx-x64`
- `osx-arm64`

Verify an archive against `SHA256SUMS` before extracting it. Each archive includes `LICENCE.md` and the build's `Sollatek.DataSync.deps.json` runtime dependency inventory. macOS executables are not code-signed or notarized unless the release notes explicitly say otherwise.

## License And Dependencies

Sollatek.DataSync is source-available under the MIT License with Commons Clause; see the repository [LICENCE.md](../../LICENCE.md). Published artifacts also contain third-party .NET and NuGet components governed by their own licenses. Review direct and transitive dependency licenses and any required notices before redistributing the binaries or container outside the intended customer deployment.
