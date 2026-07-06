#Requires -Version 5.1
<#
.SYNOPSIS
    Batch-converts legacy image assets to WebP, preserving directory structure.
.PARAMETER Source
    Root directory of source images. Defaults to legacy/images.
.PARAMETER Destination
    Root directory to write .webp files to. Defaults to web/public/assets/images.
.PARAMETER Quality
    WebP quality (0-100) for lossy encoding. Ignored if -Lossless is set. Default 85.
.PARAMETER Lossless
    Use lossless WebP encoding instead of lossy.
.PARAMETER Force
    Re-convert even if the destination file already looks up to date.
#>
param(
    [string]$Source,
    [string]$Destination,
    [int]$Quality = 85,
    [switch]$Lossless,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib\ffmpeg-tools.ps1")
$ffmpeg = Resolve-ToolPath -Name "ffmpeg"

if (-not $Source) { $Source = Join-Path $repoRoot "legacy\images" }
if (-not $Destination) { $Destination = Join-Path $repoRoot "web\public\assets\images" }

if (-not (Test-Path $Source)) {
    throw "Source directory not found: $Source"
}

# Resolve to absolute, backslash-normalized paths so the relative-path
# substring math below lines up with Get-ChildItem's FullName values
# regardless of how -Source/-Destination were spelled (relative, forward
# slashes, trailing slash, ...).
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\', '/')
if (-not [System.IO.Path]::IsPathRooted($Destination)) {
    $Destination = Join-Path (Get-Location).Path $Destination
}
$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')

$imageExtensions = @(".png", ".jpg", ".jpeg", ".gif", ".bmp")
$files = Get-ChildItem -Path $Source -Recurse -File |
    Where-Object { $imageExtensions -contains $_.Extension.ToLowerInvariant() }

$converted = 0
$skipped = 0
$sourceBytes = 0L
$destBytes = 0L

foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($Source.Length).TrimStart('\', '/')
    $relativeDir = Split-Path -Parent $relativePath
    $destDir = if ($relativeDir) { Join-Path $Destination $relativeDir } else { $Destination }
    $destFile = Join-Path $destDir ([System.IO.Path]::GetFileNameWithoutExtension($file.Name) + ".webp")

    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    if (-not $Force -and (Test-Path $destFile) -and (Get-Item $destFile).LastWriteTimeUtc -gt $file.LastWriteTimeUtc) {
        $skipped++
        continue
    }

    $qualityArgs = if ($Lossless) { @("-lossless", "1") } else { @("-quality", "$Quality") }
    & $ffmpeg -y -loglevel error -i $file.FullName -c:v libwebp @qualityArgs $destFile
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed converting $($file.FullName)"
    }

    $converted++
    $sourceBytes += $file.Length
    $destBytes += (Get-Item $destFile).Length
}

Write-Host "Images converted: $converted, skipped (up to date): $skipped"
if ($converted -gt 0) {
    $sourceMB = [math]::Round($sourceBytes / 1MB, 2)
    $destMB = [math]::Round($destBytes / 1MB, 2)
    Write-Host "Size: $sourceMB MB -> $destMB MB"
}
