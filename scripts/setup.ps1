#Requires -Version 5.1
<#
.SYNOPSIS
    One-command local setup + run for the Fish Fillets web port (Windows 11).

    Checks required tools, installs npm dependencies, converts all legacy game
    assets (images, music, sound, the intro movie) into the web-ready formats
    the game loads at runtime, builds the lookup manifests, and then starts the
    local dev server and opens the game in your browser.

    Run it from the repository root:  scripts\setup.ps1

    If PowerShell blocks the script ("running scripts is disabled"), start it
    with:   powershell -ExecutionPolicy Bypass -File scripts\setup.ps1

.PARAMETER NoRun
    Do everything except starting the dev server (just prepare the project).
.PARAMETER SkipAssets
    Skip asset conversion + manifests (use when they're already built and you
    just want to (re)start the game). Much faster.
.PARAMETER Force
    Re-convert every asset even if the output already looks up to date.
.PARAMETER Install
    Force `npm install` even if web/node_modules already exists.
.PARAMETER Port
    Dev-server port (default 5173). Must match web/vite.config.ts.

.NOTES
    Only Windows 11 is supported for now. Linux support is out of scope.
#>
param(
    [switch]$NoRun,
    [switch]$SkipAssets,
    [switch]$Force,
    [switch]$Install,
    [int]$Port = 5173
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repoRoot "scripts"
$webDir = Join-Path $repoRoot "web"
. (Join-Path $scripts "lib\common.ps1")

# --- helpers --------------------------------------------------------------

function Convert-IntroMovie {
    # The world map's Intro button plays legacy/images/menu/intro.mpg. Browsers
    # can't play MPEG-1, so transcode it once to H.264 mp4 (docs/038).
    $src = Join-Path $repoRoot "legacy\images\menu\intro.mpg"
    $destDir = Join-Path $webDir "public\assets\video"
    $dest = Join-Path $destDir "intro.mp4"

    Write-Step "Converting intro movie"
    if (-not (Test-Path $src)) {
        Write-WarnLine "legacy/images/menu/intro.mpg not found - skipping (Intro button will have no movie)."
        return
    }
    if ((Test-Path $dest) -and -not $Force) {
        Write-Ok "intro.mp4 already present (use -Force to redo)."
        return
    }
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

    $ffmpeg = Find-Tool -Name "ffmpeg"
    & $ffmpeg -y -loglevel error -i $src `
        -c:v libx264 -pix_fmt yuv420p -crf 24 -preset medium -movflags +faststart `
        -c:a aac -b:a 128k $dest
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed converting intro.mpg" }
    Write-Ok "intro.mp4 built."
}

function Build-Manifests {
    # file_exists() lookups the game does at runtime (docs/009 images, docs/018
    # sound). The audio manifest reads the *converted* sprite output, so it must
    # run after convert-assets.ps1.
    Write-Step "Building lookup manifests"
    & (Join-Path $scripts "build-image-manifest.ps1")
    if ($LASTEXITCODE -ne 0) { throw "image manifest build failed" }
    & (Join-Path $scripts "build-audio-manifest.ps1")
    if ($LASTEXITCODE -ne 0) { throw "audio manifest build failed" }
    Write-Ok "image-manifest.json + audio-manifest.json built."
}

function Invoke-AssetPipeline {
    Write-Step "Converting game assets (images, music, sound)"
    Write-Info "First run converts ~9000 files - this can take several minutes."
    Write-Info "Subsequent runs skip anything already up to date (use -Force to redo)."
    & (Join-Path $scripts "convert-assets.ps1") -Force:$Force
    Write-Ok "Images / music / sound converted."

    Convert-IntroMovie
    Build-Manifests
}

# --- main -----------------------------------------------------------------

$started = Get-Date
Write-Host "Fish Fillets - Web Generation :: local build" -ForegroundColor Magenta
Write-Host "Repository: $repoRoot" -ForegroundColor Gray

try {
    Assert-Prerequisites

    if (-not (Test-Path $webDir)) { throw "web/ directory not found at $webDir" }

    Write-Step "Installing npm dependencies (web/)"
    if ($Install -or -not (Test-Path (Join-Path $webDir "node_modules"))) {
        Push-Location $webDir
        try { npm install } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        Write-Ok "Dependencies installed."
    }
    else {
        Write-Ok "node_modules already present (use -Install to force a reinstall)."
    }

    if ($SkipAssets) {
        Write-Step "Assets"
        Write-WarnLine "Skipped (-SkipAssets). Ensure web/public/assets is already populated."
    }
    else {
        Invoke-AssetPipeline
    }

    if ($NoRun) {
        $elapsed = [int]((Get-Date) - $started).TotalSeconds
        Write-Step "Done (prepared in ${elapsed}s)"
        Write-Ok "Project ready. Start the game any time with:  scripts\setup.ps1 -SkipAssets"
        Write-Info "Or directly:  scripts\start.ps1"
        return
    }

    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    Write-Step "Starting the game (dev server, prepared in ${elapsed}s)"
    Write-Info "A browser tab will open at http://localhost:$Port/  -  press Ctrl+C here to stop."
    & (Join-Path $scripts "start.ps1") -Port $Port
}
catch {
    Write-Host ""
    Write-ErrLine $_.Exception.Message
    exit 1
}
