# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$NoBuild, [switch]$Writable, [switch]$TwoDrives)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $workspaceRoot 'artifacts\m1-build'
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $outputRoot }
$runner = Join-Path $outputRoot 'Release\net10.0\DOSEmulator.Headless.dll'
& (Join-Path $PSScriptRoot 'prepare-freedos-floppy.ps1') -Download -Runner $runner | Out-Host
$image = Join-Path $workspaceRoot 'artifacts\freedos-m2\floppy\FD14-minimal.img'
$emulatorExe = Join-Path $outputRoot 'Release\net10.0-windows\DOSEmulator.exe'
$emulatorArguments = @('--floppy-a',$image)
if ($TwoDrives) { $emulatorArguments += @('--floppy-b',$image) }
if ($Writable) {
    $overlayDirectory = Join-Path $workspaceRoot 'artifacts\freedos-floppy-user'
    New-Item -ItemType Directory -Path $overlayDirectory -Force | Out-Null
    $emulatorArguments += @('--floppy-a-overlay',(Join-Path $overlayDirectory 'a.cow'))
    if ($TwoDrives) { $emulatorArguments += @('--floppy-b-overlay',(Join-Path $overlayDirectory 'b.cow')) }
}
& $emulatorExe @emulatorArguments
