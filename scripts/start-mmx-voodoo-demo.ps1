# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet(166,200,233)][int]$MHz = 233, [ValidateSet('whpx','software')][string]$Engine = 'whpx', [switch]$Inspect, [switch]$NoBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
$taskDemoWorkspace = Split-Path -Parent $PSScriptRoot
$taskDemoRoot = Join-Path $taskDemoWorkspace 'artifacts/mmx-voodoo-demo'
$taskDemoSelectionPath = Join-Path $taskDemoRoot 'runtime.json'
$taskDemoModules = @('DOSEmulator.Contracts','DOSEmulator.Core','DOSEmulator.Cpu.X86','DOSEmulator.Hardware','DOSEmulator.Runtime','DOSEmulator.Whpx')

# Verifies only the six named modules in this separate demo runtime; existing runtime selections and PCs are untouched.
function Test-MmxDemoRuntime {
    param($Selection)
    if ($Selection.schemaVersion -ne 1) { throw 'Unknown MMX/Voodoo demo runtime format.' }
    $taskSelectedRelease = [IO.Path]::GetFullPath((Join-Path $taskDemoWorkspace $Selection.release))
    if (-not $taskSelectedRelease.StartsWith($taskDemoWorkspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Demo runtime is outside the workspace.' }
    foreach ($taskModule in $taskDemoModules) {
        foreach ($taskFramework in @('net10.0','net10.0-windows')) {
            $taskModulePath = Join-Path $taskSelectedRelease ($taskFramework + '/' + $taskModule + '.dll')
            if ($Selection.modules.$taskModule -notmatch '^[0-9A-F]{64}$' -or (Get-DosFileHash -LiteralPath $taskModulePath).Hash -cne $Selection.modules.$taskModule) { throw ('The demo module changed: ' + $taskModule) }
        }
    }
    $taskImage = Join-Path $taskSelectedRelease 'guest-tools/hardware/mmx-voodoo-test.img'
    if ($Selection.demoImageSha256 -notmatch '^[0-9A-F]{64}$' -or (Get-DosFileHash -LiteralPath $taskImage).Hash -cne $Selection.demoImageSha256) { throw 'The own hardware-demo disk changed.' }
    return $taskSelectedRelease
}

if (-not (Test-Path -LiteralPath $taskDemoSelectionPath)) {
    if ($NoBuild -or $Inspect) { throw 'The separate demo runtime is not prepared. Run run-voodoo-demo.cmd once, or scripts/build.ps1 first.' }
    New-Item -ItemType Directory -Path $taskDemoRoot -Force | Out-Null
    $taskDemoBuildRoot = Join-Path $taskDemoRoot ('build-' + [Guid]::NewGuid().ToString('N'))
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $taskDemoBuildRoot | Out-Host
    $taskDemoRelease = Join-Path $taskDemoBuildRoot 'Release'
    $taskDemoModuleHashes = @{}
    foreach ($taskModule in $taskDemoModules) { $taskDemoModuleHashes[$taskModule] = (Get-DosFileHash -LiteralPath (Join-Path $taskDemoRelease ('net10.0/' + $taskModule + '.dll'))).Hash }
    $taskDemoSelection = @{schemaVersion=1;release=[IO.Path]::GetRelativePath($taskDemoWorkspace,$taskDemoRelease);modules=$taskDemoModuleHashes;demoImageSha256=(Get-DosFileHash -LiteralPath (Join-Path $taskDemoRelease 'guest-tools/hardware/mmx-voodoo-test.img')).Hash}
    $taskDemoSelection | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskDemoSelectionPath -Encoding utf8NoBOM
}
$taskDemoSelection = Get-Content -LiteralPath $taskDemoSelectionPath -Raw | ConvertFrom-Json
$taskDemoRelease = Test-MmxDemoRuntime $taskDemoSelection
$taskDemoDisk = Join-Path $taskDemoRelease 'guest-tools/hardware/mmx-voodoo-test.img'
if (-not (Test-Path -LiteralPath $taskDemoDisk)) { throw 'The original hardware-demo disk is missing. Build with scripts/build.ps1.' }
$taskDemoMachinePath = Join-Path $taskDemoRoot ('demo-' + $MHz + '.dospc.json')
if (-not (Test-Path -LiteralPath $taskDemoMachinePath)) {
    $taskDemoProfile = Get-Content -LiteralPath (Join-Path $taskDemoWorkspace ('profiles/dose-p55c-430fx-s3-' + $MHz + '-dev-v1.json')) -Raw | ConvertFrom-Json
    $taskDemoProfile.bootDevice = 'floppy-a'; $taskDemoProfile.floppyDriveA = '1440k'
    $taskDemoProfile | Add-Member -NotePropertyName threeDAccelerator -NotePropertyValue 'voodoo-graphics-sst1-4m-development' -Force
    $taskDemoDefinition = @{schemaVersion=1;id=[Guid]::NewGuid().ToString('N');name=('DOSE MMX + Voodoo Graphics demo / ' + $MHz + ' MHz');hardware=$taskDemoProfile;
        hardDisks=@();opticalDiscs=@();floppies=@(@{slot=0;imagePath=$taskDemoDisk;overlayPath=$null});builtInTestDisk=$false}
    $taskDemoDefinition | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskDemoMachinePath -Encoding utf8NoBOM
}
$taskDemoExecutable = Join-Path $taskDemoRelease 'net10.0-windows/DOSEmulator.exe'
$taskDemoSettings = Join-Path $taskDemoRoot ('ui-' + $MHz)
$taskDemoArguments = @('--engine',$Engine,'--machine',$taskDemoMachinePath,'--data-dir',$taskDemoSettings)
if ($Inspect) { [pscustomobject]@{runtime=$taskDemoRelease;engine=$Engine;executable=$taskDemoExecutable;machine=$taskDemoMachinePath;settings=$taskDemoSettings;disk=$taskDemoDisk;arguments=$taskDemoArguments;nativeWindowStarted=$false;userMediaOpened=$false} | ConvertTo-Json -Depth 5; return }
& $taskDemoExecutable @taskDemoArguments
exit $LASTEXITCODE
