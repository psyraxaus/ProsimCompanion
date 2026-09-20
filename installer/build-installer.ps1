# Builds the ProsimCompanion installer (roadmap Phase 7).
#
#   .\installer\build-installer.ps1 [-Configuration Release] [-IsccPath <path to ISCC.exe>]
#                                   [-SkipStreamDeck]
#
# Steps: publish the app (framework-dependent win-x64; the .NET 10 Desktop Runtime is a
# documented prerequisite), VERIFY ProSimSDK.dll never made it into the payload, build and
# pack the Stream Deck plugin (Node 20+; -SkipStreamDeck omits it from the payload), read the
# version from Directory.Build.props, and compile ProsimCompanion.iss with Inno Setup 6.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$IsccPath = '',
    [switch]$SkipStreamDeck
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\ProsimCompanion.App\ProsimCompanion.App.csproj'
$publishDir = Join-Path $repoRoot "src\ProsimCompanion.App\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\publish"

Write-Host "Publishing $appProject ($Configuration, win-x64, framework-dependent)..."
dotnet publish $appProject -c $Configuration -r win-x64 --self-contained false -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The hard rule: ProSimSDK.dll is ProSim-AR's property and must never ship. The project
# references it with Private=false so it should never be copied — this guard catches any
# regression (a changed csproj, a stray manual copy) before an installer can be produced.
$sdkLeak = Get-ChildItem -Recurse -Filter 'ProSimSDK.dll' $publishDir -ErrorAction SilentlyContinue
if ($sdkLeak) {
    throw "ABORT: ProSimSDK.dll found in the publish output ($($sdkLeak[0].FullName)) - it must never be redistributed."
}
Write-Host 'Verified: ProSimSDK.dll is not in the payload.'

# Airline logos are the pilot's own files (trademarks; owner decision 2026-09-20). They live in
# %LOCALAPPDATA% and must never ride the seed config folder into the installer.
$logoLeak = Get-ChildItem -Recurse -File (Join-Path $publishDir 'config\themes\logos') -ErrorAction SilentlyContinue
if ($logoLeak) {
    throw "ABORT: theme logo found in the publish output ($($logoLeak[0].FullName)) - logos are user content and must never be redistributed."
}
Write-Host 'Verified: no theme logos in the payload.'

# Stream Deck plugin: bundle the TypeScript source and pack the .sdPlugin so the installer
# can (optionally) drop it into the Elgato plugins folder and ship the .streamDeckPlugin for
# manual installs. The plugin never ships without bin\plugin.js — a source-only copy would
# install as a dead key set.
$streamDeckDir = Join-Path $repoRoot 'streamdeck'
$sdPluginDir = Join-Path $streamDeckDir 'com.prosimcompanion.streamdeck.sdPlugin'
if (-not $SkipStreamDeck) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw 'Node.js not found - install Node 20+ or pass -SkipStreamDeck to build without the Stream Deck plugin.'
    }
    Write-Host 'Building Stream Deck plugin...'
    Push-Location $streamDeckDir
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed (streamdeck)' }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Stream Deck plugin build failed' }
        npx --yes --package @elgato/cli streamdeck pack com.prosimcompanion.streamdeck.sdPlugin --output dist --force
        if ($LASTEXITCODE -ne 0) { throw 'Stream Deck plugin pack failed' }
    }
    finally { Pop-Location }
    if (-not (Test-Path (Join-Path $sdPluginDir 'bin\plugin.js'))) {
        throw 'ABORT: streamdeck build produced no bin\plugin.js - the plugin payload would be broken.'
    }
    Write-Host 'Stream Deck plugin built and packed.'
}

# Version from the single source of truth.
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> found in Directory.Build.props' }
Write-Host "Version: $version"

if (-not $IsccPath) {
    $candidates = @(
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $IsccPath) { throw 'ISCC.exe not found - install Inno Setup 6 or pass -IsccPath' }
}

$isccArgs = @("/DAppVersion=$version", "/DPublishDir=$publishDir")
if ($SkipStreamDeck) { $isccArgs += '/DNoStreamDeck' }
& $IsccPath @isccArgs (Join-Path $PSScriptRoot 'ProsimCompanion.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Write-Host "Installer written to installer\Output\ProsimCompanion-Setup-$version.exe"
