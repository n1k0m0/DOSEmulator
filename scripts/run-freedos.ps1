param([switch]$NoBuild, [switch]$Writable, [string]$Overlay)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $workspaceRoot 'artifacts\m1-build'
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $outputRoot }
$runner = Join-Path $outputRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$emulatorExe = Join-Path $outputRoot 'Release\net10.0-windows\DOSEmulator.exe'
& (Join-Path $PSScriptRoot 'prepare-freedos.ps1') -Download -Runner $runner | Out-Host
$image = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
if ($Writable -or $Overlay) {
    if (-not $Overlay) {
        $overlayDirectory = Join-Path $workspaceRoot 'artifacts\freedos-user'
        New-Item -ItemType Directory -Path $overlayDirectory -Force | Out-Null
        $Overlay = Join-Path $overlayDirectory 'freedos.cow'
    }
    & $emulatorExe --disk $image --overlay ([IO.Path]::GetFullPath($Overlay))
} else { & $emulatorExe --disk $image }
