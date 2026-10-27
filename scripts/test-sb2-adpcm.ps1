# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk
$Overlay=Resolve-DosPath $Overlay
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\sb2-adpcm-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash
$profile=Join-Path $root 'profile.json'
@{name='Creative ADPCM DOS acceptance';soundCard='sb2-dsp-development'} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
$probe=Join-Path $root 'SBADPCM.COM'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\sb2-adpcm-test.asm') -o $probe
$floppy=Join-Path $root 'only-own-adpcm-probe.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy SBADPCM.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$cases=@()
foreach($os in @('msdos','freedos')){
    $steps=@();$base=$freeDos;$wait='C:\>'
    if($os -eq 'msdos'){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
    $steps+=@{waitForScreen=$wait;text="a:sbadpcm`r`nver`r`necho ADPCM-RUN-END`r`n"}
    $plan=Join-Path $root ($os+'.scenario.json')
    ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
    $runs=@()
    foreach($number in 1..2){
        $prefix=Join-Path $root ($os+'-'+$number);$cow=$null
        $stop=if($os -eq 'msdos'){"`r`nADPCM-RUN-END`r`n`r`nC:\>"}else{"`r`nADPCM-RUN-END`r`nC:\>"}
        $arguments=@((Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'),'--disk',$base,'--profile',$profile,
            '--floppy-a',$floppy,'--scenario',$plan,'--steps','120000000','--stop-text',$stop,'--report',($prefix+'.json'),
            '--memory-dump',($prefix+'.private-memory.bin'),'--audio-out',($prefix+'.wav'),
            '--audio-io-trace',($prefix+'.io.jsonl'),'--audio-irq-trace',($prefix+'.irq.jsonl'))
        if($os -eq 'msdos'){
            $cow=$prefix+'.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
            $hash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',$cow)
        }
        dotnet @arguments > ($prefix+'.stdout.json')
        $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        if(-not $result.status.debugOutput.Contains('ADPCM-DOS-RETURN-OK') -or $result.status.debugOutput.Contains('ADPCM-FAILED')){throw "ADPCM guest $os/$number failed: $($result.status.debugOutput)"}
        python (Join-Path $script:DosWorkspaceRoot 'tests\verify-sb2-adpcm.py') --report ($prefix+'.json') --memory ($prefix+'.private-memory.bin') --audio ($prefix+'.wav') --io ($prefix+'.io.jsonl') --irq ($prefix+'.irq.jsonl') > ($prefix+'.verified.json')
        if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'ADPCM probe changed COW'}
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].audio.dataSha256 -ne $runs[1].audio.dataSha256 -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'ADPCM cold boots differ'}
    $cases+=@{os=$os;coldBoots=2;pcmSha256=$runs[0].audio.dataSha256;frameHash=$runs[0].frameHash;
        dmaBytes=$runs[0].soundBlaster.adpcmDmaBytes;decodedSamples=$runs[0].soundBlaster.adpcmDecodedSamples;
        irqCount=$runs[0].soundBlaster.interruptRequests;pcmFrames=$runs[0].audio.recordedFrames;guestReturned=$true}
}
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'ADPCM protected media changed'}}
$summary=@{passed=$true;directory=$root;headlessColdBoots=4;cases=$cases;originalsUnchanged=$true;onlyOwnProgramOnFloppy=$true;siliconTimingComparison=$false}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
