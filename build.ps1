<#
.SYNOPSIS
  Builds and tests Chronos Timer in Debug and/or Release.
.DESCRIPTION
  Needs the LTC-dotnet repository next to this one (..\LTC-dotnet), or pass -LtcRepo.
.EXAMPLE
  ./build.ps1                         # Debug + Release: core, audio, CLI, tests, MAUI app (Windows target on Windows)
  ./build.ps1 -Configuration Release  # one configuration
  ./build.ps1 -SkipApp -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release', 'Both')]
    [string]$Configuration = 'Both',
    [switch]$SkipApp,
    [switch]$SkipTests,
    [string]$LtcRepo
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# full output also goes to build.log (handy for sharing build results)
Start-Transcript -Path (Join-Path $PSScriptRoot 'build.log') -Force | Out-Null
trap { Write-Host "BUILD FAILED: $_" -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }

$extra = @('--nologo')
if ($LtcRepo) { $extra += "-p:LtcRepo=$((Resolve-Path $LtcRepo).Path)" }
elseif (-not (Test-Path (Join-Path $PSScriptRoot '..\LTC-dotnet\src\LinearTimecode\LinearTimecode.csproj'))) {
    throw "LTC-dotnet not found next to this repository. Clone it to ..\LTC-dotnet or pass -LtcRepo <path>."
}

$configs = if ($Configuration -eq 'Both') { @('Debug', 'Release') } else { @($Configuration) }

function Run([string]$what, [string[]]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    $ErrorActionPreference = 'Continue'   # stderr lines from dotnet are not PowerShell errors
    & dotnet @cmd @extra 2>&1 | ForEach-Object { "$_" } | Out-Host
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $what ($LASTEXITCODE)" -ForegroundColor Red; $script:failed += $what }
}
$failed = @()

foreach ($c in $configs) {
    Run "Build CLI + core + audio ($c)" @('build', 'tools/ChronosTimer.Cli', '-c', $c)
    if (-not $SkipTests) { Run "Test ($c)" @('test', 'tests/ChronosTimer.Tests', '-c', $c) }
    if (-not $SkipApp) {
        if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            Run "Build Chronos Timer app ($c)" @('build', 'apps/ChronosTimer.App', '-c', $c, '-f', 'net10.0-windows10.0.19041.0')
        } elseif ($IsMacOS) {
            Run "Build Chronos Timer app ($c)" @('build', 'apps/ChronosTimer.App', '-c', $c, '-f', 'net10.0-maccatalyst')
        } else {
            Write-Host "Skipping the MAUI app (build it on Windows or macOS)." -ForegroundColor Yellow
        }
    }
}
if ($failed.Count -gt 0) {
    Write-Host "BUILD FAILED: $($failed -join '; ')" -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}
Write-Host "Done: $($configs -join ', ')" -ForegroundColor Green
Stop-Transcript | Out-Null
