#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BaselineAssemblies,[Parameter(Mandatory)][string]$CandidateAssemblies,
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$RamOverlay,
    [Parameter(Mandatory)][string]$Scenario,[Parameter(Mandatory)][string]$Game,[ValidateRange(2,9)][int]$Repeats=2)
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
            dotnet $dll --disk $Disk --overlay $cow --scenario $Scenario --steps 1000000000 --stop-text "`r`nDOOM-GUEST-END`r`n`r`nC:\>" --report ($prefix+'.json') --graphics-capture ($prefix+'-graphics') > ($prefix+'.stdout.json')
            $watch.Stop()
            $arguments=@((Join-Path $script:DosWorkspaceRoot 'tests\verify-doom-extender.py'),'--report',($prefix+'.json'),'--base',$Disk,'--overlay',$cow,'--game',$Game,'--mode',$mode)
            if($mode -eq 'RAM'){$arguments+=@('--baseline',$RamOverlay)}
            python @arguments > ($prefix+'.verified.json')
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $proof=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            if(-not $proof.passed){throw 'Doom benchmark oracle failed'}
            if($mode -eq 'RAM' -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $before){throw 'Benchmark changed its private configuration'}
            $cpu=$r.cpu | ConvertTo-Json -Depth 15 -Compress
            $row=[pscustomobject]@{mode=$mode;repeat=$repeat;variant=$variant;milliseconds=$watch.Elapsed.TotalMilliseconds;
                report=$prefix+'.json';verification=$prefix+'.verified.json';frameHash=$r.frameHash;
                sceneSequenceSha256=$proof.sceneSequenceSha256;virtualNanoseconds=$r.status.virtualNanoseconds;
                instructions=$r.cpu.instructions;cpuState=$cpu}
            if($reference){
                if($reference.cpuState -cne $cpu -or $reference.frameHash -ne $row.frameHash -or $reference.sceneSequenceSha256 -ne $row.sceneSequenceSha256 -or $reference.virtualNanoseconds -ne $row.virtualNanoseconds){throw 'Optimization changed guest CPU state, images or virtual time'}
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
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
