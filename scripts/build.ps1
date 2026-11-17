#Requires -Version 5.1
<#
.SYNOPSIS
    Type-checks and builds the web port for production (web/npm run build).
.PARAMETER Install
    Force `npm install` even if web/node_modules already exists.
.PARAMETER Preview
    Serve the production build locally after a successful build (npm run preview).
#>
param(
    [switch]$Install,
    [switch]$Preview
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
        if ($LASTEXITCODE -ne 0) { throw "npm install failed (exit $LASTEXITCODE)" }
    }

    npm run build
    # A failed build must stop here - otherwise -Preview would serve a stale dist/.
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)" }

    if ($Preview) {
        npm run preview
    }
}
finally {
    Pop-Location
}
