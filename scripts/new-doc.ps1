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

Set-Content -Path $filePath -Value $content -Encoding utf8
Write-Host "Created $filePath"
