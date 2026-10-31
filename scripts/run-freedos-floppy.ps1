# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$NoBuild, [switch]$Writable, [switch]$TwoDrives, [switch]$Inspect, [ValidateSet('whpx','software')][string]$Engine = 'whpx')
$engineOption = if ($PSBoundParameters.ContainsKey('Engine')) { '--engine' } else { '--default-engine' }
. (Join-Path $PSScriptRoot 'runtime.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$release = Get-DosRuntimeRelease -BuildIfMissing:(-not $NoBuild)
$runner = Join-Path $release 'net10.0/DOSEmulator.Headless.dll'
$emulatorExe = Join-Path $release 'net10.0-windows/DOSEmulator.exe'
if ($Inspect) { [pscustomobject]@{variant='run-freedos-floppy';runtime=$release;engine=$Engine;executable=$emulatorExe;nativeWindowStarted=$false;mediaOpened=$false} | ConvertTo-Json; return }
& (Join-Path $PSScriptRoot 'prepare-freedos-floppy.ps1') -Download -Runner $runner | Out-Host
$image = Join-Path $workspaceRoot 'artifacts\freedos-m2\floppy\FD14-minimal.img'
$emulatorArguments = @($engineOption, $Engine, '--remember-configuration',(Join-Path $workspaceRoot 'artifacts/launcher-pcs/freedos-floppy'),'--data-dir',(Join-Path $workspaceRoot 'artifacts/freedos-floppy-ui'),'--floppy-a',$image)
if ($TwoDrives) { $emulatorArguments += @('--floppy-b',$image) }
if ($Writable) {
    $overlayDirectory = Join-Path $workspaceRoot 'artifacts\freedos-floppy-user'
    New-Item -ItemType Directory -Path $overlayDirectory -Force | Out-Null
    $emulatorArguments += @('--floppy-a-overlay',(Join-Path $overlayDirectory 'a.cow'))
    if ($TwoDrives) { $emulatorArguments += @('--floppy-b-overlay',(Join-Path $overlayDirectory 'b.cow')) }
}
& $emulatorExe @emulatorArguments
