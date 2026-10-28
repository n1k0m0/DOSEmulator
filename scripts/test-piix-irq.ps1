# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [Alias('OutputRoot')][string]$Build = 'artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,
    [Parameter(Mandatory)][string]$Overlay,
    [Parameter(Mandatory)][string]$FreeDosDisk,
    [Parameter(Mandatory)][string]$ConfiguredDisk,
    [Parameter(Mandatory)][string]$NoEmsOverlay,
    [Parameter(Mandatory)][string]$RamOverlay,
    [string]$Output,
    [ValidateSet(1, 2)][int]$Repeats = 2,
    [ValidateSet('msdos', 'freedos', 'emm386-noems', 'emm386-ram')][string]$Only
)
. (Join-Path $PSScriptRoot 'common.ps1')
$previousPythonUtf8 = $env:PYTHONUTF8
$previousPythonEncoding = $env:PYTHONIOENCODING
$previousPythonBytecode = $env:PYTHONDONTWRITEBYTECODE
try {
    $env:PYTHONUTF8 = '1'
    $env:PYTHONIOENCODING = 'utf-8'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $buildRoot = Get-DosBuildRoot -OutputRoot $Build
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot
    }
    $arguments = @(
        (Join-Path $script:DosWorkspaceRoot 'tests/run-piix-irq.py'),
        '--build', $buildRoot,
        '--disk', (Resolve-DosPath $Disk),
        '--overlay', (Resolve-DosPath $Overlay),
        '--freedos-disk', (Resolve-DosPath $FreeDosDisk),
        '--configured-disk', (Resolve-DosPath $ConfiguredDisk),
        '--noems-overlay', (Resolve-DosPath $NoEmsOverlay),
        '--ram-overlay', (Resolve-DosPath $RamOverlay),
        '--repeats', $Repeats.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    if ($Output) { $arguments += @('--output', (Resolve-DosPath $Output)) }
    if ($Only) { $arguments += @('--only', $Only) }
    python @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Actual PIIX IRQ DOS/full-PC continuation or independent repetition/identity guard failed.'
    }
}
finally {
    $env:PYTHONUTF8 = $previousPythonUtf8
    $env:PYTHONIOENCODING = $previousPythonEncoding
    $env:PYTHONDONTWRITEBYTECODE = $previousPythonBytecode
}
