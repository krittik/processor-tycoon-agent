# Creates a disposable game copy in artifacts/testbeds/<Name> from the clean official game zip, adds BepInEx 5 and optionally the Agent mod.
# Testbeds are throwaway: delete or recreate them freely (-Force). The main game folder is only read (as the BepInEx source).
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$GameZip = $env:PT_GAME_ZIP,   # official game zip (source of truth)
    [string]$BepInExFrom,                  # folder containing a BepInEx 5 install; default: the resolved game folder
    [int]$AgentPort = 0,                   # > 0: install the Agent package and set its API port (main game uses 17616)
    [string]$AgentZip,                     # default: newest artifacts/dist/ProcessorTycoon-Agent-*.zip
    [switch]$Force                         # replace an existing testbed with this name
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $GameZip -or -not (Test-Path -LiteralPath $GameZip)) { throw 'Pass -GameZip or set PT_GAME_ZIP to the official game zip.' }
$testbeds = Join-Path $ArtifactsDir 'testbeds'
$root = Join-Path $testbeds $Name
$marker = Join-Path $root 'TESTBED.md'
if (Test-Path -LiteralPath $root) {
    if (-not $Force) { throw "Testbed '$Name' exists. Use -Force to recreate it." }
    if (-not (Test-Path -LiteralPath $marker)) { throw "'$root' has no TESTBED.md marker; refusing to delete it." }
    Remove-Item -Recurse -Force -LiteralPath $root
}

# Game files: the zip contains one top-level folder with the game.
$staging = Join-Path $testbeds ('.staging-' + $Name)
if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
[IO.Compression.ZipFile]::ExtractToDirectory($GameZip, $staging)
$inner = Get-ChildItem -LiteralPath $staging -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Processor Tycoon Beta.exe') } | Select-Object -First 1
if (-not $inner) { throw "No 'Processor Tycoon Beta.exe' found in $GameZip." }
Move-Item -LiteralPath $inner.FullName -Destination $root
Remove-Item -Recurse -Force -LiteralPath $staging

# BepInEx 5: loader files in the game root plus BepInEx/core. Configs, logs, caches and plugins are not copied.
if (-not $BepInExFrom) { $BepInExFrom = Resolve-GameDir '' }
if (-not (Test-Path -LiteralPath (Join-Path $BepInExFrom 'BepInEx\core\BepInEx.dll'))) { throw "No BepInEx 5 install in '$BepInExFrom'." }
foreach ($file in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) {
    $source = Join-Path $BepInExFrom $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $root }
}
New-Item -ItemType Directory -Force -Path (Join-Path $root 'BepInEx\plugins'), (Join-Path $root 'BepInEx\config') | Out-Null
Copy-Item -Recurse -LiteralPath (Join-Path $BepInExFrom 'BepInEx\core') -Destination (Join-Path $root 'BepInEx')

$agentLine = 'not installed'
if ($AgentPort -gt 0) {
    if (-not $AgentZip) { $AgentZip = Get-ChildItem (Join-Path $ArtifactsDir 'dist') -Filter 'ProcessorTycoon-Agent-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }
    if (-not $AgentZip) { throw 'No Agent package found; run Agent\package.ps1 or pass -AgentZip.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($AgentZip, $root)
    # BepInEx keeps this value and fills in the other defaults on first launch.
    Set-Content -LiteralPath (Join-Path $root 'BepInEx\config\local.processortycoon.mod.cfg') -Encoding UTF8 -Value "[Api]`r`n`r`nPort = $AgentPort"
    $agentLine = "$(Split-Path $AgentZip -Leaf), API port $AgentPort"
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $root 'Processor Tycoon Beta_Data\Managed\Assembly-CSharp.dll')).Hash.Substring(0, 16)
Set-Content -LiteralPath $marker -Encoding UTF8 -Value @"
# Disposable testbed '$Name'

Created $(Get-Date -Format 'yyyy-MM-dd HH:mm') by scripts/new-testbed.ps1. Delete or recreate freely.

- Game: $(Split-Path $GameZip -Leaf) (Assembly-CSharp SHA-256 $hash...)
- BepInEx: copied from $BepInExFrom
- Agent mod: $agentLine
- Saves are NOT isolated: every copy uses the same per-user save folder as the main game.
"@
Write-Output $root
