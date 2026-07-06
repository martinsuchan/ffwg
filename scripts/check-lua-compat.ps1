#Requires -Version 5.1
<#
.SYNOPSIS
    Parses every .lua file under -Source with the real wasmoon/Lua 5.4 engine
    and reports which files fail to compile. Pure syntax check - loadString()
    compiles without executing, so no legacy script side effects run.
    See web/tools/check-lua-compat.mjs for what this does and does not catch.
.PARAMETER Source
    Directory of .lua files to check. Defaults to legacy/script.
.PARAMETER Report
    Optional path to write a full JSON report of failures.
#>
param(
    [string]$Source,
    [string]$Report
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot "web"

if (-not $Source) { $Source = Join-Path $repoRoot "legacy\script" }

if (-not (Test-Path (Join-Path $webDir "node_modules"))) {
    Write-Host "Installing web/ dependencies..."
    Push-Location $webDir
    try { npm install } finally { Pop-Location }
}

Push-Location $webDir
try {
    if ($Report) {
        node "tools\check-lua-compat.mjs" $Source --report $Report
    }
    else {
        node "tools\check-lua-compat.mjs" $Source
    }
}
finally {
    Pop-Location
}
