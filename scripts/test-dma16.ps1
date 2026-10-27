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
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Supply ConfiguredDisk, NoEmsOverlay and RamOverlay together.'}
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img');overlay=$null})
if($configured.Count -eq 3){
    $systems+=@([pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
        [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})
}
foreach($system in $systems){
    foreach($path in @($system.disk,$system.overlay | Where-Object { $_ })){
        if($path -match '(?i)doom2-play'){throw 'User play media are excluded from acceptance.'}
    }
}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\dma16-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$originals=@($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique | Get-DosFileHash
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
foreach($variant in @('raw','managed')){
    $folder=Join-Path $root $variant; New-Item -ItemType Directory -Path $folder | Out-Null
    $prepareArgs=@($folder);if($variant -eq 'managed'){$prepareArgs+='--managed'}
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-dma16.py') @prepareArgs > (Join-Path $folder 'prepared.json')
    $program=Join-Path $folder 'DMA16.COM'; $floppy=Join-Path $folder 'private-probe.img'
    & $nasm -Werror -f bin '-I' ($folder+[IO.Path]::DirectorySeparatorChar) (Join-Path $script:DosWorkspaceRoot 'tests\guests\dma16-test.asm') -o $program
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program $floppy DMA16.COM > (Join-Path $folder 'media.json')
    $originals+=Get-DosFileHash -LiteralPath $floppy
}
$results=[Collections.Generic.List[object]]::new();$negativeCount=0
foreach($system in $systems){
    $variant=if($system.name.StartsWith('emm386')){'managed'}else{'raw'}
    $folder=Join-Path $root $variant;$program=Join-Path $folder 'DMA16.COM';$floppy=Join-Path $folder 'private-probe.img'
    $first=$null
    foreach($repeat in 1..2){
        $prefix=Join-Path $root ($system.name+'-'+$repeat);$cow=$null;$cowHash=$null
        if($system.overlay){$cow=$prefix+'.cow';Copy-Item -LiteralPath $system.overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow).Hash}
        [pscustomobject]@{disk=$system.disk;overlay=$cow;floppy=$floppy;system=$system.name;prefix=$prefix} |
            ConvertTo-Json | Set-Content -LiteralPath ($prefix+'.options.json') -Encoding utf8
        dotnet (Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Tests.dll') --dma16-guest ($prefix+'.options.json') > ($prefix+'.stdout.json')
        $arguments=@('--vectors',(Join-Path $folder 'vectors.json'),'--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),'--program',$program)
        if($repeat -eq 1){$arguments+='--negative-controls'}
        python (Join-Path $script:DosWorkspaceRoot 'tests\verify-dma16.py') @arguments > ($prefix+'.verified.json')
        if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $cowHash){throw 'DMA guest changed its private DOS HDD'}
        $v=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        if($v.PSObject.Properties.Name -contains 'negativeControlsRejected'){$negativeCount+=$v.negativeControlsRejected}
        $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        $row=[pscustomobject]@{system=$system.name;repeat=$repeat;report=$prefix+'.json';verification=$v;
            frameHash=$r.frameHash;virtualNanoseconds=$r.status.virtualNanoseconds;proofHash=$v.proofSha256}
        if($first){
            if($row.frameHash -ne $first.frameHash -or $row.proofHash -ne $first.proofHash -or $row.virtualNanoseconds -ne $first.virtualNanoseconds){throw 'DMA guest cold boots differ'}
        }else{$first=$row}
        $results.Add($row)
    }
}
foreach($item in @($originals)+@($assemblies)){
    if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Protected media or tested DLL changed: '+$item.Path)}
}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$results.Count;cases=$results;negativeControlsRejected=$negativeCount;
    testedAssembliesUnchanged=$true;sourceMediaUnchanged=$true;nativeWindowsOpened=$false;nativeInputSent=$false;
    scope='AT DMA5-7 grants via a test-only paced source/sink; bounded 128-KiB VDS lock and translation control under EMM386; no SB16, general VDS16, arbitration or electrical timing acceptance'}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
