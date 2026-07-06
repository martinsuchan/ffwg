#Requires -Version 5.1
<#
.SYNOPSIS
    Converts all legacy image/music/sound assets into web-ready formats
    (WebP images, MP3 music, MP3 audio sprites) under web/public/assets/.

    Sound is handled generically: every directory under legacy/sound/ that
    directly contains .ogg files gets its own audio sprite (this covers
    per-level per-language voice lines in legacy/sound/<level>/<lang>/, and
    the shared SFX pool in legacy/sound/share/ and its subfolders).
.PARAMETER Level
    Optional: only process this one level's images/sound (e.g. "airplane"),
    for quick testing instead of the full ~9000-file batch. Music has no
    per-level concept, so it's skipped when -Level is set.
.PARAMETER Force
    Re-convert everything even if outputs already look up to date.
#>
param(
    [string]$Level,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$legacyImages = Join-Path $repoRoot "legacy\images"
$legacyMusic = Join-Path $repoRoot "legacy\music"
$legacySound = Join-Path $repoRoot "legacy\sound"
$assetsRoot = Join-Path $repoRoot "web\public\assets"

# --- Images ---
$imageSource = if ($Level) { Join-Path $legacyImages $Level } else { $legacyImages }
$imageDest = if ($Level) { Join-Path $assetsRoot "images\$Level" } else { Join-Path $assetsRoot "images" }
if (Test-Path $imageSource) {
    Write-Host "== Images: $imageSource -> $imageDest =="
    & (Join-Path $PSScriptRoot "convert-images.ps1") -Source $imageSource -Destination $imageDest -Force:$Force
}

# --- Music (standalone tracks, not per-level) ---
if (-not $Level) {
    Write-Host "== Music =="
    & (Join-Path $PSScriptRoot "convert-music.ps1") -Source $legacyMusic -Destination (Join-Path $assetsRoot "music") -Force:$Force
}

# --- Sound: one sprite per directory that directly contains .ogg files ---
$soundSource = if ($Level) { Join-Path $legacySound $Level } else { $legacySound }
if (Test-Path $soundSource) {
    Write-Host "== Sound sprites: $soundSource =="
    $allDirs = @(Get-Item $soundSource) + @(Get-ChildItem -Path $soundSource -Recurse -Directory)

    foreach ($dir in $allDirs) {
        $oggFiles = @(Get-ChildItem -Path $dir.FullName -Filter "*.ogg" -File)
        if ($oggFiles.Count -eq 0) { continue }

        $relative = $dir.FullName.Substring($legacySound.Length).TrimStart('\', '/')
        $outDir = if ($relative) { Join-Path $assetsRoot "sound\$relative" } else { Join-Path $assetsRoot "sound" }
        $outputBase = Join-Path $outDir "sprite"

        if (-not $Force -and (Test-Path "$outputBase.mp3")) {
            $newestSource = ($oggFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
            if ((Get-Item "$outputBase.mp3").LastWriteTimeUtc -gt $newestSource) {
                Write-Host "Skipping (up to date): $relative"
                continue
            }
        }

        & (Join-Path $PSScriptRoot "build-audio-sprite.ps1") -Source $dir.FullName -OutputBase $outputBase
    }
}

Write-Host "Done."
