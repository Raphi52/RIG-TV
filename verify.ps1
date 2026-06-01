#requires -Version 5.1
<#
.SYNOPSIS
    Build + tests for Source\Wpf - the WPF reconstruction of RIG.

.DESCRIPTION
    Default mode runs unit tests only (fast, suitable for pre-commit).
    -All also runs integration tests (RIG_DEV) and UI tests (FlaUI).

.PARAMETER All
    Run integration and UI tests in addition to unit tests.

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release.

.EXAMPLE
    .\verify.ps1
    .\verify.ps1 -All
#>
[CmdletBinding()]
param(
    [switch] $All,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$sln = Join-Path $root 'Rig.Wpf.sln'

function Step([string]$label, [scriptblock]$block) {
    Write-Host ""
    Write-Host "==> $label" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $block
    $sw.Stop()
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED ($label) - exit $LASTEXITCODE - $($sw.Elapsed)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "OK ($label) - $($sw.Elapsed)" -ForegroundColor Green
}

Push-Location $root
try {
    Step "Restore" { dotnet restore $sln --nologo }
    Step "Build ($Configuration)" {
        dotnet build $sln -c $Configuration --no-restore --nologo
    }
    Step "Unit tests" {
        dotnet test $sln -c $Configuration --no-build --nologo `
            --filter "Category!=Integration&Category!=Ui"
    }

    if ($All) {
        # Filtre par Category : capture tous les projets de tests Integration / UI
        # sans avoir à les lister un par un.
        Step "Integration tests" {
            dotnet test $sln -c $Configuration --no-build --nologo `
                --filter "Category=Integration"
        }
        Step "UI tests (FlaUI)" {
            dotnet test $sln -c $Configuration --no-build --nologo `
                --filter "Category=Ui"
        }
    }

    Write-Host ""
    Write-Host "All checks passed." -ForegroundColor Green
} finally {
    Pop-Location
}
