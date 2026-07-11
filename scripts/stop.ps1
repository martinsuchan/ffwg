#Requires -Version 5.1
<#
.SYNOPSIS
    Stops the web port's dev server, found by the port it's listening on.
.DESCRIPTION
    Closing the terminal doesn't reliably kill the node process `npm run dev`
    spawned - it can linger detached and then block the next start.ps1 with
    "port already in use". This finds whatever is listening on the dev-server
    port and stops it. No-op (with a message) if nothing is running there.
.PARAMETER Port
    Dev-server port to stop. Must match start.ps1 / web/vite.config.ts's
    server.port (default 5173).
#>
param(
    [int]$Port = 5173
)

$ErrorActionPreference = "Stop"

$conns = $null
try {
    $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
}
catch {
    throw "Could not query listening ports (Get-NetTCPConnection unavailable). Kill the node process manually via Task Manager > Details."
}

if (-not $conns) {
    Write-Host "No dev server listening on port $Port - nothing to stop."
    return
}

# A listener normally maps to one PID, but dedupe defensively.
$pids = $conns | Select-Object -ExpandProperty OwningProcess -Unique
foreach ($procId in $pids) {
    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $proc) {
        continue
    }
    Write-Host "Stopping dev server on port $Port - PID $procId ($($proc.ProcessName))."
    Stop-Process -Id $procId -Force
}
