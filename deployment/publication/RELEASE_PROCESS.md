# DataSync Release Process

Public release assets and container images use a gated, immutable release path:

```text
feature or fix -> develop -> release/v1.0.0 -> QA approval
                                  | manual publication and approval
                                  v
                           v1.0.0 tag and assets
                                  |
                                  v
                                master
```

`release/vMAJOR.MINOR.PATCH` is the QA and stabilization branch. Creating or
updating it runs validation only and never creates public assets or pushes an image.
After QA approval, an authorized user manually dispatches publication from that
exact branch. The protected release job creates the matching stable tag on the
approved commit, publishes the GitHub Release assets and GHCR image, and then the
unchanged release branch is merged to `master`. Ordinary changes to `develop` or
`master`, including documentation and deployment-script changes, can run validation
but never build or publish release assets.

GitHub Releases remain tag-based: the release branch authorizes publication, while
the immutable `vMAJOR.MINOR.PATCH` tag and image digest identify what customers use.

## Promotion Rules

1. Merge ordinary work to `develop` through a pull request.
2. Create `release/vMAJOR.MINOR.PATCH` from the intended `develop` commit.
3. Run QA against that release branch. Pushes validate source and tests only; they
   do not build or upload release bundles and do not push an image. Apply any QA
   fixes to the release branch through review.
4. After QA approval, open **Actions**, select **Validate or publish DataSync
   release**, choose **Run workflow**, select the exact `release/vMAJOR.MINOR.PATCH`
   branch, and run it. Selecting that release branch is the explicit publication
   signal. The equivalent GitHub CLI command is:

   ```powershell
   gh workflow run publish-release.yml --repo Sollatek/sollatek.datasync --ref release/v1.0.0
   ```
5. After validation passes, the configured release owner proceeds directly through
   the `public-release-owner` environment without a second reviewer. Any other
   authorized publisher must receive approval through the `public-release`
   environment. Only this manually dispatched release-branch job can build and
   write the public GitHub Release and GHCR image. It creates
   `vMAJOR.MINOR.PATCH` on the exact release commit; an existing tag or release fails
   closed instead of being reused or overwritten.
6. After publication succeeds, open and merge the pull request from the unchanged
   release branch to `master`. Do not squash or rebase: the released commit must
   remain in `master` history with the same identity.
7. Merge `master` back to `develop` so all release fixes are retained, then remove
   the temporary release branch according to the repository retention policy. Keep
   the protected tag and published release immutable.

The workflow fails closed when:

- a pull request to `master` does not come from
  `release/vMAJOR.MINOR.PATCH`;
- a publication dispatch does not use a valid `release/vMAJOR.MINOR.PATCH` branch;
- the source or packaged-content exclusion scan fails;
- tests, cross-platform publishing, or the multi-platform image build fails; or
- a Git tag or GitHub Release with the same version already exists.

Manual workflow dispatch publishes only when the selected ref is a correctly named
release branch. Dispatching the workflow from any other branch validates only. The
configured release owner does not need a second approval; other publishers use the
protected `public-release` reviewer gate.

## Required GitHub Settings

The workflow enforces provenance and publication rules, while repository settings
enforce who may change protected refs:

- Protect `develop` and `master` with rulesets that require pull requests,
  successful required checks, resolved conversations, and no force pushes or deletions.
- Restrict creation and updates of `release/v*` to the Sollatek release team, and
  prevent force pushes and deletions while a release is active.
- Protect `v*.*.*` tags against unauthorized creation, update, and deletion while
  allowing the protected publication workflow to create a new version tag.
- Set the repository Actions variable `PUBLIC_RELEASE_OWNER` to `kou-kos`, the
  Sollatek-controlled release identity. Do not assign this exception to a personal
  GitHub account. When the variable is unset or does not match the initiating actor,
  publication either fails before building or routes into the reviewed environment
  path. Configure it under **Settings -> Secrets and variables -> Actions ->
  Variables**.
- Configure `public-release-owner` without required reviewers and restrict it to
  protected `release/v*.*.*` branches. The workflow selects it only when
  both the original `github.actor` and the current `github.triggering_actor` match
  `PUBLIC_RELEASE_OWNER`. A re-run initiated by anybody else uses the reviewed path.
- Configure `public-release` with Sollatek QA/release reviewers, prevent self-review,
  and restrict it to protected `release/v*.*.*` branches for all other publishers.
- Only after that reviewed environment is protected, set repository variable
  `PUBLIC_RELEASE_REVIEW_GATE_ENABLED` to `true`. Until then, non-owner publication
  fails before any public artifact is built.
- Create and restrict `public-release-owner` before the first publication. Create
  and protect `public-release` before enabling any other publisher. Merely naming an
  environment in a workflow does not configure its protection rules.
- Enable immutable GitHub Releases so published assets and their tag cannot be replaced.
- Keep the default `GITHUB_TOKEN` permission read-only. The workflow grants
  `contents: write` and `packages: write` only to the protected publication job.

These settings are required. Committing the workflow does not create organization
rulesets, select the Sollatek team, or configure environment reviewers automatically.

## Publication Outputs

An approved manual publication run from a release branch publishes:

- a multi-platform GHCR image for Linux amd64 and arm64;
- an immutable `sha-<commit>` image tag;
- semantic-version, major/minor, major, and `latest` image aliases;
- a framework-dependent .NET 10 archive;
- self-contained Linux, Windows, and macOS downloads for x64 and arm64;
- `SHA256SUMS`, the release manifest, Dockerfile, and license.

Use the immutable image digest or exact semantic version for deployments. Floating
aliases such as `latest` are convenience pointers and do not update an existing
customer deployment by themselves.
