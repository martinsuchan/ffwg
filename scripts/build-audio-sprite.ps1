#Requires -Version 5.1
<#
.SYNOPSIS
    Packs all .ogg files directly inside -Source into one MP3 audio sprite plus
    a Phaser-compatible JSON spritemap (the format Phaser's `load.audioSprite()`
    expects: {"resources": [...], "spritemap": {name: {start, end, loop}}}).
.PARAMETER Source
    Directory containing the source .ogg clips. Only direct children are used,
    not subdirectories - call this once per directory that should become one sprite.
.PARAMETER OutputBase
    Output path without extension, e.g. "...\airplane\cs\sprite" produces
    "sprite.mp3" and "sprite.json" there.
.PARAMETER GapSeconds
    Silence inserted between clips in the combined file, so seeking near a clip
    boundary can't bleed into the next one. Default 0.5.
.PARAMETER Quality
    libmp3lame VBR quality (0=best/largest .. 9=worst/smallest). Default 4.
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$OutputBase,
    [double]$GapSeconds = 0.5,
    [int]$Quality = 4
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib\ffmpeg-tools.ps1")
$ffmpeg = Resolve-ToolPath -Name "ffmpeg"
$ffprobe = Resolve-ToolPath -Name "ffprobe"

$clips = Get-ChildItem -Path $Source -Filter "*.ogg" -File | Sort-Object Name
if ($clips.Count -eq 0) {
    throw "No .ogg files found directly in $Source"
}

$outputDir = Split-Path -Parent $OutputBase
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("audiosprite-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null

function ToForwardSlash([string]$p) {
    return $p.Replace([char]92, [char]47)
}

try {
    $silence = Join-Path $work "silence.wav"
    & $ffmpeg -y -loglevel error -f lavfi -i "anullsrc=r=44100:cl=stereo" -t $GapSeconds -c:a pcm_s16le $silence
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed generating silence" }

    $listLines = New-Object System.Collections.Generic.List[string]
    $spritemap = [ordered]@{}
    $cumulative = 0.0
    $i = 0

    foreach ($clip in $clips) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($clip.Name)
        $norm = Join-Path $work "clip_$i.wav"
        & $ffmpeg -y -loglevel error -i $clip.FullName -ar 44100 -ac 2 -c:a pcm_s16le $norm
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed normalizing $($clip.FullName)" }

        $durText = & $ffprobe -v error -show_entries format=duration -of csv=p=0 $norm
        $dur = [double]$durText

        $start = $cumulative
        $end = $start + $dur
        $spritemap[$name] = [ordered]@{ start = $start; end = $end; loop = $false }

        $listLines.Add("file '" + (ToForwardSlash $norm) + "'")
        $listLines.Add("file '" + (ToForwardSlash $silence) + "'")

        $cumulative = $end + $GapSeconds
        $i++
    }

    $listFile = Join-Path $work "list.txt"
    Set-Content -Path $listFile -Value $listLines -Encoding ascii

    $combined = Join-Path $work "combined.wav"
    & $ffmpeg -y -loglevel error -f concat -safe 0 -i $listFile -c copy $combined
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed concatenating clips" }

    $mp3Path = "$OutputBase.mp3"
    & $ffmpeg -y -loglevel error -i $combined -codec:a libmp3lame -q:a $Quality $mp3Path
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed encoding sprite mp3" }

    $jsonObject = [ordered]@{
        resources = @([System.IO.Path]::GetFileName($mp3Path))
        spritemap = $spritemap
    }
    $json = $jsonObject | ConvertTo-Json -Depth 5
    # Explicit no-BOM UTF-8 regardless of PowerShell version/edition - Set-Content
    # -Encoding utf8 adds a BOM on Windows PowerShell 5.1, which some JSON parsers reject.
    [System.IO.File]::WriteAllText("$OutputBase.json", $json, [System.Text.UTF8Encoding]::new($false))

    Write-Host "Built $($clips.Count)-clip sprite: $mp3Path"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
