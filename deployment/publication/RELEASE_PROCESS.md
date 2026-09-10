# DataSync Release Process

Public release assets and container images use a gated, immutable release path:

```text
feature or fix -> develop -> candidate/v1.0.0 -> QA approval -> release/v1.0.0
                                                               | public publication
                                                               v
                                                        v1.0.0 tag and assets
                                                               |
                                                               v
                                                             master
```

Creating `release/vMAJOR.MINOR.PATCH` from the exact approved candidate commit is
the explicit publication signal. The protected release job creates the matching
stable tag on that commit, publishes the GitHub Release assets and GHCR image, and
then the unchanged release branch is merged to `master`. Ordinary changes to
`develop` or `master`, including documentation and deployment-script changes, can
run validation but never build or publish release assets.

GitHub Releases remain tag-based: the release branch authorizes publication, while
the immutable `vMAJOR.MINOR.PATCH` tag and image digest identify what customers use.

## Promotion Rules

1. Merge ordinary work to `develop` through a pull request.
2. Create `candidate/vMAJOR.MINOR.PATCH` from the intended `develop` commit.
3. Run QA against that exact candidate commit. Candidate CI validates source and
   tests only; it does not build or upload release bundles and does not push an image.
4. After QA approval, create `release/vMAJOR.MINOR.PATCH` from the approved
   candidate commit without adding another commit. That branch push starts the
   release workflow.
5. Approve the `public-release` GitHub environment. Only this release-branch job can
   build and write the public GitHub Release and GHCR image. It creates
   `vMAJOR.MINOR.PATCH` on the exact release commit; an existing tag or release fails
   closed instead of being reused or overwritten.
6. After publication succeeds, open and merge the pull request from the unchanged
   release branch to `master`. Do not squash or rebase: the released commit must
   remain in `master` history with the same identity.
7. Merge `master` back to `develop`, then remove the temporary candidate and release
   branches according to the repository retention policy. Keep the protected tag and
   published release immutable.

The workflow fails closed when:

- a pull request to `master` does not come from
  `release/vMAJOR.MINOR.PATCH`;
- a candidate commit is not contained in `develop`;
- a release branch does not point to the exact matching candidate commit;
- the source or packaged-content exclusion scan fails;
- tests, cross-platform publishing, or the multi-platform image build fails; or
- a Git tag or GitHub Release with the same version already exists.

Manual workflow dispatch validates only. It cannot publish a public release.

## Required GitHub Settings

The workflow enforces provenance and publication rules, while repository settings
enforce who may change protected refs:

- Protect `develop` and `master` with rulesets that require pull requests,
  successful required checks, resolved conversations, and no force pushes or deletions.
- Restrict creation and updates of `candidate/v*` and `release/v*` to the
  Sollatek release team, and prevent force pushes and deletions while a release is active.
- Protect `v*.*.*` tags against unauthorized creation, update, and deletion while
  allowing the protected publication workflow to create a new version tag.
- Configure the `public-release` environment with Sollatek QA/release reviewers,
  prevent self-review, and restrict it to protected `release/v*.*.*` branches.
- Enable immutable GitHub Releases so published assets and their tag cannot be replaced.
- Keep the default `GITHUB_TOKEN` permission read-only. The workflow grants
  `contents: write` and `packages: write` only to the protected publication job.

These settings are required. Committing the workflow does not create organization
rulesets, select the Sollatek team, or configure environment reviewers automatically.

## Publication Outputs

An approved release-branch push publishes:

- a multi-platform GHCR image for Linux amd64 and arm64;
- an immutable `sha-<commit>` image tag;
- semantic-version, major/minor, major, and `latest` image aliases;
- a framework-dependent .NET 10 archive;
- self-contained Linux, Windows, and macOS downloads for x64 and arm64;
- `SHA256SUMS`, the release manifest, Dockerfile, and license.

Use the immutable image digest or exact semantic version for deployments. Floating
aliases such as `latest` are convenience pointers and do not update an existing
customer deployment by themselves.
