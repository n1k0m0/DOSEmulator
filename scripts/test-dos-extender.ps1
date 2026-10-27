# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay,
    [Parameter(Mandatory=$true)][string]$Game)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay;$Game=Resolve-DosPath $Game
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\dos-extender-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$headless=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$originals=@($Disk,$Overlay,(Join-Path $Game 'DOOM2.EXE'),(Join-Path $Game 'DOOM2.WAD')) | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$prepared=Join-Path $root 'private-doom-installed.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-dos-extender-media.py') --base $Disk --overlay $Overlay --game $Game --destination $prepared > (Join-Path $root 'media.verified.json')
$originals+=Get-DosFileHash -LiteralPath $prepared
$probe=Join-Path $root 'CPUX87.COM';$floppy=Join-Path $root 'only-own-x87-client.img'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\x87-control-model-test.asm') -o $probe
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy CPUX87.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$baselines=@{}
foreach($mode in @('HIMEM','NOEMS','RAM')){
    $prefix=Join-Path $root ('configure-'+$mode);$cow=$prefix+'.cow'
    $commands="copy config.sys config.pre`r`necho DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON >config.sys`r`n"
    if($mode -ne 'HIMEM'){$commands+="echo DEVICE=C:\DOS\EMM386.EXE $mode >>config.sys`r`n"}
    $commands+="type config.pre >>config.sys`r`necho MEMORY-CONFIG-READY`r`n"
    @(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text=$commands}) |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix+'.scenario.json') -Encoding utf8
    dotnet $headless --disk $prepared --overlay $cow --scenario ($prefix+'.scenario.json') --steps 50000000 --stop-text "`r`nMEMORY-CONFIG-READY`r`n`r`nC:\>" --report ($prefix+'.json') > ($prefix+'.stdout.json')
    $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($r.status.fault -or -not $r.scenario.complete -or $r.ataSectorsWritten -le 0){throw 'DOS configuration did not finish'}
    python (Join-Path $script:DosWorkspaceRoot 'tests\verify-dos-extender-config.py') --base $prepared --overlay $cow --mode $mode > ($prefix+'.verified.json')
    $baselines[$mode]=$cow;$originals+=Get-DosFileHash -LiteralPath $cow
}
$doomPlan=Join-Path $root 'doom.scenario.json'
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="doom2 -config doomtest.cfg -nosound -timedemo fastdemo`r`n"},
  @{waitForDebug='timed ';text="ver`r`necho DOOM-GUEST-END`r`n"}) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $doomPlan -Encoding utf8
