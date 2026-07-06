<#
.SYNOPSIS
    Shared helper for locating ffmpeg/ffprobe, dot-sourced by the asset conversion scripts.
#>

function Resolve-ToolPath {
    param(
        [Parameter(Mandatory = $true)][string]$Name
    )

    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    # Freshly-installed via winget in the same shell session won't have PATH
    # refreshed yet - fall back to winget's per-user Links folder.
    $wingetLink = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\$Name.exe"
    if (Test-Path $wingetLink) {
        return $wingetLink
    }

    throw "$Name not found on PATH or in the WinGet Links folder. Install ffmpeg (e.g. 'winget install Gyan.FFmpeg') and restart your shell."
}
