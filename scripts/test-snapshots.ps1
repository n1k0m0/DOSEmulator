# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [Parameter(Mandatory)][string]$ConfiguredDisk,[Parameter(Mandatory)][string]$NoEmsOverlay,[Parameter(Mandatory)][string]$RamOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$env:PYTHONUTF8='1';$env:PYTHONIOENCODING='utf-8'
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$arguments=@((Resolve-DosPath 'tests/run-snapshots.py'),'--build',$buildRoot,'--disk',(Resolve-DosPath $Disk),'--overlay',(Resolve-DosPath $Overlay),
    '--configured-disk',(Resolve-DosPath $ConfiguredDisk),'--noems-overlay',(Resolve-DosPath $NoEmsOverlay),'--ram-overlay',(Resolve-DosPath $RamOverlay))
python @arguments
if($LASTEXITCODE -ne 0){throw 'Actual DOS full snapshot continuation oracle failed.'}
