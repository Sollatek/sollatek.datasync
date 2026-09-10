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
    [string] $CanonicalBranch = 'master'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$versionExpression = '(?<version>(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*))'
$releasePattern = "^release/v$versionExpression`$"

if ($EventName -eq 'pull_request' -and $BaseRef -eq $CanonicalBranch -and $HeadRef -notmatch $releasePattern) {
    throw "Pull requests to $CanonicalBranch must come from release/vMAJOR.MINOR.PATCH."
}

if ($RefType -eq 'branch' -and $RefName.StartsWith('release/', [System.StringComparison]::Ordinal)) {
    if ($RefName -notmatch $releasePattern) {
        throw 'Release branches must use release/vMAJOR.MINOR.PATCH.'
    }
}

Write-Host "Release promotion validation passed for $EventName $RefType '$RefName'."
