# Pre-release checks for the Silksong Co-op mod.
#
# Usage:
#   pwsh ./scripts/preflight-release.ps1                 # build + tests + consistency checks
#   pwsh ./scripts/preflight-release.ps1 -SkipTests
#   pwsh ./scripts/preflight-release.ps1 -AllowDirty      # while preparing a commit
#
# Exits non-zero on the first failed check so it can gate a release.
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$AllowDirty,
    [switch]$NoRestore,
    [switch]$MetadataOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$failures = New-Object System.Collections.Generic.List[string]

function Read-ProjectVersion {
    param([string]$RelativePath)
    [xml]$project = Get-Content -Path (Join-Path $root $RelativePath) -Raw -Encoding UTF8
    $entry = $project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $entry -or -not $entry.Version) {
        throw "Could not read <Version> from $RelativePath"
    }
    return [string]$entry.Version
}

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

Write-Host '==> Version consistency' -ForegroundColor Cyan
$modVersion = Read-ProjectVersion 'src\SilksongCoop.Mod\SilksongCoop.Mod.csproj'
$coreVersion = Read-ProjectVersion 'src\SilksongCoop.Core\SilksongCoop.Core.csproj'
$pluginSource = Get-Content (Join-Path $root 'src\SilksongCoop.Mod\SilksongCoopPlugin.cs') -Raw -Encoding UTF8
$pluginMatch = [regex]::Match($pluginSource, 'PluginVersion\s*=\s*"([^"]+)"')

Write-Host "  project version: $modVersion"
Check ($modVersion -eq $coreVersion) "Core csproj version matches Mod ($modVersion)"
Check ($pluginMatch.Success -and $pluginMatch.Groups[1].Value -eq $modVersion) "PluginVersion matches csproj ($modVersion)"

foreach ($projectFile in @('src\SilksongCoop.Mod\SilksongCoop.Mod.csproj', 'src\SilksongCoop.Core\SilksongCoop.Core.csproj')) {
    [xml]$project = Get-Content (Join-Path $root $projectFile) -Raw -Encoding UTF8
    $entry = $project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    $assembly = [string]$entry.AssemblyVersion
    $file = [string]$entry.FileVersion
    Check ($assembly -eq "$modVersion.0") "$projectFile AssemblyVersion is $modVersion.0 ($assembly)"
    Check ($file -eq "$modVersion.0") "$projectFile FileVersion is $modVersion.0 ($file)"
}

Write-Host '==> Changelog and README' -ForegroundColor Cyan
$changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
Check ($changelog -match [regex]::Escape("## [$modVersion]")) "CHANGELOG.md has a [$modVersion] section"

$readme = Get-Content (Join-Path $root 'README.md') -Raw -Encoding UTF8
Check ($readme -match [regex]::Escape("Version-$modVersion")) "README.md badge mentions $modVersion"
Check ($readme -match [regex]::Escape("SilksongCoop_v$modVersion.zip")) "README.md install section mentions the release archive"

$readmeRu = Get-Content (Join-Path $root 'README.ru.md') -Raw -Encoding UTF8
Check ($readmeRu -match [regex]::Escape("Version-$modVersion")) "README.ru.md badge mentions $modVersion"
Check ($readmeRu -match [regex]::Escape("SilksongCoop_v$modVersion.zip")) "README.ru.md install section mentions the release archive"

$roadmap = Get-Content (Join-Path $root 'ROADMAP.md') -Raw -Encoding UTF8
Check ($roadmap -match [regex]::Escape("v$modVersion")) "ROADMAP.md mentions v$modVersion"

Write-Host '==> Working tree' -ForegroundColor Cyan
$status = & git -C $root status --porcelain
if ($AllowDirty) {
    Write-Host '  [skip] dirty tree allowed (-AllowDirty)' -ForegroundColor Yellow
}
else {
    Check ([string]::IsNullOrWhiteSpace(($status -join "`n"))) 'git working tree is clean'
}

if ($MetadataOnly) {
    if ($failures.Count -gt 0) { exit 1 }
    Write-Host "Release metadata verified for $modVersion."
    exit 0
}

Write-Host '==> Build (warnings as errors)'  -ForegroundColor Cyan
$buildArgs = @(
    (Join-Path $root 'SilksongCoop.sln'),
    '-c', 'Release',
    '-p:TreatWarningsAsErrors=true',
    '-v:minimal'
)
if ($NoRestore) { $buildArgs += '--no-restore' }

$buildSucceeded = $false
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) {
    $failures.Add("dotnet build failed with exit code $LASTEXITCODE")
    Write-Host '  [FAIL] build failed' -ForegroundColor Red
}
else {
    $buildSucceeded = $true
    Write-Host '  [ok]   build succeeded' -ForegroundColor Green
}

if ($SkipTests) {
    Write-Host '  [skip] tests (-SkipTests)' -ForegroundColor Yellow
}
elseif (-not $buildSucceeded) {
    # Running --no-build after a failed build would test stale binaries from an earlier
    # build and could print "tests passed" for code that no longer compiles.
    Write-Host '  [skip] tests skipped because the build failed' -ForegroundColor Yellow
}
else {
    Write-Host '==> Tests' -ForegroundColor Cyan
    & dotnet test (Join-Path $root 'SilksongCoop.sln') -c Release --no-build --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("dotnet test failed with exit code $LASTEXITCODE")
        Write-Host '  [FAIL] tests failed' -ForegroundColor Red
    }
    else {
        Write-Host '  [ok]   tests passed' -ForegroundColor Green
    }
}

if ($buildSucceeded -and -not [string]::IsNullOrWhiteSpace($env:ManagedDir)) {
    Write-Host '==> Game binding metadata' -ForegroundColor Cyan
    & dotnet run --project (Join-Path $root 'tools/SilksongCoop.BindingCheck') -c Release -- $env:ManagedDir
    if ($LASTEXITCODE -ne 0) { $failures.Add('game binding metadata check failed') }
}

Write-Host '==> Built DLL version' -ForegroundColor Cyan
$modDll = Join-Path $root 'src\SilksongCoop.Mod\bin\Release\netstandard2.1\SilksongCoop.Mod.dll'
$coreDll = Join-Path $root 'src\SilksongCoop.Core\bin\Release\netstandard2.1\SilksongCoop.Core.dll'
foreach ($dll in @($modDll, $coreDll)) {
    if (-not (Test-Path $dll)) {
        $failures.Add("Missing build output: $dll")
        Write-Host "  [FAIL] missing $dll" -ForegroundColor Red
        continue
    }
    $product = (Get-Item $dll).VersionInfo.ProductVersion
    Check ($product -like "$modVersion*") "$([System.IO.Path]::GetFileName($dll)) ProductVersion starts with $modVersion ($product)"
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Preflight failed with $($failures.Count) problem(s):" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host "Preflight passed for $modVersion." -ForegroundColor Green
Write-Host 'Next: commit, tag, push, then upload dist/ archives to the GitHub release.'
