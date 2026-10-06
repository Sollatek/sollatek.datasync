# Contributing to DataSync

Create a topic branch and open a pull request against the maintained target branch. The `Project build` check restores, compiles and tests the complete solution. A successful check for an up-to-date proposed change is required before a maintainer merges it. Pull requests may be opened with failing builds while fixes are in progress.

Install the stable .NET SDK declared in `global.json`, then run:

```shell
dotnet test Sollatek.DataSync.sln --configuration Release --disable-build-servers -m:1 --verbosity minimal
```

The public build workflow needs no repository secrets or publication credentials. Test fixtures own their temporary data. Review the existing project README for application configuration and provider-specific usage. Never commit credentials, private connection configuration or customer data.

Workflows from external contributors require a maintainer's approval to run. That approval permits CI execution; it does not approve or merge the pull request. External contributors do not receive repository write access. Merging is a manual maintainer action, with no automatic approval or merge.

Describe the problem, resulting behavior and relevant tests in the pull request. Explain changes that remove or replace a maintained feature. Preserve published history and release tags; use a revert pull request to restore an earlier implementation.

Public release authorization and content-exclusion checks remain in the separate release workflow. Those checks use private maintainer configuration and may be unavailable to a fork. Do not expose that configuration to forks or execute untrusted proposed code in a privileged pull-request-target workflow. Passing `Project build` does not authorize a release or deployment.
