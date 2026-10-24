param([string]$Disk, [string]$Overlay, [string]$Profile, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$emulatorExe = Join-Path $workspaceRoot 'artifacts\m1-build\Release\net10.0-windows\DOSEmulator.exe'
$emulatorArguments = @()
if ($Disk) { $emulatorArguments += @('--disk', (Resolve-Path -LiteralPath $Disk).Path) }
if ($Overlay) { $emulatorArguments += @('--overlay', [IO.Path]::GetFullPath($Overlay)) }
if ($Profile) { $emulatorArguments += @('--profile', (Resolve-Path -LiteralPath $Profile).Path) }
& $emulatorExe @emulatorArguments
