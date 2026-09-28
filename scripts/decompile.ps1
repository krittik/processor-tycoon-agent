# Decompiles the game's Assembly-CSharp.dll into reference/decomp/<game version>/ for reading only (ignored by git; never commit or ship it).
# One folder per game version, so a game update can be diffed against the previous decompilation.
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$game = Resolve-GameDir $GameDir
$staging = Join-Path $ArtifactsDir 'decomp-staging'
if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
Push-Location $RepoRoot
try {
    dotnet tool restore | Out-Null
    dotnet ilspycmd -p -o $staging (Join-Path $game 'Processor Tycoon Beta_Data\Managed\Assembly-CSharp.dll')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
# The game writes its version into saves from a literal in SaveHandler.Save.
$saveHandler = Get-Content -Raw -LiteralPath (Join-Path $staging 'ProcessorTycoon.Save\SaveHandler.cs')
$version = if ($saveHandler -match 'string gameVersion = "([^"]+)"') { $Matches[1] } else { 'unknown-' + (Get-Date -Format 'yyyyMMdd') }
$out = Join-Path $RepoRoot ('reference\decomp\' + $version)
if (Test-Path -LiteralPath $out) { Remove-Item -Recurse -Force -LiteralPath $out }
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
Move-Item -LiteralPath $staging -Destination $out
Write-Output $out
