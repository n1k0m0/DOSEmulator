# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk, [Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk, [string]$NoEmsOverlay, [string]$RamOverlay,
    [ValidateRange(1,2)][int]$Repeats=2)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk = Resolve-DosPath $Disk; $Overlay = Resolve-DosPath $Overlay
$systems = @([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Resolve-DosPath 'artifacts/freedos-m1/FD14LITE-minimal.img');overlay=$null})
$optional = @($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if ($optional.Count -notin 0,3) { throw 'Supply all three optional EMM386 source paths together.' }
if ($optional.Count -eq 3) { $systems += @(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)}) }
$sources = @($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique
foreach ($source in $sources) { if ($source -match '(?i)doom2-play') { throw 'Personal play journals are excluded.' } }
$originals = @($sources | Get-DosFileHash)
$root = Join-Path $script:DosWorkspaceRoot ('artifacts/gameport-m3/acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner = Join-Path $buildRoot 'Release/net10.0/DOSEmulator.Headless.dll'
$assemblies = @(Get-ChildItem -LiteralPath (Split-Path $runner -Parent) -Filter '*.dll' | Get-DosFileHash)
$assemblies | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$probe=Join-Path $root 'GAMEPORT.COM'; $floppy=Join-Path $root 'own-probe.img'
& (Resolve-DosPath 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe') -Werror -f bin (Resolve-DosPath 'tests/guests/gameport-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Gameport probe assembly failed.'}
python (Resolve-DosPath 'tests/prepare-font-media.py') $probe $floppy GAMEPORT.COM > (Join-Path $root 'media.json')
if($LASTEXITCODE -ne 0){throw 'Own FAT12 probe creation failed.'}
$inputs=@(
    @{axOhms=$null;ayOhms=$null;bxOhms=$null;byOhms=$null;buttons=0},
    @{axOhms=0;ayOhms=25000;bxOhms=50000;byOhms=100000;buttons=5},
    @{axOhms=100000;ayOhms=50000;bxOhms=25000;byOhms=0;buttons=10},
    @{axOhms=1;ayOhms=100;bxOhms=49999;byOhms=99999;buttons=15},
    @{axOhms=50000;ayOhms=50000;bxOhms=50000;byOhms=50000;buttons=0},
    @{axOhms=12345;ayOhms=67890;bxOhms=$null;byOhms=$null;buttons=3},
    @{axOhms=$null;ayOhms=0;bxOhms=$null;byOhms=100000;buttons=12},
    @{axOhms=100000;ayOhms=100000;bxOhms=100000;byOhms=100000;buttons=0})
foreach($buttons in 0..15){$inputs+=@{axOhms=0;ayOhms=0;bxOhms=0;byOhms=0;buttons=$buttons}}
$rows=@()
foreach($system in $systems){
    $plan=@();$prompt='C:\>'
    if($system.name -ne 'freedos'){$plan+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
    $plan+=@{waitForScreen=$prompt;text="a:gameport`r`n"}
    for($index=0;$index -lt 24;$index++){$plan+=@{waitForDebug=('GAMEPORT-READY-'+$index.ToString('X2'));settleNanoseconds=0;gameport=$inputs[$index];text="`r"}}
    $plan+=@{waitForDebug='GAMEPORT-OK';text="ver`r`necho GAMEPORT-DOS-RETURN`r`n"}
    $scenario=Join-Path $root ($system.name+'.scenario.json')
    ConvertTo-Json -InputObject $plan -Depth 8 | Set-Content -LiteralPath $scenario -Encoding utf8
    foreach($repeat in 1..$Repeats){
        $prefix=Join-Path $root ($system.name+'-'+$repeat);$cow=$prefix+'.cow'
        if($system.overlay){Copy-Item -LiteralPath $system.overlay -Destination $cow}
        $initialCow=if($system.overlay){(Get-DosFileHash -LiteralPath $cow).Hash}else{$null}
        $stop=if($system.name -eq 'freedos'){"`r`nGAMEPORT-DOS-RETURN`r`nC:\>"}else{"`r`nGAMEPORT-DOS-RETURN`r`n`r`nC:\>"}
        $arguments=@($runner,'--profile',(Resolve-DosPath 'profiles/virtual486-gameport-dev-v1.json'),'--disk',$system.disk,'--overlay',$cow,
            '--floppy-a',$floppy,'--scenario',$scenario,'--steps','150000000','--stop-text',$stop,'--report',($prefix+'.json'),
            '--memory-dump',($prefix+'.ram.bin'),'--gameport-io-trace',($prefix+'.io.jsonl'))
        dotnet @arguments > ($prefix+'.stdout.json')
        if($LASTEXITCODE -ne 0){throw 'Actual DOS gameport execution failed.'}
        $oracle=@('--report',($prefix+'.json'),'--memory',($prefix+'.ram.bin'),'--io',($prefix+'.io.jsonl'),'--program',$probe)
        if($system.name -like 'emm386-*'){$oracle+='--manager'}
        if($repeat -eq 1){$oracle+='--negative-controls'}
        python (Resolve-DosPath 'tests/verify-gameport.py') @oracle > ($prefix+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent gameport timing/port/RAM oracle failed.'}
        if($initialCow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $initialCow){throw 'System journal changed.'}
        $rows+=[pscustomobject]@{system=$system.name;repeat=$repeat;report=$prefix+'.json';verification=(Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json)}
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Input/tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count;cases=$rows;originalsUnchanged=$true;nativeInputSent=$false;
    scope='Nominal IBM analog adapter, external virtual resistances/buttons, byte port path only; INT15/AH84 switches and bounded raw counts; no host gamepad or MIDI endpoint'}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 12
