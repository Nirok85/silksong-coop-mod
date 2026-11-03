# Builds the release artifacts for the Silksong Co-op mod on Windows.
#
# Usage:
#   pwsh ./scripts/package-release.ps1 -ManagedDir "D:\SteamLibrary\...\Hollow Knight Silksong_Data\Managed"
#   pwsh ./scripts/package-release.ps1 -ManagedDir ... -BepInExZip C:\downloads\BepInEx_x64_5.4.21.0.zip
#   pwsh ./scripts/package-release.ps1 -ManagedDir ... -SkipBundle
#
# Produces in dist/ (or -OutputDir):
#   SilksongCoop_v<version>.zip              -- Windows, self-contained (BepInEx 5.4.21 x64)
#   SilksongCoop_v<version>_plugin-only.zip  -- plugin DLLs only (Linux/macOS or existing BepInEx)
#
# The version is read from src/SilksongCoop.Mod/SilksongCoop.Mod.csproj.
[CmdletBinding()]
param(
    [string]$ManagedDir = $env:ManagedDir,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\dist'),
    [string]$BepInExZip = $env:BEPINEX_ZIP,
    [switch]$SkipBundle,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$bepInExVersion = '5.4.21'
$bepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$bepInExVersion/BepInEx_x64_$bepInExVersion.0.zip"

function Get-ProjectVersion {
    param([string]$Path)
    [xml]$project = Get-Content -Path $Path -Raw -Encoding UTF8
    $entry = $project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $entry -or -not $entry.Version) {
        throw "Could not read <Version> from $Path"
    }
    return [string]$entry.Version
}

function Assert-File {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing required file: $Path"
    }
}

if ([string]::IsNullOrWhiteSpace($ManagedDir)) {
    throw 'ManagedDir is not set. Pass -ManagedDir or set the ManagedDir environment variable.'
}

$version = Get-ProjectVersion (Join-Path $root 'src\SilksongCoop.Mod\SilksongCoop.Mod.csproj')
Write-Host "==> Building Silksong Co-op $version" -ForegroundColor Cyan
Write-Host "    Managed: $ManagedDir"

$buildArgs = @(
    (Join-Path $root 'SilksongCoop.sln'),
    '-c', 'Release',
    "-p:ManagedDir=$ManagedDir",
    '-p:TreatWarningsAsErrors=true'
)
if ($NoRestore) { $buildArgs += '--no-restore' }

& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$modBin = Join-Path $root 'src\SilksongCoop.Mod\bin\Release\netstandard2.1'
$coreBin = Join-Path $root 'src\SilksongCoop.Core\bin\Release\netstandard2.1'
$modDll = Join-Path $modBin 'SilksongCoop.Mod.dll'
$coreDll = Join-Path $coreBin 'SilksongCoop.Core.dll'
Assert-File $modDll
Assert-File $coreDll

if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$OutputDir = (Resolve-Path $OutputDir).Path

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("silksong-coop-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    # ---------------- plugin-only package ----------------
    $plugin = Join-Path $work 'plugin'
    New-Item -ItemType Directory -Path (Join-Path $plugin 'SilksongCoop') -Force | Out-Null
    Copy-Item $modDll (Join-Path $plugin 'SilksongCoop\SilksongCoop.Mod.dll')
    Copy-Item $coreDll (Join-Path $plugin 'SilksongCoop\SilksongCoop.Core.dll')
    Copy-Item (Join-Path $root 'assets\com.silksong.coop.cfg') $plugin
    Copy-Item (Join-Path $root 'README.md') $plugin
    Copy-Item (Join-Path $root 'LICENSE') $plugin
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $plugin
    Copy-Item (Join-Path $root 'LICENSES') (Join-Path $plugin 'LICENSES') -Recurse

    $install = Get-Content (Join-Path $root 'assets\INSTALL.txt') -Raw -Encoding UTF8
    $install = $install.Replace('__VERSION__', $version)
    [System.IO.File]::WriteAllText((Join-Path $plugin 'INSTALL.txt'), $install, (New-Object System.Text.UTF8Encoding($false)))

    $pluginZip = Join-Path $OutputDir "SilksongCoop_v${version}_plugin-only.zip"
    if (Test-Path $pluginZip) { Remove-Item $pluginZip -Force }
    Compress-Archive -Path (Join-Path $plugin '*') -DestinationPath $pluginZip -CompressionLevel Optimal
    Write-Host "  -> $pluginZip"

    if ($SkipBundle) {
        Write-Host '==> -SkipBundle: self-contained package skipped' -ForegroundColor Yellow
        return
    }

    # ---------------- self-contained (Windows) package ----------------
    if ([string]::IsNullOrWhiteSpace($BepInExZip)) {
        $BepInExZip = Join-Path $work "BepInEx_x64_$bepInExVersion.0.zip"
        Write-Host "==> Downloading BepInEx $bepInExVersion" -ForegroundColor Cyan
        Invoke-WebRequest -Uri $bepInExUrl -OutFile $BepInExZip -UseBasicParsing
    }
    Assert-File $BepInExZip

    $win = Join-Path $work 'win'
    New-Item -ItemType Directory -Path $win -Force | Out-Null
    Expand-Archive -Path $BepInExZip -DestinationPath $win -Force

    New-Item -ItemType Directory -Path (Join-Path $win 'BepInEx\plugins\SilksongCoop') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $win 'BepInEx\config') -Force | Out-Null
    Copy-Item $modDll (Join-Path $win 'BepInEx\plugins\SilksongCoop\SilksongCoop.Mod.dll')
    Copy-Item $coreDll (Join-Path $win 'BepInEx\plugins\SilksongCoop\SilksongCoop.Core.dll')
    Copy-Item (Join-Path $root 'assets\com.silksong.coop.cfg') (Join-Path $win 'BepInEx\config')
    Copy-Item (Join-Path $root 'README.md') $win
    Copy-Item (Join-Path $root 'LICENSE') $win
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $win
    Copy-Item (Join-Path $root 'LICENSES') (Join-Path $win 'LICENSES') -Recurse

    $fullZip = Join-Path $OutputDir "SilksongCoop_v$version.zip"
    if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
    Compress-Archive -Path (Join-Path $win '*') -DestinationPath $fullZip -CompressionLevel Optimal
    Write-Host "  -> $fullZip"

    Write-Host '==> SHA256' -ForegroundColor Cyan
    Get-FileHash $pluginZip, $fullZip -Algorithm SHA256 |
        Select-Object @{ Name = 'File'; Expression = { Split-Path $_.Path -Leaf } }, Hash |
        Format-Table -AutoSize
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '==> Done' -ForegroundColor Green
