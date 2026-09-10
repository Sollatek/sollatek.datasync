[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EventName,

    [Parameter(Mandatory)]
    [string] $RefType,

    [Parameter(Mandatory)]
    [string] $RefName,

    [string] $BaseRef = '',
    [string] $HeadRef = '',
    [string] $CommitSha = 'HEAD',
    [string] $RemoteName = 'origin',
    [string] $CanonicalBranch = 'master'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$versionExpression = '(?<version>(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*))'
$candidatePattern = "^candidate/v$versionExpression`$"
$releasePattern = "^release/v$versionExpression`$"

if ($EventName -eq 'pull_request' -and $BaseRef -eq $CanonicalBranch -and $HeadRef -notmatch $releasePattern) {
    throw "Pull requests to $CanonicalBranch must come from release/vMAJOR.MINOR.PATCH."
}

if ($RefType -eq 'branch' -and $RefName.StartsWith('candidate/', [System.StringComparison]::Ordinal)) {
    if ($RefName -notmatch $candidatePattern) {
        throw 'Candidate branches must use candidate/vMAJOR.MINOR.PATCH.'
    }

    $developRef = "refs/remotes/$RemoteName/develop"
    git show-ref --verify --quiet $developRef
    if ($LASTEXITCODE -ne 0) {
        throw "Candidate branch '$RefName' requires $RemoteName/develop to be available."
    }

    git merge-base --is-ancestor $CommitSha $developRef
    if ($LASTEXITCODE -ne 0) {
        throw "Candidate branch '$RefName' must point to a commit contained in develop."
    }
}

if ($RefType -eq 'branch' -and $RefName.StartsWith('release/', [System.StringComparison]::Ordinal)) {
    if ($RefName -notmatch $releasePattern) {
        throw 'Release branches must use release/vMAJOR.MINOR.PATCH.'
    }

    $version = $Matches.version
    $candidateRef = "refs/remotes/$RemoteName/candidate/v$version"
    git show-ref --verify --quiet $candidateRef
    if ($LASTEXITCODE -ne 0) {
        throw "Release branch '$RefName' requires candidate/v$version to remain available."
    }

    $resolvedCommitSha = (git rev-parse $CommitSha).Trim()
    $candidateSha = (git rev-parse $candidateRef).Trim()
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($resolvedCommitSha) -or
        [string]::IsNullOrWhiteSpace($candidateSha)) {
        throw "Could not resolve the release and candidate commits for version '$version'."
    }
    if (-not [string]::Equals($candidateSha, $resolvedCommitSha, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release branch '$RefName' must point to the exact QA-approved candidate/v$version commit. Do not add commits directly to the release branch."
    }
}

Write-Host "Release promotion validation passed for $EventName $RefType '$RefName'."
