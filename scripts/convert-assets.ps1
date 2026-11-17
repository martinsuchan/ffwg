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

# --- Lossless WebP policy: the ONE place that lists every lossless choice ---
# Default is lossy WebP (atlases q90, individual images q85): level art is
# mostly painted/photographic, where lossless is 1.25-6.6x larger (all 82
# atlases: lossy 12.2 MB vs lossless 36.5 MB - docs/084). Lossless is used only
# where pixels are read back exactly, or where it is also smaller/sharper:
#   1. Hit-test masks (*_mask.*: map_mask, pedometer_mask) - their flat colours
#      identify button regions and must match exactly. Forced in
#      convert-images.ps1. (docs/039)
#   2. Fish atlases (all 4 variants) - flat sprite art with hard outlines,
#      always on screen; lossy edge noise is most visible there, lossless costs
#      ~+41 KB in total. (docs/062)
#   3. demo_briefcase movie frames - flat cartoon layers composited with alpha;
#      lossy fringes the alpha edges and lossless is SMALLER (2.40 vs 2.89 MB).
#      (docs/062)
#   4. Level atlases in $losslessLevelAtlases (docs/084):
#      - emulator: the "zx" draw effect (zxEffect.ts) paints with the exact
#        colours of zx-tmp.png's corner pixels (lossy turned 128,0,0 into
#        103,7,8); flat ZX pixel art, 29.6 KB -> 4.0 KB.
#      - submarine: the "mirror" draw effect (mirrorEffect.ts) finds the glass
#        by exact colour match against the mirror's centre pixel; lossy blurs
#        the glass edge and highlight. Costs ~+100 KB (33 -> 134 KB).
#      - tetris, warcraft: flat retro pixel art where lossless is simply
#        smaller and sharper (122 -> 21 KB, 241 -> 105 KB).
$losslessLevelAtlases = @("emulator", "submarine", "tetris", "warcraft")

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

# Lossless for the dirs in $losslessLevelAtlases - see the policy block above.
function Build-LevelAtlas {
    param([string]$Name)
    $lossless = $losslessLevelAtlases -contains $Name
    Write-Host "== Atlas: $Name$(if ($lossless) { ' [lossless]' }) =="
    Build-Atlas -Src (Join-Path $legacyImages $Name) -DestDir (Join-Path $assetsRoot "images\$Name") -Lossless:$lossless
}

function Build-FishAtlas {
    param([string]$Variant)
    Write-Host "== Atlas: fishes/$Variant [lossless] =="
    # Always lossless - policy item 2 above.
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
    # demo_briefcase is lossless - policy item 3 above.
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
