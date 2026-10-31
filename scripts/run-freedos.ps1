# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$NoBuild, [switch]$Writable, [string]$Overlay, [switch]$Inspect)
. (Join-Path $PSScriptRoot 'runtime.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$release = Get-DosRuntimeRelease -BuildIfMissing:(-not $NoBuild)
$runner = Join-Path $release 'net10.0/DOSEmulator.Headless.dll'
$emulatorExe = Join-Path $release 'net10.0-windows/DOSEmulator.exe'
if ($Inspect) { [pscustomobject]@{variant='run-freedos';runtime=$release;executable=$emulatorExe;nativeWindowStarted=$false;mediaOpened=$false} | ConvertTo-Json; return }
& (Join-Path $PSScriptRoot 'prepare-freedos.ps1') -Download -Runner $runner | Out-Host
$image = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
if ($Writable -or $Overlay) {
    if (-not $Overlay) {
        $overlayDirectory = Join-Path $workspaceRoot 'artifacts\freedos-user'
        New-Item -ItemType Directory -Path $overlayDirectory -Force | Out-Null
        $Overlay = Join-Path $overlayDirectory 'freedos.cow'
    }
    & $emulatorExe --remember-configuration (Join-Path $workspaceRoot 'artifacts/launcher-pcs/freedos') --data-dir (Join-Path $workspaceRoot 'artifacts/freedos-ui') --disk $image --overlay (Resolve-DosPath -Path $Overlay)
} else { & $emulatorExe --remember-configuration (Join-Path $workspaceRoot 'artifacts/launcher-pcs/freedos') --data-dir (Join-Path $workspaceRoot 'artifacts/freedos-ui') --disk $image }