$doomCases=@()
foreach($mode in @('none','HIMEM','NOEMS','RAM')){
    $pair=@()
    foreach($number in 1..2){
        $prefix=Join-Path $root ("doom-$mode-$number");$cow=$prefix+'.cow';$initialHash=$null
        if($mode -ne 'none'){Copy-Item -LiteralPath $baselines[$mode] -Destination $cow;$initialHash=(Get-DosFileHash -LiteralPath $cow).Hash}
        dotnet $headless --disk $prepared --overlay $cow --scenario $doomPlan --steps 1000000000 --stop-text "`r`nDOOM-GUEST-END`r`n`r`nC:\>" --report ($prefix+'.json') --graphics-capture ($prefix+'-graphics') > ($prefix+'.stdout.json')
        $arguments=@((Join-Path $script:DosWorkspaceRoot 'tests\verify-doom-extender.py'),'--report',($prefix+'.json'),'--base',$prepared,'--overlay',$cow,'--game',$Game,'--mode',$mode)
        if($mode -ne 'none'){$arguments+=@('--baseline',$baselines[$mode])}
        python @arguments > ($prefix+'.verified.json')
        if($initialHash -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $initialHash){throw 'Doom changed its private baseline'}
        $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        $p=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        $pair+=@{mode=$mode;prefix=$prefix;proof=$p;frameHash=$r.frameHash;virtualNanoseconds=$r.status.virtualNanoseconds}
    }
    if($pair[0].frameHash -ne $pair[1].frameHash -or $pair[0].virtualNanoseconds -ne $pair[1].virtualNanoseconds -or $pair[0].proof.sceneSequenceSha256 -ne $pair[1].proof.sceneSequenceSha256){throw 'Doom cold boots differ'}
    $doomCases+=$pair
}
$controlCases=@()
foreach($system in @('msdos','freedos','emm386-noems','emm386-ram')){
    $plan=Join-Path $root ($system+'-control.scenario.json')
    $commands=@()
    if($system -ne 'freedos'){$commands+=@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="a:cpux87`r`n"})}
    else{$commands+=@{waitForScreen='C:\>';text="a:cpux87`r`n"}}
    $commands+=@{waitForDebug='CPU-X87-PROOF=';text="ver`r`necho CPU-X87-GUEST-END`r`n"}
    $commands | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
    $pair=@()
    foreach($number in 1..2){
        $prefix=Join-Path $root ("control-$system-$number");$cow=$prefix+'.cow'
        $controlDisk=$prepared
        if($system -eq 'msdos'){$baseline=$null}
        elseif($system -eq 'freedos'){$baseline=$null;$controlDisk=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'}
        elseif($system -eq 'emm386-noems'){$baseline=$baselines['NOEMS']}
        else{$baseline=$baselines['RAM']}
        $initialHash=$null
        if($baseline){Copy-Item -LiteralPath $baseline -Destination $cow;$initialHash=(Get-DosFileHash -LiteralPath $cow).Hash}
        dotnet $headless --disk $controlDisk --overlay $cow --floppy-a $floppy --scenario $plan --steps 150000000 --stop-text "`r`nCPU-X87-GUEST-END`r`n`r`nC:\>" --report ($prefix+'.json') --memory-dump ($prefix+'.private-ram.bin') > ($prefix+'.stdout.json')
        python (Join-Path $script:DosWorkspaceRoot 'tests\verify-x87-control.py') --report ($prefix+'.json') --memory ($prefix+'.private-ram.bin') --program $probe --system $system > ($prefix+'.verified.json')
        if($initialHash -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $initialHash){throw 'Control guest changed its private baseline'}
        $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json;$p=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        $pair+=@{system=$system;prefix=$prefix;proof=$p;frameHash=$r.frameHash;virtualNanoseconds=$r.status.virtualNanoseconds}
    }
    if($pair[0].frameHash -ne $pair[1].frameHash -or $pair[0].virtualNanoseconds -ne $pair[1].virtualNanoseconds -or $pair[0].proof.proofSha256 -ne $pair[1].proof.proofSha256){throw 'Control cold boots differ'}
    $controlCases+=$pair
}
$prefix=$doomCases[0].prefix
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-doom-extender.py') --report ($prefix+'.json') --base $prepared --overlay ($prefix+'.cow') --game $Game --mode none --negative-controls (Join-Path $root 'doom-negative-controls') > (Join-Path $root 'doom-negative-validation.json')
$prefix=$controlCases[0].prefix
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-x87-control.py') --report ($prefix+'.json') --memory ($prefix+'.private-ram.bin') --program $probe --system msdos --negative-controls (Join-Path $root 'control-negative-controls') > (Join-Path $root 'control-negative-validation.json')
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'Protected source, media, baseline or assembly changed'}}
$summary=@{passed=$true;directory=$root;configurationColdBoots=3;doomColdBoots=$doomCases.Count;controlColdBoots=$controlCases.Count;
    doomCases=$doomCases;controlCases=$controlCases;originalsUnchanged=$true;originalFileHashes=$originals;
    options=@('none','HIMEM','NOEMS','RAM');demoTicksPerStart=32;negativeOracleControls=14;nativeWindowsOpened=$false;
    fullX87ArithmeticAccepted=$false;fullGameCompatibility=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 10
