# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk, [Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk, [string]$NoEmsOverlay, [string]$RamOverlay,
    [ValidateRange(1,2)][int]$Repeats=2)
. (Join-Path $PSScriptRoot 'common.ps1')
$env:PYTHONUTF8='1'; $env:PYTHONIOENCODING='utf-8'
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Resolve-DosPath 'artifacts/freedos-m1/FD14LITE-minimal.img');overlay=$null})
$optional=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($optional.Count -notin 0,3){throw 'Supply all three optional EMM386 source paths together.'}
if($optional.Count -eq 3){$systems+=@(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
$sources=@($systems|ForEach-Object{$_.disk;if($_.overlay){$_.overlay}})|Select-Object -Unique
foreach($source in $sources){if($source -match '(?i)doom2-play'){throw 'Personal play journals are excluded.'}}
$originals=@($sources | Get-DosFileHash)
$root=Join-Path $script:DosWorkspaceRoot ('artifacts/serial-m3/acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$runner=Join-Path $buildRoot 'Release/net10.0/DOSEmulator.Headless.dll'
$assemblies=@(Get-ChildItem -LiteralPath (Split-Path $runner -Parent) -Filter '*.dll'|Get-DosFileHash)
$assemblies|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$probe=Join-Path $root 'SERIAL.COM';$floppy=Join-Path $root 'own-probe.img'
& (Resolve-DosPath 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe') -Werror -f bin (Resolve-DosPath 'tests/guests/serial-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Own serial probe assembly failed.'}
python (Resolve-DosPath 'tests/prepare-font-media.py') $probe $floppy SERIAL.COM > (Join-Path $root 'media.json')
if($LASTEXITCODE -ne 0){throw 'Own FAT12 probe creation failed.'}
$rows=@()
foreach($system in $systems){
    $plan=@();$prompt='C:\>'
    if($system.name -ne 'freedos'){$plan+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
    $plan+=@{waitForScreen=$prompt;text="a:serial`r`n"}
    $errors=@(0,4,8,16)
    foreach($index in 0..3){$plan+=@{waitForDebug=('SERIAL-EXT-'+$index.ToString('X2'));settleNanoseconds=0;
        serial=@{data=@(214);errors=$errors[$index];cts=$true;dsr=$true;dcd=$true;ri=($index%2 -eq 0)};text="`r"}}
    $plan+=@{waitForDebug='SERIAL-OK';text="ver`r`necho SERIAL-DOS-RETURN`r`n"}
    $scenario=Join-Path $root ($system.name+'.scenario.json');ConvertTo-Json -InputObject $plan -Depth 8|Set-Content -LiteralPath $scenario -Encoding utf8
    foreach($repeat in 1..$Repeats){
        $prefix=Join-Path $root ($system.name+'-'+$repeat);$cow=$prefix+'.cow'
        if($system.overlay){Copy-Item -LiteralPath $system.overlay -Destination $cow}
        $initialCow=if($system.overlay){(Get-DosFileHash -LiteralPath $cow).Hash}else{$null}
        $stop=if($system.name -eq 'freedos'){"`r`nSERIAL-DOS-RETURN`r`nC:\>"}else{"`r`nSERIAL-DOS-RETURN`r`n`r`nC:\>"}
        $arguments=@($runner,'--profile',(Resolve-DosPath 'profiles/virtual486-serial-dev-v1.json'),'--disk',$system.disk,'--overlay',$cow,
            '--floppy-a',$floppy,'--scenario',$scenario,'--serial-echo','--serial-io-trace',($prefix+'.io.jsonl'),
            '--steps','150000000','--stop-text',$stop,'--report',($prefix+'.json'),'--memory-dump',($prefix+'.ram.bin'))
        dotnet @arguments > ($prefix+'.stdout.json')
        if($LASTEXITCODE -ne 0){throw 'Actual DOS serial execution failed.'}
        $oracle=@('--report',($prefix+'.json'),'--memory',($prefix+'.ram.bin'),'--io',($prefix+'.io.jsonl'),'--program',$probe)
        if($system.name -like 'emm386-*'){$oracle+='--manager'}
        if($repeat -eq 1){$oracle+='--negative-controls'}
        python (Resolve-DosPath 'tests/verify-serial.py') @oracle > ($prefix+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent serial port/frame/IRQ/RAM oracle failed.'}
        if($initialCow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $initialCow){throw 'System journal changed.'}
        $rows+=[pscustomobject]@{system=$system.name;repeat=$repeat;report=$prefix+'.json';verification=(Get-Content -LiteralPath ($prefix+'.verified.json') -Raw|ConvertFrom-Json)}
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Input/tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count;cases=$rows;originalsUnchanged=$true;nativeInputSent=$false;
    scope='Nominal PC16550D COM1, framed virtual echo, BIOS00..03, FIFO/IRQ/error/loopback; no host COM/network/electrical-chip precision'}
$summary|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary|ConvertTo-Json -Depth 12
