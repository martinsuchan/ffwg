#Requires -Version 5.1
<#
.SYNOPSIS
    Launches the web port's dev server (web/npm run dev).
.PARAMETER NoOpen
    Don't automatically open a browser tab.
.PARAMETER Install
    Force `npm install` even if web/node_modules already exists.
.PARAMETER Port
    Dev-server port. Must match web/vite.config.ts's server.port (pinned so
    localStorage - solved levels/saves, scoped per origin incl. port - survives
    restarts). If a server is already listening here, this script attaches to it
    (opens a browser tab) instead of failing on the strictPort conflict.
#>
param(
    [switch]$NoOpen,
    [switch]$Install,
    [int]$Port = 5173
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot "web"

if (-not (Test-Path $webDir)) {
    throw "web/ directory not found at $webDir"
}

# If a dev server is already listening on the pinned port, don't try to start a
# second one (vite's strictPort would just error "port already in use") - a Node
# process can't be "attached" to anyway. Just open a tab pointing at it.
$alreadyRunning = $false
try {
    $alreadyRunning = $null -ne (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}
catch {
    # Get-NetTCPConnection unavailable - fall through and let vite try to start.
}
if ($alreadyRunning) {
    $url = "http://localhost:$Port/"
    Write-Host "Dev server already running at $url - attaching (nothing to start)."
    if (-not $NoOpen) {
        Start-Process $url
    }
    return
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
