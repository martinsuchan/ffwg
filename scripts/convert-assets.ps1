#Requires -Version 5.1
<#
.SYNOPSIS
    Converts all legacy image/music/sound assets into web-ready formats
    (WebP images, MP3 music, MP3 audio sprites) under web/public/assets/.

    Sound is handled generically: every directory under legacy/sound/ that
    directly contains .ogg files gets its own audio sprite (this covers
    per-level per-language voice lines in legacy/sound/<level>/<lang>/, and
    the shared SFX pool in legacy/sound/share/ and its subfolders).

    Images are packed into Phaser texture atlases (docs/042): each level dir
    (legacy/images/<level>/) and each shared fish variant
    (legacy/images/fishes/{small,big,ex_small,ex_big}/) becomes one
    atlas.webp + atlas.json, and the individual per-sprite .webp files are NOT
    emitted for those dirs. Only the two dirs that can't single-page pack and
    are loaded through their own pathways - menu/ (world map UI, needs its
    lossless masks) and demo_briefcase/ (the movie frames) - stay as
    individual webp.
.PARAMETER Level
    Optional: only process this one level's images/sound (e.g. "airplane"),
    for quick testing instead of the full ~9000-file batch. Music has no
    per-level concept, so it's skipped when -Level is set. The shared fish
    atlases are always (re)built too, since every level needs them.
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

# Dirs under legacy/images/ that are NOT atlased: they're loaded through their
# own pathways (not ModelAnimator) and/or can't single-page pack. They get
# individual webp conversion instead. "fishes" is excluded from the level loop
# because it's atlased separately (one atlas per variant, below).
$nonAtlasImageDirs = @("menu", "demo_briefcase")
$fishVariants = @("small", "big", "ex_small", "ex_big")

# --- Images: per-level + fish atlases, individual webp for the rest ---
$buildAtlas = Join-Path $PSScriptRoot "build-atlas.ps1"
$convertImages = Join-Path $PSScriptRoot "convert-images.ps1"

function Convert-ImageDirIndividual {
    param([string]$Name, [switch]$Lossless)
    $src = Join-Path $legacyImages $Name
    if (-not (Test-Path $src)) { return }
    Write-Host "== Images (individual): $Name$(if ($Lossless) { ' [lossless]' }) =="
    & $convertImages -Source $src -Destination (Join-Path $assetsRoot "images\$Name") -Force:$Force -Lossless:$Lossless
}

# Atlas one image dir into <destDir>/atlas.{webp,json}. Wipes destDir first so
# the dir becomes atlas-only - no stale individual .webp sprites from an earlier
# (pre-atlas) conversion linger to be published alongside the atlas.
function Build-Atlas {
    param([string]$Src, [string]$DestDir, [switch]$Lossless)
    if (-not (Test-Path $Src)) { return }
    if (Test-Path $DestDir) { Remove-Item -LiteralPath $DestDir -Recurse -Force }
    & $buildAtlas -Source $Src -Destination (Join-Path $DestDir "atlas") -Lossless:$Lossless
}

function Build-LevelAtlas {
    param([string]$Name)
    Write-Host "== Atlas: $Name =="
    Build-Atlas -Src (Join-Path $legacyImages $Name) -DestDir (Join-Path $assetsRoot "images\$Name")
}

function Build-FishAtlas {
    param([string]$Variant)
    Write-Host "== Atlas: fishes/$Variant [lossless] =="
    # The fish are flat sprite art with hard outlines, always on screen and
    # animated - lossy WebP's edge noise is most visible on them, and lossless
    # costs only ~+41 KB across all 4 variants. See docs/062.
    Build-Atlas -Src (Join-Path $legacyImages "fishes\$Variant") `
        -DestDir (Join-Path $assetsRoot "images\fishes\$Variant") -Lossless
}

if ($Level) {
    # Quick single-level path. Atlas the level (unless it's one of the
    # non-atlased special dirs) plus always (re)build the fish atlases it needs.
    if ($nonAtlasImageDirs -contains $Level -or $Level -eq "fishes") {
        Convert-ImageDirIndividual -Name $Level
    }
    else {
        Build-LevelAtlas -Name $Level
    }
    foreach ($variant in $fishVariants) { Build-FishAtlas -Variant $variant }
}
else {
    # Full batch: individual webp for the non-atlased dirs, an atlas per level
    # dir, and an atlas per fish variant.
    # demo_briefcase's movie frames are flat cartoon art composited as
    # transparent layers - lossy WebP bleeds RGB block-noise across their hard
    # alpha edges (visible fringing), and lossless is actually SMALLER for this
    # content (2.40 vs 2.89 MB). So convert them lossless. See docs/062.
    foreach ($name in $nonAtlasImageDirs) {
        Convert-ImageDirIndividual -Name $name -Lossless:($name -eq "demo_briefcase")
    }

    $levelDirs = Get-ChildItem -Path $legacyImages -Directory |
        Where-Object { $nonAtlasImageDirs -notcontains $_.Name -and $_.Name -ne "fishes" }
    foreach ($dir in $levelDirs) { Build-LevelAtlas -Name $dir.Name }

    foreach ($variant in $fishVariants) { Build-FishAtlas -Variant $variant }
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
