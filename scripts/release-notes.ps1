# Extracts one version section from CHANGELOG.md for a GitHub Release body.
#
# Usage:
#   pwsh ./scripts/release-notes.ps1 -Version 3.3.3
#   pwsh ./scripts/release-notes.ps1 -Version 3.3.3 -OutputPath dist/3.3.3/release-notes.md
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$ChangelogPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ChangelogPath)) {
    $ChangelogPath = Join-Path $root 'CHANGELOG.md'
}

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "CHANGELOG not found: $ChangelogPath"
}

$lines = Get-Content -LiteralPath $ChangelogPath -Encoding UTF8
$escaped = [regex]::Escape($Version)
$start = -1
$end = $lines.Count

for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## \[$escaped\](?:\s|$)") {
        $start = $i + 1
        continue
    }
    if ($start -ge 0 -and $lines[$i] -match '^## \[') {
        $end = $i
        break
    }
}

if ($start -lt 0) {
    throw "CHANGELOG.md has no section for version $Version"
}

$body = $lines[$start..($end - 1)]
# Trim blank lines at both ends without mutating the source array.
$first = 0
while ($first -lt $body.Count -and [string]::IsNullOrWhiteSpace($body[$first])) { $first++ }
$last = $body.Count - 1
while ($last -ge $first -and [string]::IsNullOrWhiteSpace($body[$last])) { $last-- }

if ($last -lt $first) {
    throw "CHANGELOG section for $Version is empty"
}

$text = ($body[$first..$last] -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $text -Encoding UTF8 -NoNewline
    Write-Host "Release notes written to $OutputPath" -ForegroundColor Green
}
else {
    Write-Output $text
}
