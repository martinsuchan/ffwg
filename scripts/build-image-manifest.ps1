#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerates web/public/lua/image-manifest.json, the list of every real
    image path under legacy/images/ (in the "images/<...>" form Lua's
    file_exists() receives) that web/src/lua/levelLoader.ts uses to answer
    file_exists() for real instead of always returning false.
.PARAMETER Source
    Directory of images to scan. Defaults to legacy/images.
.PARAMETER Destination
    Output manifest path. Defaults to web/public/lua/image-manifest.json.
#>
param(
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot "web"

if (-not $Source) { $Source = Join-Path $repoRoot "legacy\images" }
if (-not $Destination) { $Destination = Join-Path $webDir "public\lua\image-manifest.json" }

if (-not (Test-Path (Join-Path $webDir "node_modules"))) {
    Write-Host "Installing web/ dependencies..."
    Push-Location $webDir
    try { npm install } finally { Pop-Location }
}

Push-Location $webDir
try {
    node "tools\build-image-manifest.mjs" $Source $Destination
}
finally {
    Pop-Location
}
