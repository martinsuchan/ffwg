#Requires -Version 5.1
<#
.SYNOPSIS
    Scaffolds the next numbered docs/ log entry (see docs/README.md for the convention).
.PARAMETER Slug
    Short description, e.g. "lua runtime spike". Used in the filename (kebab-cased) and as the default title.
.PARAMETER Title
    Optional human-readable title. Defaults to the slug, title-cased.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Slug,

    [string]$Title
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$docsDir = Join-Path $repoRoot "docs"

if (-not (Test-Path $docsDir)) {
    throw "docs/ directory not found at $docsDir"
}

$slugified = ($Slug.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
if (-not $slugified) {
    throw "Slug '$Slug' produced an empty filename fragment."
}

$existingNumbers = Get-ChildItem -Path $docsDir -Filter "*.md" |
    Where-Object { $_.Name -match '^(\d{3})-' } |
    ForEach-Object { [int]$Matches[1] }

$nextNumber = if ($existingNumbers) { [int]($existingNumbers | Measure-Object -Maximum).Maximum + 1 } else { 1 }
$numberStr = "{0:D3}" -f $nextNumber

$date = Get-Date -Format "yyyy-MM-dd"
$fileName = "$numberStr-$date-$slugified.md"
$filePath = Join-Path $docsDir $fileName

if (Test-Path $filePath) {
    throw "$filePath already exists."
}

if (-not $Title) {
    $Title = (Get-Culture).TextInfo.ToTitleCase(($slugified -replace '-', ' '))
}

$content = @"
# $numberStr - $Title

$date

## What was done

-

## Open for next time

-
"@

# UTF-8 *without* BOM and LF line endings, like the rest of docs/. (Set-Content /
# Add-Content -Encoding utf8 write a BOM on Windows PowerShell 5.1.)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$content = ($content -replace "`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($filePath, $content, $utf8NoBom)
Write-Host "Created $filePath"

# Keep docs/README.md's one-line-per-entry index in sync (it, not CLAUDE.md, is the
# place milestones get listed - see docs/077).
$readme = Join-Path $docsDir "README.md"
if (Test-Path $readme) {
    $dash = [char]0x2014  # em dash; kept out of the source so the file stays ASCII for PS 5.1
    $line = "- [$numberStr]($fileName) $dash $Title`n"
    # Start on a fresh line even if the index lacks a trailing newline.
    $existing = [System.IO.File]::ReadAllText($readme)
    if ($existing.Length -gt 0 -and -not $existing.EndsWith("`n")) { $line = "`n" + $line }
    [System.IO.File]::AppendAllText($readme, $line, $utf8NoBom)
    Write-Host "Indexed in $readme (edit the title there if the default isn't descriptive)"
}
