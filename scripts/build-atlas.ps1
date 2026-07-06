#Requires -Version 5.1
<#
.SYNOPSIS
    Packs one level's PNG images (legacy/images/<Level>/) into a single Phaser
    texture atlas (WebP + JSON) via scripts/asset-tools/build-atlas.mjs.
.PARAMETER Level
    Level name, e.g. "airplane" - reads from legacy/images/<Level>.
.PARAMETER Source
    Explicit source directory of PNGs, overrides -Level.
.PARAMETER Destination
    Output path without extension. Defaults to
    web/public/assets/images/<Level>/atlas (requires -Level).
.PARAMETER Padding
    Pixels kept between packed sprites. Default 2.
.PARAMETER MaxSize
    Max atlas page dimension in pixels. Default 2048.
.PARAMETER Quality
    WebP quality (0-100) for the final atlas image. Default 90.
#>
param(
    [string]$Level,
    [string]$Source,
    [string]$Destination,
    [int]$Padding = 2,
    [int]$MaxSize = 2048,
    [int]$Quality = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolDir = Join-Path $PSScriptRoot "asset-tools"

if (-not $Source) {
    if (-not $Level) { throw "Specify -Level <name> or -Source <dir>." }
    $Source = Join-Path $repoRoot "legacy\images\$Level"
}
if (-not $Destination) {
    if (-not $Level) { throw "Specify -Level <name> or -Destination <path>." }
    $Destination = Join-Path $repoRoot "web\public\assets\images\$Level\atlas"
}

if (-not (Test-Path (Join-Path $toolDir "node_modules"))) {
    Write-Host "Installing asset-tools dependencies..."
    Push-Location $toolDir
    try { npm install } finally { Pop-Location }
}

node (Join-Path $toolDir "build-atlas.mjs") `
    --source $Source `
    --output $Destination `
    --padding $Padding `
    --max-size $MaxSize `
    --quality $Quality
