# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BaselineAssemblies,[Parameter(Mandatory)][string]$CandidateAssemblies,
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$RamOverlay,
    [Parameter(Mandatory)][string]$Scenario,[Parameter(Mandatory)][string]$Game,[ValidateRange(2,9)][int]$Repeats=2,
    [switch]$AllowTranslationCounterDifferences,[switch]$CompareGuestRam)
. (Join-Path $PSScriptRoot 'common.ps1')
$BaselineAssemblies=Resolve-DosPath $BaselineAssemblies;$CandidateAssemblies=Resolve-DosPath $CandidateAssemblies
$Disk=Resolve-DosPath $Disk;$RamOverlay=Resolve-DosPath $RamOverlay;$Scenario=Resolve-DosPath $Scenario;$Game=Resolve-DosPath $Game
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\performance\doom-pairs-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$protected=@($Disk,$RamOverlay,$Scenario,(Join-Path $Game 'DOOM2.EXE'),(Join-Path $Game 'DOOM2.WAD')) | Get-DosFileHash
$assemblies=@(Get-ChildItem -LiteralPath $BaselineAssemblies,$CandidateAssemblies -Filter '*.dll' -File | Get-DosFileHash)
$rows=[Collections.Generic.List[object]]::new()
foreach($mode in @('none','RAM')){
    $reference=$null
    foreach($repeat in 1..$Repeats){
        # Alternate order to reduce a systematic JIT/cache/temperature advantage.
        $order=if($repeat%2 -eq 1){@('baseline','candidate')}else{@('candidate','baseline')}
        foreach($variant in $order){
            $prefix=Join-Path $root ($mode+'-'+$repeat+'-'+$variant);$cow=$prefix+'.cow'
            if($mode -eq 'RAM'){Copy-Item -LiteralPath $RamOverlay -Destination $cow;$before=(Get-DosFileHash -LiteralPath $cow).Hash}
            $dll=Join-Path $(if($variant -eq 'baseline'){$BaselineAssemblies}else{$CandidateAssemblies}) 'DOSEmulator.Headless.dll'
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $ramArguments=if($CompareGuestRam){@('--memory-dump',($prefix+'.private-ram.bin'))}else{@()}
            dotnet $dll --disk $Disk --overlay $cow --scenario $Scenario --steps 1000000000 --stop-text "`r`nDOOM-GUEST-END`r`n`r`nC:\>" --report ($prefix+'.json') --graphics-capture ($prefix+'-graphics') @ramArguments > ($prefix+'.stdout.json')
            $watch.Stop()
            $arguments=@((Join-Path $script:DosWorkspaceRoot 'tests\verify-doom-extender.py'),'--report',($prefix+'.json'),'--base',$Disk,'--overlay',$cow,'--game',$Game,'--mode',$mode)
            if($mode -eq 'RAM'){$arguments+=@('--baseline',$RamOverlay)}
            python @arguments > ($prefix+'.verified.json')
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $proof=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            if(-not $proof.passed){throw 'Doom benchmark oracle failed'}
            if($mode -eq 'RAM' -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $before){throw 'Benchmark changed its private configuration'}
            $architecturalCpu=if($AllowTranslationCounterDifferences){$r.cpu | Select-Object -Property * -ExcludeProperty tlbHits}else{$r.cpu}
            $cpu=$architecturalCpu | ConvertTo-Json -Depth 15 -Compress
            $ramHash=if($CompareGuestRam){(Get-DosFileHash -LiteralPath ($prefix+'.private-ram.bin')).Hash}else{$null}
            $row=[pscustomobject]@{mode=$mode;repeat=$repeat;variant=$variant;milliseconds=$watch.Elapsed.TotalMilliseconds;
                report=$prefix+'.json';verification=$prefix+'.verified.json';frameHash=$r.frameHash;
                sceneSequenceSha256=$proof.sceneSequenceSha256;virtualNanoseconds=$r.status.virtualNanoseconds;
                instructions=$r.cpu.instructions;cpuState=$cpu;translationHits=$r.cpu.tlbHits;guestRamSha256=$ramHash}
            if($reference){
                if($reference.cpuState -cne $cpu -or $reference.frameHash -ne $row.frameHash -or $reference.sceneSequenceSha256 -ne $row.sceneSequenceSha256 -or $reference.virtualNanoseconds -ne $row.virtualNanoseconds -or $reference.guestRamSha256 -ne $ramHash){throw 'Optimization changed guest CPU state, images, RAM or virtual time'}
            }else{$reference=$row}
            $rows.Add($row)
            $rows | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'progress.json') -Encoding utf8
        }
    }
}
foreach($item in @($protected)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Benchmark source changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;coldBoots=$rows.Count;rows=$rows;
    originalMediaAndBaselinesUnchanged=$true;testedAssembliesUnchanged=$true;testedAssemblies=$assemblies;
    protectedInputs=$protected;cpuStateImagesAndVirtualTimeIdentical=$true;guestTimingModelChanged=$false;
    timingIncludesProcessStartAndGraphicsCapture=$true;nativeInputUsed=$false;fullGameCompatibilityAccepted=$false}
$summary | Add-Member -NotePropertyName translationCallStatisticsExcluded -NotePropertyValue ([bool]$AllowTranslationCounterDifferences)
$summary | Add-Member -NotePropertyName completeGuestRamCompared -NotePropertyValue ([bool]$CompareGuestRam)
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
