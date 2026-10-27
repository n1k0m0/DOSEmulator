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
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Resolve-DosPath 'artifacts/freedos-m1/FD14LITE-minimal.img');overlay=$null})
$optional=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay|Where-Object{$_})
if($optional.Count -notin 0,3){throw 'Supply all three optional manager source paths together.'}
if($optional.Count -eq 3){$systems+=@(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
$sources=@($systems|ForEach-Object{$_.disk;if($_.overlay){$_.overlay}})|Select-Object -Unique
foreach($source in $sources){if($source -match '(?i)doom2-play'){throw 'Personal play journals are excluded.'}}
$originals=@($sources|Get-DosFileHash)
$root=Join-Path $script:DosWorkspaceRoot ('artifacts/lpt-m3/acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$runner=Join-Path $buildRoot 'Release/net10.0/DOSEmulator.Headless.dll'
$assemblies=@(Get-ChildItem -LiteralPath (Split-Path $runner -Parent) -Filter '*.dll'|Get-DosFileHash)
$assemblies|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$probe=Join-Path $root 'LPT.COM';$floppy=Join-Path $root 'own-probe.img'
& (Resolve-DosPath 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe') -Werror -f bin (Resolve-DosPath 'tests/guests/parallel-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Own LPT probe assembly failed.'}
$copySource=Join-Path $root 'LPTDATA.TXT'
python (Resolve-DosPath 'tests/prepare-parallel-media.py') $probe $floppy $copySource > (Join-Path $root 'media.json')
if($LASTEXITCODE -ne 0){throw 'Own FAT12 media creation failed.'}
$rows=@()
foreach($system in $systems){foreach($mode in @('sink','pins')){
    $plan=@();$prompt='C:\>'
    if($system.name -ne 'freedos'){$plan+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
    $command=if($mode -eq 'pins'){"a:lpt /p`r`n"}else{"a:lpt`r`n"}
    $plan+=@{waitForScreen=$prompt;text=$command}
    if($mode -eq 'pins'){
        foreach($index in 0..32){$value=if($index -lt 32){$index}else{0}
            $plan+=@{waitForDebug=('LPIN-READY-'+$index.ToString('X2'));settleNanoseconds=0;text="`r";
                parallel=@{busyHigh=[bool]($value -band 16);ackHigh=[bool]($value -band 8);paperEndHigh=[bool]($value -band 4);selectedHigh=[bool]($value -band 2);errorHigh=[bool]($value -band 1)}}}
        $plan+=@{waitForDebug='LPIN-TIMEOUT';settleNanoseconds=0;text="`r";
            parallel=@{busyHigh=$true;ackHigh=$true;paperEndHigh=$false;selectedHigh=$true;errorHigh=$true}}
    }
    $last=if($mode -eq 'sink'){"echo DOSE-LPT-COPY>prn`r`ncopy /b a:lptdata.txt prn /b`r`nver`r`necho LPT-DOS-RETURN`r`n"}else{"ver`r`necho LPT-DOS-RETURN`r`n"}
    $plan+=@{waitForDebug='LPT-OK';text=$last}
    $scenario=Join-Path $root ($system.name+'-'+$mode+'.scenario.json');ConvertTo-Json -InputObject $plan -Depth 8|Set-Content -LiteralPath $scenario -Encoding utf8
    foreach($repeat in 1..$Repeats){
        $prefix=Join-Path $root ($system.name+'-'+$mode+'-'+$repeat);$cow=$prefix+'.cow'
        if($system.overlay){Copy-Item -LiteralPath $system.overlay -Destination $cow}
        $initial=if($system.overlay){(Get-DosFileHash -LiteralPath $cow).Hash}else{$null}
        $stop=if($system.name -eq 'freedos'){"`r`nLPT-DOS-RETURN`r`nC:\>"}else{"`r`nLPT-DOS-RETURN`r`n`r`nC:\>"}
        $arguments=@($runner,'--profile',(Resolve-DosPath 'profiles/virtual486-parallel-dev-v1.json'),'--disk',$system.disk,'--overlay',$cow,
            '--floppy-a',$floppy,'--scenario',$scenario,'--steps','150000000','--stop-text',$stop,'--report',($prefix+'.json'),
            '--memory-dump',($prefix+'.ram.bin'),'--parallel-io-trace',($prefix+'.io.jsonl'))
        if($mode -eq 'sink'){$arguments+=@('--parallel-sink','--parallel-output',($prefix+'.print.bin'))}
        dotnet @arguments > ($prefix+'.stdout.json')
        if($LASTEXITCODE -ne 0){throw 'Actual DOS parallel execution failed.'}
        $oracle=@('--report',($prefix+'.json'),'--memory',($prefix+'.ram.bin'),'--io',($prefix+'.io.jsonl'),'--program',$probe)
        if($mode -eq 'sink'){$oracle+=@('--output',($prefix+'.print.bin'),'--copy-source',$copySource)}
        if($system.name -like 'emm386-*'){$oracle+='--manager'}
        if($repeat -eq 1){$oracle+='--negative-controls'}
        python (Resolve-DosPath 'tests/verify-parallel.py') @oracle > ($prefix+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent LPT data/pin/timing/IRQ/RAM/DOS oracle failed.'}
        if($initial -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $initial){throw 'System journal changed.'}
        $rows+=[pscustomobject]@{system=$system.name;mode=$mode;repeat=$repeat;report=$prefix+'.json';verification=(Get-Content -LiteralPath ($prefix+'.verified.json') -Raw|ConvertFrom-Json)}
    }
}}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Input/tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count;cases=$rows;originalsUnchanged=$true;nativeInputSent=$false;
    scope='IBM 6361507 output-only adapter, synthetic raw-byte peer/manual status pins, actual BIOS/PRN/COPY/IRQ; no host printer/EPP/ECP/physical print engine'}
$summary|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary|ConvertTo-Json -Depth 12
