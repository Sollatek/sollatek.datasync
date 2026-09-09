[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path,

    [string] $ForbiddenTextBase64,

    [switch] $ExcludedTextOnly
)

$ErrorActionPreference = "Stop"

$patterns = [ordered] @{
    "private key" = "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"
    "AWS access key" = "(?:AKIA|ASIA)[0-9A-Z]{16}"
    "GitHub token" = "(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})"
    "Azure storage account key" = "AccountKey=[A-Za-z0-9+/=]{20,}"
    "JWT bearer token" = "eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}"
    "assigned secret value" = "(?i)(?:client[_-]?secret|password|api[_-]?key|access[_-]?token)\s*[`"']?\s*[:=]\s*[`"'][^`"'{}\s]{12,}[`"']"
    "assigned tenant or subscription identifier" = "(?i)(?:tenant|subscription)(?:id)?\s*[`"']?\s*[:=]\s*[`"'][0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}[`"']"
}

$textExtensions = @(
    ".bat", ".cmd", ".config", ".cs", ".csproj", ".csv", ".env", ".ini", ".json",
    ".md", ".props", ".ps1", ".psd1", ".psm1", ".sh", ".sln", ".targets", ".toml",
    ".txt", ".xml", ".yaml", ".yml"
)

$forbiddenText = @()
if (-not [string]::IsNullOrWhiteSpace($ForbiddenTextBase64)) {
    try {
        $decodedText = [System.Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($ForbiddenTextBase64)
        )
    } catch {
        throw "The public release exclusion list is not valid Base64."
    }

    $forbiddenText = @($decodedText -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    if ($forbiddenText.Count -eq 0) {
        throw "The public release exclusion list is empty."
    }
}

$files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
foreach ($candidate in $Path) {
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Public release scan path does not exist: $candidate"
    }

    $item = Get-Item -LiteralPath $candidate -Force
    if ($item.PSIsContainer) {
        foreach ($file in Get-ChildItem -LiteralPath $item.FullName -File -Recurse -Force) {
            $files.Add($file)
        }
    } else {
        $files.Add($item)
    }
}

$findings = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    $content = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($file.FullName))
    $isTextFile = $textExtensions -contains $file.Extension.ToLowerInvariant() -or
        $file.Name.StartsWith("Dockerfile", [StringComparison]::OrdinalIgnoreCase)
    if (-not $ExcludedTextOnly) {
        foreach ($entry in $patterns.GetEnumerator()) {
            if ([System.Text.RegularExpressions.Regex]::IsMatch($content, $entry.Value)) {
                $findings.Add("$($entry.Key): $($file.FullName)")
            }
        }
    }
    foreach ($excludedText in $forbiddenText) {
        if ($excludedText.Length -lt 8 -and -not $isTextFile) {
            continue
        }
        if ($content.IndexOf($excludedText, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $findings.Add("excluded customer identifier: $($file.FullName)")
        }
    }
}

if ($findings.Count -gt 0) {
    throw "Public release content scan failed.`n$($findings -join [Environment]::NewLine)"
}

Write-Host "Public release content scan passed for $($files.Count) file(s)."
