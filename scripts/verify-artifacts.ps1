# Verifies release archives and their SHA-256 manifest before publishing.
#
# Usage:
#   pwsh ./scripts/verify-artifacts.ps1 -Version 3.3.3
#   pwsh ./scripts/verify-artifacts.ps1 -Version 3.3.3 -Directory dist/3.3.3
#
# Checks:
#   - both archives and the manifest exist;
#   - every manifest hash matches the file on disk;
#   - the plugin-only archive contains the two mod DLLs;
#   - the full archive contains BepInEx, the two mod DLLs and the injector files;
#   - archive paths are safe and unique on Windows;
#   - no proprietary game assemblies leaked into either archive.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Directory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($Directory)) {
    $Directory = Join-Path $root "dist/$Version"
}
$Directory = (Resolve-Path -LiteralPath $Directory).Path

$failures = New-Object System.Collections.Generic.List[string]

function Check {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  [ok]   $Message" -ForegroundColor Green
    }
    else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $failures.Add($Message)
    }
}

$fullZip = Join-Path $Directory "SilksongCoop_v${Version}.zip"
$pluginZip = Join-Path $Directory "SilksongCoop_v${Version}_plugin-only.zip"
$manifest = Join-Path $Directory "SilksongCoop_v${Version}_SHA256SUMS.txt"

Write-Host "==> Expected artifacts ($Version)" -ForegroundColor Cyan
Check (Test-Path -LiteralPath $fullZip) "full archive exists"
Check (Test-Path -LiteralPath $pluginZip) "plugin-only archive exists"
Check (Test-Path -LiteralPath $manifest) "SHA-256 manifest exists"

if ($failures.Count -gt 0) {
    Write-Host "Artifact verification failed." -ForegroundColor Red
    exit 1
}

Write-Host '==> SHA-256 manifest' -ForegroundColor Cyan
$expectedNames = @([IO.Path]::GetFileName($fullZip), [IO.Path]::GetFileName($pluginZip))
$verifiedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath $manifest -Encoding UTF8) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $match = [regex]::Match($line, '^([0-9A-Fa-f]{64})\s+\*?(.+?)\s*$')
    if (-not $match.Success) {
        Check $false "malformed manifest line: $line"
        continue
    }

    $expected = $match.Groups[1].Value.ToUpperInvariant()
    $name = $match.Groups[2].Value
    if ($expectedNames -cnotcontains $name -or -not $verifiedNames.Add($name)) {
        Check $false "unexpected or duplicate manifest entry: $name"
        continue
    }
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path)) {
        Check $false "manifest entry missing on disk: $name"
        continue
    }

    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    Check ($actual -eq $expected) "SHA-256 matches for $name"
}

Check ($verifiedNames.SetEquals([string[]]$expectedNames)) "manifest lists exactly both expected archives"

Write-Host '==> Archive contents' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-EntryNames {
    param([string]$ZipPath)
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $seen = [System.Collections.Generic.Dictionary[string,bool]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $canonical = $name.TrimEnd('/')
            $segments = $canonical.Split('/')
            $unsafe = $name.EndsWith('//') -or $segments -contains '' -or ($segments | Where-Object {
                $_ -eq '.' -or $_ -eq '..' -or
                $_ -match '^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)' -or $_ -match '[<>:"|?*\x00-\x1F]|[. ]$'
            })
            $symlink = (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000
            if ($unsafe -or $symlink) {
                Check $false "unsafe ZIP entry in $([IO.Path]::GetFileName($ZipPath)): $name"
                continue
            }
            if ($seen.ContainsKey($canonical)) {
                Check $false "duplicate ZIP entry in $([IO.Path]::GetFileName($ZipPath)): $name"
                continue
            }
            $seen.Add($canonical, $name.EndsWith("/"))
            $name
        }
        foreach ($name in $seen.Keys) {
            $ancestor = $name
            while (($slash = $ancestor.LastIndexOf('/')) -ge 0) {
                $ancestor = $ancestor.Substring(0, $slash)
                if ($seen.ContainsKey($ancestor) -and -not $seen[$ancestor]) {
                    Check $false "file used as ZIP directory in $([IO.Path]::GetFileName($ZipPath)): $ancestor"
                    break
                }
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

$pluginEntries = Get-EntryNames $pluginZip
$fullEntries = Get-EntryNames $fullZip

Check ($pluginEntries -contains 'SilksongCoop/SilksongCoop.Mod.dll') 'plugin-only has SilksongCoop.Mod.dll'
Check ($pluginEntries -contains 'SilksongCoop/SilksongCoop.Core.dll') 'plugin-only has SilksongCoop.Core.dll'
Check ($pluginEntries -contains 'README.md') 'plugin-only has README.md'
Check ($pluginEntries -contains 'CHANGELOG.md') 'plugin-only has CHANGELOG.md'

Check ($fullEntries -contains 'BepInEx/plugins/SilksongCoop/SilksongCoop.Mod.dll') 'full archive has the mod DLL'
Check ($fullEntries -contains 'BepInEx/plugins/SilksongCoop/SilksongCoop.Core.dll') 'full archive has the core DLL'
Check ($fullEntries -contains 'winhttp.dll') 'full archive has the injector (winhttp.dll)'
Check ($fullEntries -contains 'doorstop_config.ini') 'full archive has doorstop_config.ini'
Check ($fullEntries -contains 'BepInEx/core/BepInEx.dll') 'full archive has BepInEx core'

$forbidden = @(
    'Assembly-CSharp.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'TeamCherry.TK2D.dll'
)
$leaked = @(
    @($pluginEntries + $fullEntries) |
        Where-Object { $forbidden -contains [IO.Path]::GetFileName($_) }
)
Check ($leaked.Count -eq 0) 'no proprietary game assemblies in archives'
foreach ($entry in $leaked) {
    Write-Host "         leaked: $entry" -ForegroundColor Red
}

if ($failures.Count -gt 0) {
    Write-Host "Artifact verification failed with $($failures.Count) error(s)." -ForegroundColor Red
    exit 1
}

Write-Host "Artifact verification passed for $Version." -ForegroundColor Green
