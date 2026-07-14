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
.PARAMETER Sandbox
    Open /sandbox (every level unlocked + reference solutions) instead of the
    standard game at / (docs/045).
#>
param(
    [switch]$NoOpen,
    [switch]$Install,
    [int]$Port = 5173,
    [switch]$Sandbox
)

$ErrorActionPreference = "Stop"

# Which path to open in the browser: / is the standard, progression-gated game;
# /sandbox unlocks everything (docs/045). The dev server serves both.
$openPath = if ($Sandbox) { "/sandbox" } else { "/" }

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
    $url = "http://localhost:$Port$openPath"
    Write-Host "Dev server already running - attaching (nothing to start). Opening $url"
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
        # vite --open <path> opens the browser straight at that route.
        $devArgs += @("--", "--open", $openPath)
    }

    & npm @devArgs
}
finally {
    Pop-Location
}
