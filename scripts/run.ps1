#requires -Version 7.4
[CmdletBinding()]
param([string]$Disk, [string]$Overlay, [string]$Profile, [switch]$NoBuild, [string]$SmokeTestDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$emulatorExe = Join-Path $workspaceRoot 'artifacts\m1-build\Release\net10.0-windows\DOSEmulator.exe'
$emulatorArguments = @()
if ($Disk) { $emulatorArguments += @('--disk', (Resolve-Path -LiteralPath $Disk).Path) }
if ($Overlay) { $emulatorArguments += @('--overlay', (Resolve-DosPath -Path $Overlay)) }
if ($Profile) { $emulatorArguments += @('--profile', (Resolve-Path -LiteralPath $Profile).Path) }
if ($SmokeTestDirectory) {
    $smokePath = Resolve-DosPath -Path $SmokeTestDirectory
    $emulatorArguments += @('--data-dir', (Join-Path $smokePath 'settings'), '--smoke-test', $smokePath)
}
& $emulatorExe @emulatorArguments
