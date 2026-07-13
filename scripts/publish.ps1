#Requires -Version 5.1
<#
.SYNOPSIS
    Produce a ready-to-deploy 'publish' folder for the Fish Fillets web port
    (Windows 11). Everything a static web host needs is inside it - copy the
    folder's contents to your server's web root and you're done.

    The game is a pure client-side static site (no server runtime/database):
    Phaser for rendering, the Lua interpreter runs in-browser as WebAssembly.

    Run it from the repository root:  scripts\publish.ps1

    If PowerShell blocks the script ("running scripts is disabled"), start it
    with:   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

.PARAMETER OutputDir
    Where to write the package (default: .\publish).
.PARAMETER SkipAssets
    Skip asset conversion (only if web/public/assets is already fully built).
.PARAMETER Force
    Re-convert every asset even if the output already looks up to date.

.NOTES
    Only Windows 11 is supported for now. Deploy target: any static file host
    (Azure Static Web Apps recommended; also Azure Blob static website, IIS /
    Azure App Service via the included web.config). Serve at the site root and
    over HTTPS. See publish/README.txt after running.
#>
param(
    [string]$OutputDir,
    [switch]$SkipAssets,
    [switch]$Force,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repoRoot "scripts"
$webDir = Join-Path $repoRoot "web"
$distDir = Join-Path $webDir "dist"
. (Join-Path $scripts "lib\common.ps1")

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "publish" }

# --- helpers --------------------------------------------------------------

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

# Writes host configuration (Azure Static Web Apps + IIS/App Service) and a
# short deploy guide into the package.
function Write-DeployFiles {
    param([Parameter(Mandatory = $true)][string]$OutDir)

    Write-Step "Writing host config + README"

    $swaConfig = @'
{
  "navigationFallback": {
    "rewrite": "/index.html",
    "exclude": ["/assets/*", "/legacy/*", "/lua/*", "/*.js", "/*.css", "/*.wasm"]
  },
  "mimeTypes": {
    ".lua": "text/plain",
    ".wasm": "application/wasm",
    ".webp": "image/webp"
  }
}
'@
    Write-Utf8NoBom -Path (Join-Path $OutDir "staticwebapp.config.json") -Content $swaConfig

    $webConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<!--
  Static hosting for IIS / Azure App Service (Windows). The game has no
  client-side routes (it always loads at "/"), so no URL-rewrite module or SPA
  fallback is needed - only correct MIME types for the served assets.
-->
<configuration>
  <system.webServer>
    <staticContent>
      <remove fileExtension=".wasm" />
      <mimeMap fileExtension=".wasm" mimeType="application/wasm" />
      <remove fileExtension=".lua" />
      <mimeMap fileExtension=".lua" mimeType="text/plain" />
      <remove fileExtension=".webp" />
      <mimeMap fileExtension=".webp" mimeType="image/webp" />
      <remove fileExtension=".mp4" />
      <mimeMap fileExtension=".mp4" mimeType="video/mp4" />
    </staticContent>
  </system.webServer>
</configuration>
'@
    Write-Utf8NoBom -Path (Join-Path $OutDir "web.config") -Content $webConfig

    $readme = @'
Fish Fillets - Web Generation - deployment package
==================================================

This folder is a complete, self-contained STATIC website. There is no server
code, database, or runtime to install - the whole game (including the Lua
interpreter, compiled to WebAssembly) runs in the visitor's browser.

To publish: serve the CONTENTS of this folder from your web root, at the site
ROOT (not a sub-path), over HTTPS.

Contents
--------
  index.html                 entry point
  assets/                    JS bundle, WebAssembly, images, sound, music, video
  lua/                       runtime Lua shims + lookup manifests
  legacy/                    level scripts + reference solutions (loaded at runtime)
  favicon.png
  staticwebapp.config.json   config for Azure Static Web Apps
  web.config                 MIME types for IIS / Azure App Service (Windows)

Deploy options
--------------
* Azure Static Web Apps (recommended)
    npm install -g @azure/static-web-apps-cli
    swa deploy "<this folder>" --env production
  or point an Azure Static Web Apps GitHub Action's "output location" here.

* Azure Blob Storage static website
    Enable "Static website" on the storage account and upload the contents of
    this folder to the $web container. Put Azure CDN / Front Door in front for
    HTTPS on a custom domain.

* IIS / Azure App Service (Windows)
    Copy the contents into the site root. web.config sets the required MIME
    types (.wasm/.lua/.webp/.mp4). Ensure HTTPS is enabled.

Notes
-----
* Must be served at the site root. To host under a sub-path, set Vite's "base"
  in web/vite.config.ts and re-run scripts\publish.ps1.
* Licensing: this game is GPLv2 (see legacy/COPYING, legacy/AUTHORS, LICENSE).
  Publishing it obliges you to make the corresponding source available and to
  preserve the license and attribution.
'@
    Write-Utf8NoBom -Path (Join-Path $OutDir "README.txt") -Content $readme

    Write-Ok "staticwebapp.config.json + web.config + README.txt written."
}

