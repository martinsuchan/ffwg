<#
.SYNOPSIS
    Shared helpers for setup.ps1 / publish.ps1: colored console logging and a
    Windows prerequisite check (Node.js/npm/ffmpeg/ffprobe) with install hints.
    Dot-sourced, not run directly.
#>

# --- Console logging ------------------------------------------------------

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    [OK] $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Write-WarnLine {
    param([string]$Message)
    Write-Host "    [!] $Message" -ForegroundColor Yellow
}

function Write-ErrLine {
    param([string]$Message)
    Write-Host "    [X] $Message" -ForegroundColor Red
}

# --- Tool discovery -------------------------------------------------------

# Return the full path to a CLI tool, or $null if not found. Handles the case
# where a tool was just installed via winget in this same shell (PATH not yet
# refreshed) by also probing winget's per-user Links folder.
function Find-Tool {
    param([Parameter(Mandatory = $true)][string]$Name)

    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $wingetLink = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\$Name.exe"
    if (Test-Path $wingetLink) { return $wingetLink }

    return $null
}

function Get-ToolVersion {
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Arg = "--version")
    try {
        $out = & $Path $Arg 2>&1 | Select-Object -First 1
        return ($out | Out-String).Trim()
    }
    catch {
        return "(version unknown)"
    }
}

# --- Prerequisite check ---------------------------------------------------

# Verifies every tool the build needs is installed. Prints a status line per
# tool; if any are missing, prints per-tool install instructions and throws.
# All required tools are available on Windows 11 via winget.
function Assert-Prerequisites {
    Write-Step "Checking required tools"

    # name, version-arg, winget package id, human hint
    $tools = @(
        @{ Name = "node";    Arg = "--version"; Winget = "OpenJS.NodeJS.LTS"; Hint = "Node.js (LTS) - includes npm" }
        @{ Name = "npm";     Arg = "--version"; Winget = "OpenJS.NodeJS.LTS"; Hint = "npm - ships with Node.js" }
        @{ Name = "ffmpeg";  Arg = "-version";  Winget = "Gyan.FFmpeg";       Hint = "FFmpeg - converts images/sound/music/video" }
        @{ Name = "ffprobe"; Arg = "-version";  Winget = "Gyan.FFmpeg";       Hint = "ffprobe - ships with FFmpeg" }
    )

    $missing = @()
    foreach ($tool in $tools) {
        $path = Find-Tool -Name $tool.Name
        if ($path) {
            $version = Get-ToolVersion -Path $path -Arg $tool.Arg
            Write-Ok ("{0,-8} {1}" -f $tool.Name, $version)
        }
        else {
            Write-ErrLine ("{0,-8} NOT FOUND - {1}" -f $tool.Name, $tool.Hint)
            $missing += $tool
        }
    }

    if ($missing.Count -eq 0) {
        Write-Ok "All required tools are installed."
        return
    }

    # Deduplicate winget packages (npm+node, ffmpeg+ffprobe share one each).
    $packages = $missing | Select-Object -ExpandProperty Winget -Unique

    Write-Host ""
    Write-Host "Missing tools. Install them (Windows 11), then re-run this script:" -ForegroundColor Yellow
    foreach ($pkg in $packages) {
        Write-Host "    winget install --id $pkg -e" -ForegroundColor White
    }
    Write-Host ""
    Write-Host "    After installing, OPEN A NEW TERMINAL so PATH is refreshed." -ForegroundColor Yellow
    Write-Host "    (No winget? Node.js: https://nodejs.org/  |  FFmpeg: https://ffmpeg.org/download.html)" -ForegroundColor Gray

    throw "Missing required tool(s): $((($missing | Select-Object -ExpandProperty Name) -join ', ')). See instructions above."
}
