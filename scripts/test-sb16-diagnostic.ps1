# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk, [Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk, [string]$NoEmsOverlay, [string]$RamOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$configured=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Supply all manager configuration paths together.'}
$systems=@(@{name='msdos';disk=$Disk;overlay=$Overlay},
    @{name='freedos';disk=(Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img');overlay=$null})
if($configured.Count -eq 3){$systems+=@(
    @{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    @{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
foreach($system in $systems){foreach($path in @($system.disk,$system.overlay | Where-Object { $_ })){
    if($path -match '(?i)doom2-play'){throw 'Personal game journals are excluded.'}
}}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\sb16-diagnostic-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -Filter 'DOSEmulator.*.dll' | Get-DosFileHash
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$results=[Collections.Generic.List[object]]::new(); $negativeCount=0
foreach($resource in @(@{port=0x220;irq=5},@{port=0x240;irq=10})){
    $folder=Join-Path $root ('A{0:X3}-I{1}' -f $resource.port,$resource.irq)
    New-Item -ItemType Directory -Path $folder | Out-Null
    $program=Join-Path $folder 'SB16DIAG.COM'; $floppy=Join-Path $folder 'own-probe.img'; $profile=Join-Path $folder 'profile.json'
    & $nasm -Werror -f bin ('-DSBBASE='+$resource.port) ('-DSBIRQ='+$resource.irq) (Join-Path $script:DosWorkspaceRoot 'tests\guests\sb16-diagnostic-test.asm') -o $program
    if($LASTEXITCODE -ne 0){throw 'Diagnostic guest assembly failed.'}
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program $floppy SB16DIAG.COM > (Join-Path $folder 'media.json')
    @{soundCard='sb16-ct1740-dsp405-development';soundBlasterBasePort=$resource.port;soundBlasterIrq=$resource.irq;ramMiB=8;bootDevice='ide'} |
        ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
    foreach($system in $systems){
        $scenario=Join-Path $folder ($system.name+'.scenario.json')
        $steps=@(); $wait='C:\>'
        if($system.name -ne 'freedos'){$steps+=@{waitForScreen=$wait;text="kezb us`r`n"};$wait='C:\>keyb us'}
        $steps+=@{waitForScreen=$wait;text="a:sb16diag`r`nver`r`necho SB16-DIAG-END`r`n"}
        ConvertTo-Json -InputObject $steps | Set-Content -LiteralPath $scenario -Encoding utf8
        $first=$null
        foreach($repeat in 1..2){
            $prefix=Join-Path $folder ($system.name+'-'+$repeat); $cow=$prefix+'.cow'
            if($system.overlay){Copy-Item -LiteralPath $system.overlay -Destination $cow}
            $before=if($system.overlay){(Get-DosFileHash -LiteralPath $cow).Hash}else{$null}
            $args=@($runner,'--profile',$profile,'--disk',$system.disk,'--floppy-a',$floppy,'--scenario',$scenario,'--steps','150000000',
                '--report',($prefix+'.json'),'--memory-dump',($prefix+'.ram'),'--audio-io-trace',($prefix+'.io.jsonl'),
                '--audio-irq-trace',($prefix+'.irq.jsonl'),'--stop-text',"`nSB16-DIAG-END`r`n")
            if($system.overlay){$args+=@('--overlay',$cow)}
            dotnet @args > ($prefix+'.log')
            $oracle=@('--program',$program,'--report',($prefix+'.json'),'--memory',($prefix+'.ram'),'--io',($prefix+'.io.jsonl'),
                '--irq',($prefix+'.irq.jsonl'),'--port',$resource.port,'--line',$resource.irq)
            if($repeat -eq 1){$oracle+='--negative-controls'}
            python (Join-Path $script:DosWorkspaceRoot 'tests\verify-sb16-diagnostic.py') @oracle > ($prefix+'.verified.json')
            $v=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            if($v.PSObject.Properties.Name -contains 'negativeControlsRejected'){$negativeCount+=$v.negativeControlsRejected}
            if($before -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $before){throw 'Diagnostic guest modified its DOS journal.'}
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $row=[pscustomobject]@{system=$system.name;resource=$resource;repeat=$repeat;report=$prefix+'.json';verification=$v;
                virtualNanoseconds=$r.status.virtualNanoseconds;frameHash=$r.frameHash;virtual8086Instructions=$r.cpu.virtual8086Instructions}
            if($first){if($row.virtualNanoseconds -ne $first.virtualNanoseconds -or $row.frameHash -ne $first.frameHash){throw 'Diagnostic cold boots differ.'}}else{$first=$row}
            $results.Add($row)
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Protected input/build changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;coldBoots=$results.Count;handlers=5*$results.Count;
    negativeControlsRejected=$negativeCount;cases=$results;sourceInputs=$originals;testedAssemblies=$assemblies;
    originalMediaUnchanged=$true;nativeWindowsOpened=$false;nativeInputSent=$false;scope='Software IRQ diagnostics; no DMA/VDS/analog/timing silicon claim'}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