# --- main -----------------------------------------------------------------

$started = Get-Date
Write-Host "Fish Fillets - Web Generation :: publish" -ForegroundColor Magenta
Write-Host "Repository: $repoRoot" -ForegroundColor Gray
Write-Host "Output:     $OutputDir" -ForegroundColor Gray

try {
    # 1. Prepare (tools, deps, assets, manifests) via the local setup script.
    Write-Step "Preparing project (tools + dependencies + assets)"
    & (Join-Path $scripts "setup.ps1") -NoRun -SkipAssets:$SkipAssets -Force:$Force

    # 2. Production build (tsc + vite) -> web/dist.
    Write-Step "Building the production bundle (npm run build)"
    Push-Location $webDir
    try { npm run build } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    if (-not (Test-Path (Join-Path $distDir "index.html"))) {
        throw "expected build output not found at $distDir"
    }
    Write-Ok "Bundle built (web/dist)."

    # 3. Assemble the publish folder.
    Write-Step "Assembling the publish folder"
    if (Test-Path $OutputDir) {
        Remove-Item -LiteralPath $OutputDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    # 3a. The built site (HTML/JS/WASM + public/ = /assets, /lua, /favicon...).
    Copy-Item -Path (Join-Path $distDir "*") -Destination $OutputDir -Recurse -Force
    Write-Ok "Copied the built site."

    # 3b. The legacy Lua content the game fetches at runtime. In production
    #     LEGACY_ROOT resolves to <site>/legacy/ (see web/src/lua/levelLoader.ts),
    #     so it must live next to the site. Only these two trees are fetched:
    #     level scripts (script/**) and reference solutions (solution/**).
    $legacyDest = Join-Path $OutputDir "legacy"
    New-Item -ItemType Directory -Path $legacyDest -Force | Out-Null
    Copy-Item -Path (Join-Path $repoRoot "legacy\script") -Destination $legacyDest -Recurse -Force
    Copy-Item -Path (Join-Path $repoRoot "legacy\solution") -Destination $legacyDest -Recurse -Force
    Write-Ok "Copied legacy Lua content (script + solution)."

    # 3c. Host config + a short deploy guide.
    Write-DeployFiles -OutDir $OutputDir

    # 4. Summary.
    $bytes = (Get-ChildItem -LiteralPath $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $sizeMB = [math]::Round($bytes / 1MB, 1)
    $fileCount = (Get-ChildItem -LiteralPath $OutputDir -Recurse -File | Measure-Object).Count
    $elapsed = [int]((Get-Date) - $started).TotalSeconds

    Write-Step "Done in ${elapsed}s"
    Write-Ok "Package ready: $OutputDir"
    Write-Info "$fileCount files, $sizeMB MB total."
    Write-Info "Deploy: copy the CONTENTS of that folder to your web root (serve at the"
    Write-Info "site root, over HTTPS). See $OutputDir\README.txt for per-host steps."
}
catch {
    Write-Host ""
    Write-ErrLine $_.Exception.Message
    exit 1
}
