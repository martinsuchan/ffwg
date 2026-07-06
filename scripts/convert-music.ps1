#Requires -Version 5.1
<#
.SYNOPSIS
    Batch-converts standalone legacy music tracks (.ogg) to MP3.
.PARAMETER Source
    Root directory of source music. Defaults to legacy/music.
.PARAMETER Destination
    Root directory to write .mp3 files to. Defaults to web/public/assets/music.
.PARAMETER Quality
    libmp3lame VBR quality (0=best/largest .. 9=worst/smallest). Default 2.
.PARAMETER Force
    Re-convert even if the destination file already looks up to date.
#>
param(
    [string]$Source,
    [string]$Destination,
    [int]$Quality = 2,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib\ffmpeg-tools.ps1")
$ffmpeg = Resolve-ToolPath -Name "ffmpeg"

if (-not $Source) { $Source = Join-Path $repoRoot "legacy\music" }
if (-not $Destination) { $Destination = Join-Path $repoRoot "web\public\assets\music" }

if (-not (Test-Path $Source)) {
    throw "Source directory not found: $Source"
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$converted = 0
$skipped = 0

foreach ($file in Get-ChildItem -Path $Source -Filter "*.ogg" -File) {
    $destFile = Join-Path $Destination ([System.IO.Path]::GetFileNameWithoutExtension($file.Name) + ".mp3")

    if (-not $Force -and (Test-Path $destFile) -and (Get-Item $destFile).LastWriteTimeUtc -gt $file.LastWriteTimeUtc) {
        $skipped++
        continue
    }

    & $ffmpeg -y -loglevel error -i $file.FullName -codec:a libmp3lame -q:a $Quality $destFile
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed converting $($file.FullName)"
    }
    $converted++
}

Write-Host "Music tracks converted: $converted, skipped (up to date): $skipped"
