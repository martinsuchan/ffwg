#Requires -Version 5.1
<#
.SYNOPSIS
    Launches the web port's dev server (web/npm run dev).
.PARAMETER NoOpen
    Don't automatically open a browser tab.
.PARAMETER Install
    Force `npm install` even if web/node_modules already exists.
#>
param(
    [switch]$NoOpen,
    [switch]$Install
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot "web"

if (-not (Test-Path $webDir)) {
    throw "web/ directory not found at $webDir"
}

Push-Location $webDir
try {
    if ($Install -or -not (Test-Path (Join-Path $webDir "node_modules"))) {
        Write-Host "Installing dependencies..."
        npm install
    }

    $imageManifest = Join-Path $webDir "public\lua\image-manifest.json"
    if (-not (Test-Path $imageManifest)) {
        Write-Host "Generating image manifest..."
        & (Join-Path $PSScriptRoot "build-image-manifest.ps1")
    }

    $devArgs = @("run", "dev")
    if (-not $NoOpen) {
        $devArgs += @("--", "--open")
    }

    & npm @devArgs
}
finally {
    Pop-Location
}
