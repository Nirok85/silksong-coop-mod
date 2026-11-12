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
    [string]$OutputDir = '',
    [string]$BepInExZip = $env:BEPINEX_ZIP,
    [string]$BepInExSha256 = $env:BEPINEX_SHA256,
    [switch]$SkipBundle,
    [switch]$AllowUnpinnedBepInEx,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# $PSScriptRoot is empty when the script is invoked via `powershell -File`, so it must
# not be used in a parameter default. Resolve the output directory here instead.
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'dist'
}
$bepInExVersion = '5.4.21'
$bepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$bepInExVersion/BepInEx_x64_$bepInExVersion.0.zip"
# SHA-256 of the official BepInEx_x64_5.4.21.0.zip release asset. Used only when the
# script downloads the archive itself; a caller-provided -BepInExZip must be pinned
# explicitly with -BepInExSha256/BEPINEX_SHA256.
$defaultBepInExSha256 = '2AF69FE0AAF821E95C4CD3E4D53860E667C54648F97DCA6F971A5BCD3C22AA34'

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

function Assert-SafeZip {
    param([string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $seen = [System.Collections.Generic.Dictionary[string,bool]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $canonical = $name.TrimEnd('/')
            $segments = $canonical.Split('/')
            $mode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            $unsafe = $name.Contains('\') -or $segments -contains '' -or ($segments | Where-Object {
                $_ -eq '.' -or $_ -eq '..' -or
                $_ -match '^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)' -or
                $_ -match '[<>:"|?*\x00-\x1F]|[. ]$'
            })
            if ($unsafe -or $mode -notin @(0, 0x8000, 0x4000)) {
                throw "Unsafe entry in $Path`: $name"
            }
            if ($seen.ContainsKey($canonical)) {
                throw "Duplicate entry in $Path`: $name"
            }
            $seen.Add($canonical, $name.EndsWith('/'))
        }
        foreach ($name in $seen.Keys) {
            $ancestor = $name
            while (($slash = $ancestor.LastIndexOf('/')) -ge 0) {
                $ancestor = $ancestor.Substring(0, $slash)
                if ($seen.ContainsKey($ancestor) -and -not $seen[$ancestor]) {
                    throw "File used as directory in $Path`: $ancestor"
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

# Compress-Archive on Windows stores backslashes in entry names, which some Unix
# extractors treat as literal filename characters. Build the archive explicitly so
# every entry uses the ZIP-standard forward slash.
function New-PortableZip {
    param([string]$SourceDir, [string]$DestinationPath)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $source = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\', '/')
    $archive = [System.IO.Compression.ZipFile]::Open(
        $DestinationPath,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = Get-ChildItem -LiteralPath $source -Recurse -File
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/').Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $relative,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Write-ChecksumManifest {
    param([string]$Directory, [string]$Version, [string[]]$Files)

    $hashes = Get-FileHash $Files -Algorithm SHA256
    $hashes |
        Select-Object @{ Name = 'File'; Expression = { Split-Path $_.Path -Leaf } }, Hash |
        Format-Table -AutoSize

    $manifest = Join-Path $Directory "SilksongCoop_v${Version}_SHA256SUMS.txt"
    $hashes | ForEach-Object {
        "$($_.Hash.ToLowerInvariant())  $(Split-Path $_.Path -Leaf)"
    } | Set-Content -LiteralPath $manifest -Encoding ASCII
    Write-Host "  -> $manifest"
}

function Copy-ReleaseDocs {
    param([string]$Destination)

    foreach ($doc in @('README.md', 'README.ru.md', 'CHANGELOG.md', 'ROADMAP.md')) {
        $path = Join-Path $root $doc
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Copy-Item -LiteralPath $path -Destination $Destination
        }
    }
    $docsDir = Join-Path $root 'docs'
    if (Test-Path -LiteralPath $docsDir -PathType Container) {
        Copy-Item -LiteralPath $docsDir -Destination (Join-Path $Destination 'docs') -Recurse
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
    Copy-ReleaseDocs $plugin
    Copy-Item (Join-Path $root 'LICENSE') $plugin
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $plugin
    Copy-Item (Join-Path $root 'LICENSES') (Join-Path $plugin 'LICENSES') -Recurse

    $install = Get-Content (Join-Path $root 'assets\INSTALL.txt') -Raw -Encoding UTF8
    $install = $install.Replace('__VERSION__', $version)
    [System.IO.File]::WriteAllText((Join-Path $plugin 'INSTALL.txt'), $install, (New-Object System.Text.UTF8Encoding($false)))

    $pluginZip = Join-Path $OutputDir "SilksongCoop_v${version}_plugin-only.zip"
    New-PortableZip -SourceDir $plugin -DestinationPath $pluginZip
    Write-Host "  -> $pluginZip"

    if ($SkipBundle) {
        Write-Host '==> -SkipBundle: self-contained package skipped' -ForegroundColor Yellow
        Write-Host '==> SHA256' -ForegroundColor Cyan
        Write-ChecksumManifest -Directory $OutputDir -Version $version -Files @($pluginZip)
        return
    }

    # ---------------- self-contained (Windows) package ----------------
    $bepInExWasProvided = -not [string]::IsNullOrWhiteSpace($BepInExZip)
    if (-not $bepInExWasProvided) {
        $BepInExZip = Join-Path $work "BepInEx_x64_$bepInExVersion.0.zip"
        Write-Host "==> Downloading BepInEx $bepInExVersion" -ForegroundColor Cyan
        Invoke-WebRequest -Uri $bepInExUrl -OutFile $BepInExZip -UseBasicParsing
    }
    Assert-File $BepInExZip

    $bepInExHash = (Get-FileHash -LiteralPath $BepInExZip -Algorithm SHA256).Hash
    Write-Host "    BepInEx SHA256: $bepInExHash"

    $expectedBepInExHash = if (-not [string]::IsNullOrWhiteSpace($BepInExSha256)) {
        $BepInExSha256
    }
    elseif (-not $bepInExWasProvided) {
        $defaultBepInExSha256
    }
    elseif ($AllowUnpinnedBepInEx) {
        ''
    }
    else {
        throw 'A caller-provided -BepInExZip requires -BepInExSha256 (or BEPINEX_SHA256). Use -AllowUnpinnedBepInEx only for local tests, never for a release.'
    }

    if (-not [string]::IsNullOrWhiteSpace($expectedBepInExHash)) {
        if ($bepInExHash -ne $expectedBepInExHash.ToUpperInvariant()) {
            throw "BepInEx SHA256 mismatch: expected $($expectedBepInExHash.ToUpperInvariant()), got $bepInExHash"
        }
        Write-Host '  [ok]   BepInEx SHA256 matches the pinned value'
    }
    else {
        Write-Host '  [warn] BepInEx SHA256 is not pinned (-AllowUnpinnedBepInEx); do not publish this archive' -ForegroundColor Yellow
    }

    Assert-SafeZip $BepInExZip

    $win = Join-Path $work 'win'
    New-Item -ItemType Directory -Path $win -Force | Out-Null
    Expand-Archive -Path $BepInExZip -DestinationPath $win -Force

    New-Item -ItemType Directory -Path (Join-Path $win 'BepInEx\plugins\SilksongCoop') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $win 'BepInEx\config') -Force | Out-Null
    Copy-Item $modDll (Join-Path $win 'BepInEx\plugins\SilksongCoop\SilksongCoop.Mod.dll')
    Copy-Item $coreDll (Join-Path $win 'BepInEx\plugins\SilksongCoop\SilksongCoop.Core.dll')
    Copy-Item (Join-Path $root 'assets\com.silksong.coop.cfg') (Join-Path $win 'BepInEx\config')
    Copy-ReleaseDocs $win
    Copy-Item (Join-Path $root 'LICENSE') $win
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $win
    Copy-Item (Join-Path $root 'LICENSES') (Join-Path $win 'LICENSES') -Recurse

    $fullZip = Join-Path $OutputDir "SilksongCoop_v$version.zip"
    New-PortableZip -SourceDir $win -DestinationPath $fullZip
    Write-Host "  -> $fullZip"

    Write-Host '==> SHA256' -ForegroundColor Cyan
    Write-ChecksumManifest -Directory $OutputDir -Version $version -Files @($pluginZip, $fullZip)
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '==> Done' -ForegroundColor Green
