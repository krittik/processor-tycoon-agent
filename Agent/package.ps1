# Packaging only: builds a Release plugin and a self-contained win-x64 CLI into artifacts/dist/ as a zip that extracts into the game folder.
# Does not touch the installed game, saves or configuration, and includes no development files. BepInEx 5 is a separate prerequisite.
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\common.ps1')
$game = Resolve-GameDir $GameDir
$pluginProject = Join-Path $PSScriptRoot 'src\ProcessorTycoon.Mod\ProcessorTycoon.Mod.csproj'
$version = Get-ProjectVersion $pluginProject
$stage = Join-Path $ArtifactsDir ('dist\ProcessorTycoon-Agent-' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$toolsDirectory = Join-Path $stage 'tools'
$pluginDirectory = Join-Path $stage 'BepInEx\plugins\ProcessorTycoon.Mod'
New-Item -ItemType Directory -Path $toolsDirectory, $pluginDirectory | Out-Null
dotnet build $pluginProject -c Release "-p:GameDir=$game" -p:SkipDeploy=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$pluginBin = Join-Path $PSScriptRoot 'src\ProcessorTycoon.Mod\bin\Release\netstandard2.1'
foreach ($name in @('ProcessorTycoon.Mod.dll', 'Newtonsoft.Json.dll')) { Copy-Item -LiteralPath (Join-Path $pluginBin $name) -Destination $pluginDirectory }
dotnet publish (Join-Path $PSScriptRoot 'src\ProcessorTycoon.Cli\ProcessorTycoon.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $toolsDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package\pt-agent.cmd'), (Join-Path $PSScriptRoot 'package\AGENTS.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PLAYER_START.md') -Destination (Join-Path $stage 'START-HERE.md')
# Licenses: the mod's own, and the notices of what it redistributes (Newtonsoft.Json; the .NET runtime inside the self-contained CLI).
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $pluginDirectory 'LICENSE.txt')
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
Copy-Item -LiteralPath (Join-Path $nuget 'newtonsoft.json\13.0.3\LICENSE.md') -Destination (Join-Path $pluginDirectory 'Newtonsoft.Json-LICENSE.md')
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $toolsDirectory 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $toolsDirectory 'dotnet-ThirdPartyNotices.txt')
New-DistZip $stage
