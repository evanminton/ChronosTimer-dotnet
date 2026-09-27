<#
.SYNOPSIS
  Publishes portable builds into artifacts/:
    chronos-timer (console utility): self-contained single-file executables, no install needed
    Chronos Timer app (Windows): unpackaged, self-contained folder + zip
.EXAMPLE
  ./publish.ps1                                  # CLI for the current OS + the Windows app (on Windows)
  ./publish.ps1 -Rids win-x64,osx-arm64,linux-x64
  ./publish.ps1 -SkipApp
#>
param(
    [string[]]$Rids,
    [switch]$SkipApp,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force $out | Out-Null
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1

if (-not $Rids) {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $os = if ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { 'win' }
    $Rids = @("$os-$arch")
}

foreach ($rid in $Rids) {
    $dir = Join-Path $out "chronos-timer-$version-$rid"
    Write-Host "==> chronos-timer $rid" -ForegroundColor Cyan
    dotnet publish tools/ChronosTimer.Cli -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $dir --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    # an empty settings file next to the exe switches the utility to portable mode (settings travel with it)
    '{}' | Set-Content -Encoding utf8 (Join-Path $dir 'chronos-timer.json')
    Compress-Archive -Path "$dir/*" -DestinationPath "$dir.zip" -Force
}

if (-not $SkipApp -and ($IsWindows -or $env:OS -eq 'Windows_NT')) {
    $dir = Join-Path $out "ChronosTimer-$version-win-x64"
    Write-Host "==> Chronos Timer app win-x64" -ForegroundColor Cyan
    dotnet publish apps/ChronosTimer.App -c $Configuration -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true `
        -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o $dir --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish app failed" }
    Compress-Archive -Path "$dir/*" -DestinationPath "$dir.zip" -Force
}
Write-Host "Artifacts in $out" -ForegroundColor Green
