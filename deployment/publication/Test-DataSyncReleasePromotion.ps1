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
    [string] $Actor = '',
    [string] $ReleaseOwner = '',
    [string[]] $ChangedPath = @(),
    [string] $CanonicalBranch = 'master'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$versionExpression = '(?<version>(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*))'
$releasePattern = "^release/v$versionExpression`$"

if ($EventName -eq 'pull_request' -and $BaseRef -eq $CanonicalBranch -and $HeadRef -notmatch $releasePattern) {
    if ([string]::IsNullOrWhiteSpace($ReleaseOwner) -or
        -not [string]::Equals($Actor, $ReleaseOwner, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Non-release pull requests to $CanonicalBranch are restricted to the configured release owner."
    }

    if ($ChangedPath.Count -eq 0) {
        throw "Non-release pull requests to $CanonicalBranch must provide their changed paths."
    }

    $nonRuntimePatterns = @(
        '^\.github/',
        '^deployment/',
        '^readme\.md$',
        '^licen[cs]e\.md$'
    )
    $runtimePaths = @(
        $ChangedPath |
            ForEach-Object { $_.Replace('\', '/') } |
            Where-Object {
                $candidatePath = $_
                -not ($nonRuntimePatterns | Where-Object { $candidatePath -match $_ })
            }
    )
    if ($runtimePaths.Count -gt 0) {
        throw "Pull requests to $CanonicalBranch containing runtime files must come from release/vMAJOR.MINOR.PATCH."
    }

    Write-Host "Release-owner non-runtime promotion accepted for $($ChangedPath.Count) file(s)."
}

if ($RefType -eq 'branch' -and $RefName.StartsWith('release/', [System.StringComparison]::Ordinal)) {
    if ($RefName -notmatch $releasePattern) {
        throw 'Release branches must use release/vMAJOR.MINOR.PATCH.'
    }
}

Write-Host "Release promotion validation passed for $EventName $RefType '$RefName'."
