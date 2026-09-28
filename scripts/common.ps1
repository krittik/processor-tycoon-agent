# Shared helpers for build/package scripts. Dot-source it: . (Join-Path $PSScriptRoot '..\scripts\common.ps1')
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'

# Game folder: explicit value > PT_GAME_DIR > the folder containing this repository (same rule as Directory.Build.props).
function Resolve-GameDir([string]$GameDir) {
    if (-not $GameDir) { $GameDir = $env:PT_GAME_DIR }
    if (-not $GameDir) { $GameDir = Join-Path $RepoRoot '..' }
    $full = [IO.Path]::GetFullPath($GameDir)
    if (-not (Test-Path -LiteralPath (Join-Path $full 'Processor Tycoon Beta.exe'))) { throw "Processor Tycoon not found at '$full'. Set PT_GAME_DIR or pass -GameDir." }
    $full
}

function Get-ProjectVersion([string]$ProjectPath) {
    $version = ([xml](Get-Content -LiteralPath $ProjectPath)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "No <Version> in $ProjectPath" }
    $version
}

# Zips a staged folder whose layout mirrors the game folder; returns the zip path.
# Entries use '/' separators (Compress-Archive on Windows PowerShell writes '\', which breaks extraction on Linux/macOS).
function New-DistZip([string]$StageDirectory) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = $StageDirectory + '.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    $root = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $StageDirectory -Recurse -File) {
            $entry = $file.FullName.Substring($root.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    $zip
}
