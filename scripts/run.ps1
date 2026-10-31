# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([string]$Disk, [string]$Overlay, [string]$Profile, [switch]$NoBuild, [string]$SmokeTestDirectory,
    [string]$Machine, [string]$Disk1, [string]$Overlay1, [string]$Disk2, [string]$Overlay2, [string]$Disk3, [string]$Overlay3, [switch]$Inspect)
. (Join-Path $PSScriptRoot 'runtime.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$release = Get-DosRuntimeRelease -BuildIfMissing:(-not $NoBuild)
$emulatorExe = Join-Path $release 'net10.0-windows/DOSEmulator.exe'
$emulatorArguments = @('--remember-configuration', (Join-Path $workspaceRoot 'artifacts/launcher-pcs/general'))
if ($Disk) { $emulatorArguments += @('--disk', (Resolve-Path -LiteralPath $Disk).Path) }
if ($Overlay) { $emulatorArguments += @('--overlay', (Resolve-DosPath -Path $Overlay)) }
if ($Profile) { $emulatorArguments += @('--profile', (Resolve-Path -LiteralPath $Profile).Path) }
if ($Machine) { $emulatorArguments += @('--machine', (Resolve-Path -LiteralPath $Machine).Path) }
foreach($taskSlot in 1..3){
    $taskDisk=(Get-Variable -Name ('Disk'+$taskSlot) -ValueOnly)
    $taskOverlay=(Get-Variable -Name ('Overlay'+$taskSlot) -ValueOnly)
    if($taskDisk){$emulatorArguments+=@(('--disk'+$taskSlot),(Resolve-Path -LiteralPath $taskDisk).Path)}
    if($taskOverlay){$emulatorArguments+=@(('--overlay'+$taskSlot),(Resolve-DosPath -Path $taskOverlay))}
}
if ($SmokeTestDirectory) {
    $smokePath = Resolve-DosPath -Path $SmokeTestDirectory
    $emulatorArguments += @('--data-dir', (Join-Path $smokePath 'settings'), '--smoke-test', $smokePath)
}
if ($Inspect) { [pscustomobject]@{runtime=$release;executable=$emulatorExe;arguments=$emulatorArguments;nativeWindowStarted=$false;mediaOpened=$false} | ConvertTo-Json; return }
& $emulatorExe @emulatorArguments
