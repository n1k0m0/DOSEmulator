# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk,[string]$NoEmsOverlay,[string]$RamOverlay,[ValidateRange(1,2)][int]$Repeats=2)
. (Join-Path $PSScriptRoot 'common.ps1')
$env:PYTHONUTF8='1';$env:PYTHONIOENCODING='utf-8'
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$systems=@([pscustomobject]@{name='msdos';disk=(Resolve-DosPath $Disk);overlay=(Resolve-DosPath $Overlay)},
    [pscustomobject]@{name='freedos';disk=(Resolve-DosPath 'artifacts/freedos-m1/FD14LITE-minimal.img');overlay=$null})
$optional=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay|Where-Object{$_})
if($optional.Count -notin 0,3){throw 'Supply all three optional manager paths together.'}
if($optional.Count -eq 3){$systems+=@(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
$sources=@($systems|ForEach-Object{$_.disk;if($_.overlay){$_.overlay}})|Select-Object -Unique
foreach($source in $sources){if($source -match '(?i)doom2-play'){throw 'Personal play journals are excluded.'}}
$originals=@($sources|Get-DosFileHash)
$root=Join-Path $script:DosWorkspaceRoot ('artifacts/midi-m3/acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$runner=Join-Path $buildRoot 'Release/net10.0/DOSEmulator.Headless.dll'
$assemblies=@(Get-ChildItem -LiteralPath (Split-Path $runner -Parent) -Filter '*.dll'|Get-DosFileHash)
$assemblies|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$variants=@(@{name='a220-i5-p330';card=0x220;irq=5;port=0x330;dma=1;high=5},
    @{name='a240-i10-p300';card=0x240;irq=10;port=0x300;dma=3;high=6},
    @{name='a280-i9-p330';card=0x280;irq=9;port=0x330;dma=0;high=7},
    @{name='a260-i7-p300';card=0x260;irq=7;port=0x300;dma=1;high=5})
$rows=@()
foreach($variant in $variants){
    $probe=Join-Path $root ($variant.name+'.com');$floppy=Join-Path $root ($variant.name+'.img')
    & (Resolve-DosPath 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe') -Werror -f bin (Resolve-DosPath 'tests/guests/midi-test.asm') -o $probe `
        ('-DMPUBASE='+$variant.port) ('-DSBBASE='+$variant.card) ('-DSBIRQ='+$variant.irq)
    if($LASTEXITCODE -ne 0){throw 'Own MIDI probe assembly failed.'}
    python (Resolve-DosPath 'tests/prepare-font-media.py') $probe $floppy MIDI.COM > (Join-Path $root ($variant.name+'.media.json'))
    if($LASTEXITCODE -ne 0){throw 'Own MIDI FAT12 media creation failed.'}
    $profile=Get-Content -LiteralPath (Resolve-DosPath 'profiles/virtual486-midi-dev-v1.json') -Raw|ConvertFrom-Json
    $profile|Add-Member -Force NoteProperty soundBlasterBasePort $variant.card
    $profile|Add-Member -Force NoteProperty soundBlasterIrq $variant.irq
    $profile|Add-Member -Force NoteProperty soundBlasterDmaChannel $variant.dma
    $profile|Add-Member -Force NoteProperty soundBlasterDma16Channel $variant.high
    $profile.midiBasePort=$variant.port
    $profilePath=Join-Path $root ($variant.name+'.profile.json');$profile|ConvertTo-Json|Set-Content -LiteralPath $profilePath -Encoding utf8
    foreach($system in $systems){
        $plan=@();$prompt='C:\>'
        if($system.name -ne 'freedos'){$plan+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
        $plan+=@{waitForScreen=$prompt;text="a:midi`r`n"}
        $plan+=@{waitForDebug='MPU-FILL';settleNanoseconds=0;text="`r";midi=@{data=@(0..31)}}
        $plan+=@{waitForDebug='MPU-CANCEL';settleNanoseconds=0;text="`r";midi=@{data=@(65)}}
        $plan+=@{waitForDebug='MPU-IRQ';settleNanoseconds=0;text="`r";midi=@{data=@(0..127|ForEach-Object{($_*37+240)-band 255})}}
        $plan+=@{waitForDebug='MPU-OK';text="ver`r`necho MIDI-DOS-RETURN`r`n"}
        $scenario=Join-Path $root ($system.name+'.scenario.json');ConvertTo-Json -InputObject $plan -Depth 6|Set-Content -LiteralPath $scenario -Encoding utf8
        foreach($repeat in 1..$Repeats){
            $prefix=Join-Path $root ($system.name+'-'+$variant.name+'-'+$repeat);$cow=$prefix+'.cow'
            if($system.overlay){Copy-Item -LiteralPath $system.overlay -Destination $cow}
            $initial=if($system.overlay){(Get-DosFileHash -LiteralPath $cow).Hash}else{$null}
            $stop=if($system.name -eq 'freedos'){"`r`nMIDI-DOS-RETURN`r`nC:\>"}else{"`r`nMIDI-DOS-RETURN`r`n`r`nC:\>"}
            dotnet $runner --profile $profilePath --disk $system.disk --overlay $cow --floppy-a $floppy --scenario $scenario --steps 150000000 --stop-text $stop `
                --report ($prefix+'.json') --memory-dump ($prefix+'.ram.bin') --midi-output ($prefix+'.midi.bin') --midi-io-trace ($prefix+'.io.jsonl') > ($prefix+'.stdout.json')
            if($LASTEXITCODE -ne 0){throw 'Actual DOS MIDI execution failed.'}
            $oracle=@('--report',($prefix+'.json'),'--memory',($prefix+'.ram.bin'),'--program',$probe,'--output',($prefix+'.midi.bin'),'--io',($prefix+'.io.jsonl'))
            if($system.name -like 'emm386-*'){$oracle+='--manager'}
            if($repeat -eq 1){$oracle+='--negative-controls'}
            python (Resolve-DosPath 'tests/verify-midi.py') @oracle > ($prefix+'.verified.json')
            if($LASTEXITCODE -ne 0){throw 'Independent MIDI bytes/framing/shared-IRQ/RAM/DOS oracle failed.'}
            if($initial -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $initial){throw 'System journal changed.'}
            $rows+=[pscustomobject]@{system=$system.name;variant=$variant.name;repeat=$repeat;report=$prefix+'.json';verification=(Get-Content -LiteralPath ($prefix+'.verified.json') -Raw|ConvertFrom-Json)}
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Input/tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count;cases=$rows;originalsUnchanged=$true;nativeInputSent=$false;
    scope='CT1740 optional MPU UART with nominal framed virtual peer, raw data/shared IRQ, no host MIDI/synthesizer/intelligent/silicon precision claim'}
$summary|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary|ConvertTo-Json -Depth 12
