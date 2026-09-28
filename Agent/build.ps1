# Builds the plugin (Debug) and deploys it into the game folder, then publishes the CLI into the game's tools/.
# -NoDeploy builds and publishes into artifacts/ instead, leaving the installed game untouched.
param([string]$GameDir, [switch]$NoDeploy)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\common.ps1')
$game = Resolve-GameDir $GameDir
$deployArgs = @("-p:GameDir=$game")
$toolsDirectory = Join-Path $game 'tools'
if ($NoDeploy) {
    $deployArgs += '-p:SkipDeploy=true'
    $toolsDirectory = Join-Path $ArtifactsDir 'agent\tools'
}
dotnet build (Join-Path $PSScriptRoot 'src\ProcessorTycoon.Mod\ProcessorTycoon.Mod.csproj') -c Debug @deployArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $PSScriptRoot 'src\ProcessorTycoon.Cli\ProcessorTycoon.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $toolsDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($name in @('pt-agent.dll', 'pt-agent.deps.json', 'pt-agent.runtimeconfig.json', 'pt-agent.pdb')) {
    $obsolete = Join-Path $toolsDirectory $name
    if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete }
}
exit 0
