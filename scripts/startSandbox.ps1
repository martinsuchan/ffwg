#Requires -Version 5.1
<#
.SYNOPSIS
    Launch the local game in SANDBOX mode - every level unlocked and the bundled
    reference solutions replayable (opens /sandbox). For the standard,
    progression-gated game, use start.ps1 instead. See docs/045.

    Thin wrapper over start.ps1 -Sandbox; all its parameters are forwarded.
.PARAMETER NoOpen
    Don't automatically open a browser tab.
.PARAMETER Install
    Force `npm install` even if web/node_modules already exists.
.PARAMETER Port
    Dev-server port (default 5173; must match web/vite.config.ts).
#>
param(
    [switch]$NoOpen,
    [switch]$Install,
    [int]$Port = 5173
)

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "start.ps1") -Sandbox -NoOpen:$NoOpen -Install:$Install -Port $Port
