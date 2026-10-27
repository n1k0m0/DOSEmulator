# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk,[string]$NoEmsOverlay,[string]$RamOverlay,
    [string]$ReferenceExe='artifacts\references\ymfm\opl3-reference.exe')
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay; $ReferenceExe=Resolve-DosPath $ReferenceExe
if(-not (Test-Path -LiteralPath $ReferenceExe)){throw 'Supply a separately built tests/opl3-reference.cpp with independent YMFM; see docs/hardware/OPL3.md'}
$configured=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Supply all three optional manager configuration paths together.'}
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img');overlay=$null})
if($configured.Count -eq 3){$systems+=@(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
foreach($s in $systems){foreach($p in @($s.disk,$s.overlay | Where-Object { $_ })){if($p -match '(?i)doom2-play'){throw 'User play media are excluded.'}}}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\opl3-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$originals=@($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique | Get-DosFileHash
$originals+=Get-DosFileHash -LiteralPath $ReferenceExe
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$rows=[Collections.Generic.List[object]]::new();$negative=0
foreach($plan in @(@{base=0x220;irq=5;low=1;high=5},@{base=0x280;irq=9;low=0;high=7})){
    $folder=Join-Path $root ('A{0:X3}-I{1}' -f $plan.base,$plan.irq)
    New-Item -ItemType Directory -Path $folder | Out-Null
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-opl3.py') $folder > (Join-Path $folder 'prepared.json')
    $program=Join-Path $folder 'OPL3.COM';$floppy=Join-Path $folder 'private-probe.img'
    & $nasm -Werror -f bin '-I' ($folder+[IO.Path]::DirectorySeparatorChar) ('-DPORT='+$plan.base) (Join-Path $script:DosWorkspaceRoot 'tests\guests\opl3-test.asm') -o $program
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program $floppy OPL3.COM > (Join-Path $folder 'media.json')
    $originals+=Get-DosFileHash -LiteralPath $floppy
    $profile=Join-Path $folder 'profile.json'
    @{soundCard='sb16-ct1740-dsp405-development';soundBlasterBasePort=$plan.base;soundBlasterIrq=$plan.irq;
        soundBlasterDmaChannel=$plan.low;soundBlasterDma16Channel=$plan.high} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
    foreach($s in $systems){
        $steps=@();$prompt='C:\>'
        if($s.name -ne 'freedos'){$steps+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
        $steps+=@{waitForScreen=$prompt;text="a:opl3`r`nver`r`necho OPL3-RUN-END`r`n"}
        $scenario=Join-Path $folder ($s.name+'.scenario.json');ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $scenario -Encoding utf8
        $first=$null
        foreach($repeat in 1..2){
            $prefix=Join-Path $folder ($s.name+'-'+$repeat);$cow=$null;$cowHash=$null
            $runnerArgs=@($runner,'--profile',$profile,'--disk',$s.disk,'--floppy-a',$floppy,'--scenario',$scenario,'--steps','300000000',
                '--report',($prefix+'.json'),'--audio-out',($prefix+'.wav'),'--audio-io-trace',($prefix+'.io.jsonl'),
                '--opl3-native-out',($prefix+'.quad.s16'),'--memory-dump',($prefix+'.private-ram.bin'),
                '--stop-text',$(if($s.name -eq 'freedos'){"`r`nOPL3-RUN-END`r`nC:\>"}else{"`r`nOPL3-RUN-END`r`n`r`nC:\>"}))
            if($s.overlay){$cow=$prefix+'.cow';Copy-Item -LiteralPath $s.overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow).Hash;$runnerArgs+=@('--overlay',$cow)}
            dotnet @runnerArgs > ($prefix+'.stdout.json')
            $oracle=@('--report',($prefix+'.json'),'--vectors',(Join-Path $folder 'vectors.json'),'--io',($prefix+'.io.jsonl'),
                '--audio',($prefix+'.wav'),'--native',($prefix+'.quad.s16'),'--memory',($prefix+'.private-ram.bin'),
                '--program',$program,'--reference-exe',$ReferenceExe)
            if($repeat -eq 1){$oracle+='--negative-controls'}
            if($s.name -like 'emm386-*'){$oracle+='--manager'}
            python (Join-Path $script:DosWorkspaceRoot 'tests\verify-opl3.py') @oracle > ($prefix+'.verified.json')
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $cowHash){throw 'OPL3 guest modified its private installed HDD'}
            $v=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $negative+=$v.negativeControlsRejected
            $row=[pscustomobject]@{system=$s.name;repeat=$repeat;base=$plan.base;report=$prefix+'.json';verification=$v;
                virtualNanoseconds=$r.status.virtualNanoseconds;frameHash=$r.frameHash;pcmHash=$v.pcmSha256;quadHash=$v.quadSha256;proofHash=$v.proofSha256}
            if($first){foreach($key in @('virtualNanoseconds','frameHash','pcmHash','quadHash','proofHash')){if($row.$key -ne $first.$key){throw ('Nonrepeatable OPL3 guest '+$key)}}}else{$first=$row}
            $rows.Add($row)
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Protected input or tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count;cases=$rows;negativeControlsRejected=$negative;
    testedAssembliesUnchanged=$true;originalsUnchanged=$true;hostMicrophoneOpened=$false;nativeInputSent=$false;
    scope='YMF262 functional reference: native quad routing/2-op/4-op/waves/rhythm, CT1740 stereo mix and bounded real DOS/EMM386; not silicon or full card'}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
