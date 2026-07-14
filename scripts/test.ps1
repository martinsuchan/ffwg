#Requires -Version 5.1
<#
.SYNOPSIS
    Run the e2e regression suite (web/tests) against the dev server (Windows 11).

    If the dev server isn't already running on the pinned port, this starts it,
    waits for it, runs the suite, and stops it again. If one is already running,
    it's reused and left running.

    Run from the repository root:  scripts\test.ps1

.PARAMETER Filter
    Only run test cases whose filename contains this substring (e.g. "pedometer",
    or "06"). Passed to the runner as FFWG_TEST.
.PARAMETER Port
    Dev-server port (default 5173; must match web/vite.config.ts).
.PARAMETER KeepServer
    If this script started the dev server, leave it running afterwards.

.NOTES
    Assets must already be built (run scripts\setup.ps1 once). Windows 11 only.
#>
param(
    [string]$Filter,
    [int]$Port = 5173,
    [switch]$KeepServer
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot "web"
. (Join-Path $PSScriptRoot "lib\common.ps1")

function Test-DevServerUp {
    param([int]$Port)
    try {
        Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -TimeoutSec 2 | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

$startedServer = $false
$exitCode = 1

try {
    if (-not (Test-Path (Join-Path $webDir "node_modules\playwright"))) {
        Write-Step "Installing web dependencies (incl. Playwright)"
        Push-Location $webDir
        try { npm install } finally { Pop-Location }
    }

    # Playwright's browser binary (cached per-user; no-op if already present).
    Write-Step "Ensuring Playwright's Chromium is installed"
    Push-Location $webDir
    try { npx playwright install chromium | Out-Null } finally { Pop-Location }
    Write-Ok "Chromium ready."

    if (-not (Test-Path (Join-Path $webDir "public\lua\image-manifest.json"))) {
        Write-WarnLine "Assets don't look built yet - run scripts\setup.ps1 first if the suite fails to load the map."
    }

    if (Test-DevServerUp -Port $Port) {
        Write-Step "Using the dev server already running on port $Port"
    }
    else {
        Write-Step "Starting the dev server on port $Port"
        # Launch Vite directly via node (not `npm run dev`): under a hidden
        # Start-Process window the npm.cmd wrapper exits without keeping Vite
        # alive, whereas the node process binds the port and stays up (and
        # stop.ps1 kills it by port).
        $viteBin = Join-Path $webDir "node_modules\vite\bin\vite.js"
        Start-Process -FilePath "node" -ArgumentList $viteBin -WorkingDirectory $webDir -WindowStyle Hidden | Out-Null
        $startedServer = $true
        $deadline = (Get-Date).AddSeconds(90)
        while (-not (Test-DevServerUp -Port $Port)) {
            if ((Get-Date) -gt $deadline) { throw "dev server did not come up on port $Port within 90s" }
            Start-Sleep -Milliseconds 500
        }
        Write-Ok "Dev server is up."
    }

    Write-Step "Running the e2e suite"
    Push-Location $webDir
    try {
        if ($Filter) { $env:FFWG_TEST = $Filter } else { Remove-Item Env:\FFWG_TEST -ErrorAction SilentlyContinue }
        $env:FFWG_BASE_URL = "http://localhost:$Port/"
        node "tests\run.mjs"
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($startedServer -and -not $KeepServer) {
        Write-Step "Stopping the dev server"
        & (Join-Path $PSScriptRoot "stop.ps1") -Port $Port
    }
}

if ($exitCode -eq 0) { Write-Ok "Suite passed." } else { Write-ErrLine "Suite failed (exit $exitCode)." }
exit $exitCode
