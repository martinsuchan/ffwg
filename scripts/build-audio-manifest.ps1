#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerates web/public/lua/audio-manifest.json, the list of every real
    sound path (in the "sound/<...>" form Lua's file_exists() receives)
    found across the already-built web/public/assets/sound/**/sprite.json
    files, that web/src/lua/levelScript.ts and levelLoader.ts use to answer
    file_exists() for sound instead of always returning false. Run this
    *after* scripts/convert-assets.ps1 (or convert-assets.ps1 -Level ...) -
    it reads converted output, not legacy/sound/ directly.
.PARAMETER Source
    Directory of built sound sprites to scan. Defaults to
    web/public/assets/sound.
.PARAMETER Destination
    Output manifest path. Defaults to web/public/lua/audio-manifest.json.
#>
param(
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot "web"

if (-not $Source) { $Source = Join-Path $webDir "public\assets\sound" }
if (-not $Destination) { $Destination = Join-Path $webDir "public\lua\audio-manifest.json" }

if (-not (Test-Path (Join-Path $webDir "node_modules"))) {
    Write-Host "Installing web/ dependencies..."
    Push-Location $webDir
    try { npm install } finally { Pop-Location }
}

Push-Location $webDir
try {
    node "tools\build-audio-manifest.mjs" $Source $Destination
}
finally {
    Pop-Location
}
