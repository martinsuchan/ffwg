#Requires -Version 5.1
<#
.SYNOPSIS
    Build (or Native-AOT publish) the Fish Fillets solver, then verify it against
    the game's recorded solutions. See solver/docs/001.

    Run from the repository root:  scripts\build-solver.ps1

.PARAMETER Publish
    Produce a self-contained Native AOT executable instead of a plain build.
    Needs the "Desktop development with C++" workload in the Visual Studio
    Installer; a plain build does not.
.PARAMETER Runtime
    RID for -Publish (default: this machine's, e.g. win-arm64 / win-x64).
.PARAMETER NoVerify
    Skip the test run (and, with -Publish, the `verify --all` smoke test).

.NOTES
    Needs the .NET 10 SDK. Windows only (the AOT prerequisites below are).
#>
param(
    [switch]$Publish,
    [string]$Runtime,
    [switch]$NoVerify
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solverDir = Join-Path $repoRoot "solver"
$cliProject = Join-Path $solverDir "src\FishFillets.Cli\FishFillets.Cli.csproj"
. (Join-Path $PSScriptRoot "lib\common.ps1")

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-ErrLine "dotnet not found. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10"
    exit 1
}

if ($Publish) {
    if (-not $Runtime) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $Runtime = "win-$arch"
    }

    # ILCompiler's Windows targets shell out to `vswhere` by name to locate the
    # MSVC linker, but the VS Installer directory it lives in isn't on PATH by
    # default. Without this the link step fails with the vswhere "not
    # recognized" message spliced into its own command line (exit 123).
    $installerDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    if ((Test-Path (Join-Path $installerDir "vswhere.exe")) -and ($env:PATH -notlike "*$installerDir*")) {
        Write-Step "Adding the VS Installer directory to PATH (for vswhere)"
        $env:PATH = "$installerDir;$env:PATH"
    }

    Write-Step "Publishing the solver (Native AOT, $Runtime)"
    dotnet publish $cliProject -c Release -r $Runtime
    if ($LASTEXITCODE -ne 0) {
        Write-ErrLine @"
Native AOT publish failed.

If the error mentions a platform linker, install the "Desktop development with
C++" workload in the Visual Studio Installer (on ARM64 also tick "MSVC ARM64
build tools"). A plain build - scripts\build-solver.ps1 with no -Publish - needs
none of that.
"@
        exit 1
    }

    $exe = Join-Path $solverDir "src\FishFillets.Cli\bin\Release\net10.0\$Runtime\publish\ffsolve.exe"
    Write-Ok "Published: $exe"

    if (-not $NoVerify) {
        # Smoke-test the actual published binary. The test suite below covers the
        # same corpus, but against the managed build - this proves the AOT exe runs.
        Write-Step "Smoke-testing the published binary"
        & $exe verify --all | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0) { Write-ErrLine "The published binary failed verification."; exit 1 }
        Write-Ok "Published binary verified."
    }
}
else {
    Write-Step "Building the solver"
    dotnet build (Join-Path $solverDir "FishFillets.Solver.slnx") -c Release
    if ($LASTEXITCODE -ne 0) { Write-ErrLine "Build failed."; exit 1 }
    Write-Ok "Build succeeded."
}

if ($NoVerify) { exit 0 }

# The regression suite: every one of the game's recorded solutions must still
# replay to Solved (one test case per level), plus the rule and invalid-solution
# tests. See solver/docs/002.
Write-Step "Running the test suite"
dotnet test (Join-Path $solverDir "FishFillets.Solver.slnx") -c Release
if ($LASTEXITCODE -ne 0) { Write-ErrLine "Tests FAILED."; exit 1 }
Write-Ok "Tests passed."
