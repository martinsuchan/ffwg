#Requires -Version 5.1
<#
.SYNOPSIS
    Export every level's physics geometry to solver/levels/*.json for the C# solver
    (Windows 11). See solver/docs/001.

    The export runs the real browser-side Lua loader (web/src/lua/levelLoader.ts)
    through Playwright, so it needs the dev server. If one isn't already running on
    the pinned port, this starts it, waits, exports, and stops it again.

    Run from the repository root:  scripts\export-levels.ps1

.PARAMETER Port
    Dev-server port (default 5173; must match web/vite.config.ts).
.PARAMETER OutDir
    Output directory (default <repo>\solver\levels).
.PARAMETER KeepServer
    If this script started the dev server, leave it running afterwards.

.NOTES
    Assets must already be built (run scripts\setup.ps1 once). Windows 11 only.
#>
param(
    [int]$Port = 5173,
    [string]$OutDir,
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

    Write-Step "Ensuring Playwright's Chromium is installed"
    Push-Location $webDir
    try { npx playwright install chromium | Out-Null } finally { Pop-Location }
    Write-Ok "Chromium ready."

    if (Test-DevServerUp -Port $Port) {
        Write-Step "Using the dev server already running on port $Port"
    }
    else {
        Write-Step "Starting the dev server on port $Port"
        # Launch Vite directly via node - see the same note in scripts\test.ps1.
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

    Write-Step "Exporting levels"
    Push-Location $webDir
    try {
        $env:FFWG_BASE_URL = "http://localhost:$Port/"
        if ($OutDir) { $env:FFWG_OUT_DIR = $OutDir } else { Remove-Item Env:\FFWG_OUT_DIR -ErrorAction SilentlyContinue }
        node "tools\export-levels.mjs"
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

if ($exitCode -eq 0) { Write-Ok "Levels exported." } else { Write-ErrLine "Export failed (exit $exitCode)." }
exit $exitCode
