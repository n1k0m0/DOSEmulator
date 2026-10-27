# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk,[string]$NoEmsOverlay,[string]$RamOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$configured=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Supply all three manager configuration paths together.'}
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img');overlay=$null})
if($configured.Count -eq 3){
    $systems+=@([pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
        [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})
}
foreach($system in $systems){foreach($path in @($system.disk,$system.overlay | Where-Object { $_ })){
    if($path -match '(?i)doom2-play'){throw 'User play media are excluded from acceptance.'}
}}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\sb16-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$originals=@($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique | Get-DosFileHash
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$results=[Collections.Generic.List[object]]::new();$negativeCount=0
foreach($resource in @(@{port=0x220;irq=5;low=1;high=5},@{port=0x240;irq=10;low=3;high=6},@{port=0x280;irq=9;low=0;high=7})){
    $folder=Join-Path $root ('A{0:X3}-I{1}-D{2}-H{3}' -f $resource.port,$resource.irq,$resource.low,$resource.high)
    New-Item -ItemType Directory -Path $folder | Out-Null
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-sb16.py') $folder > (Join-Path $folder 'prepared.json')
    $program=Join-Path $folder 'SB16.COM';$floppy=Join-Path $folder 'private-probe.img';$profile=Join-Path $folder 'profile.json'
    & $nasm -Werror -f bin '-I' ($folder+[IO.Path]::DirectorySeparatorChar) ('-DPORT='+$resource.port) ('-DIRQ='+$resource.irq) ('-DDMA8='+$resource.low) ('-DDMA16='+$resource.high) (Join-Path $script:DosWorkspaceRoot 'tests\guests\sb16-pcm-test.asm') -o $program
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program $floppy SB16.COM > (Join-Path $folder 'media.json')
    $originals+=Get-DosFileHash -LiteralPath $floppy
    [pscustomobject]@{soundCard='sb16-ct1740-dsp405-development';soundBlasterBasePort=$resource.port;soundBlasterIrq=$resource.irq;
        soundBlasterDmaChannel=$resource.low;soundBlasterDma16Channel=$resource.high} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
    foreach($system in $systems){
        $steps=@();$wait='C:\>'
        if($system.name -ne 'freedos'){$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
        $steps+=@{waitForScreen=$wait;text="a:sb16`r`nver`r`necho SB16-END`r`n"}
        $scenario=Join-Path $folder ($system.name+'.scenario.json');ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $scenario -Encoding utf8
        $first=$null
        foreach($repeat in 1..2){
            $prefix=Join-Path $folder ($system.name+'-'+$repeat);$cow=$null;$cowHash=$null
            $args=@($runner,'--profile',$profile,'--disk',$system.disk,'--floppy-a',$floppy,'--scenario',$scenario,'--steps','150000000',
                '--report',($prefix+'.json'),'--memory-dump',($prefix+'.private-ram.bin'),'--audio-out',($prefix+'.wav'),
                '--sb16-trace',($prefix+'.trace.jsonl'),'--audio-io-trace',($prefix+'.io.jsonl'),'--audio-irq-trace',($prefix+'.irq.jsonl'),
                '--sb16-adc-test-signal','ramp','--stop-text',$(if($system.name -eq 'freedos'){"`r`nSB16-END`r`nC:\>"}else{"`r`nSB16-END`r`n`r`nC:\>"}))
            if($system.overlay){$cow=$prefix+'.cow';Copy-Item -LiteralPath $system.overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow).Hash;$args+=@('--overlay',$cow)}
            dotnet @args > ($prefix+'.stdout.json')
            $oracle=@('--vectors',(Join-Path $folder 'vectors.json'),'--program',$program,'--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),
                '--trace',($prefix+'.trace.jsonl'),'--io',($prefix+'.io.jsonl'),'--irq',($prefix+'.irq.jsonl'),'--wave',($prefix+'.wav'))
            if($repeat -eq 1){$oracle+='--negative-controls'}
            python (Join-Path $script:DosWorkspaceRoot 'tests\verify-sb16.py') @oracle > ($prefix+'.verified.json')
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $cowHash){throw 'SB16 guest changed its private DOS HDD'}
            $v=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            if($v.PSObject.Properties.Name -contains 'negativeControlsRejected'){$negativeCount+=$v.negativeControlsRejected}
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $row=[pscustomobject]@{system=$system.name;resource=$resource;repeat=$repeat;report=$prefix+'.json';verification=$v;
                frameHash=$r.frameHash;virtualNanoseconds=$r.status.virtualNanoseconds;proofHash=$v.proofSha256;pcmHash=$v.pcmSha256}
            if($first){if($row.frameHash -ne $first.frameHash -or $row.proofHash -ne $first.proofHash -or $row.pcmHash -ne $first.pcmHash -or $row.virtualNanoseconds -ne $first.virtualNanoseconds){throw 'SB16 PCM cold boots differ'}}else{$first=$row}
            $results.Add($row)
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Protected input/tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$results.Count;cases=$results;negativeControlsRejected=$negativeCount;
    testedAssembliesUnchanged=$true;sourceMediaUnchanged=$true;hostMicrophoneOpened=$false;nativeWindowsOpened=$false;nativeInputSent=$false;
    scope='CT1740 DSP4.05 PCM/core CT1745: DMA8/DMA16, stereo, virtual ADC, shared IRQ and bounded VDS; OPL3/MPU/CSP/analog filtering/FIFO bus timing remain open'}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
