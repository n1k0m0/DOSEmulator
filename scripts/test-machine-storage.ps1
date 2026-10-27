# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[switch]$Gui,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk,[string]$NoEmsOverlay,[string]$RamOverlay,
    [ValidateSet('all','msdos','freedos','emm386-noems','emm386-ram')][string]$System='all',
    [ValidateRange(1,2)][int]$Repeats=2)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Resolve-DosPath 'artifacts/freedos-m1/FD14LITE-minimal.img');overlay=$null})
$configured=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Supply all three manager configuration paths together.'}
if($configured.Count -eq 3){$systems+=@(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
if($System -ne 'all'){$systems=@($systems | Where-Object name -eq $System)}
if($systems.Count -eq 0){throw 'Requested guest system is not configured.'}
$inputs=@($systems | ForEach-Object { $_.disk;if($_.overlay){$_.overlay} }) | Select-Object -Unique
foreach($inputPath in $inputs){if($inputPath -match '(?i)doom2-play'){throw 'User play images are excluded.'}}
$originals=@($inputs | Get-DosFileHash)
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\storage-review\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner=Join-Path $buildRoot 'Release/net10.0/DOSEmulator.Headless.dll'
$assemblies=Get-ChildItem -LiteralPath (Split-Path $runner -Parent) -Filter '*.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
& (Resolve-DosPath 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe') -Werror -f bin (Resolve-DosPath 'tests/guests/harddisks-test.asm') -o (Join-Path $root 'HD4TEST.COM')
if($LASTEXITCODE -ne 0){throw 'Four-disk guest assembly failed.'}
$floppy=Join-Path $root 'own-probe.img'
python (Join-Path $script:DosWorkspaceRoot 'tests/prepare-harddisk-media.py') $root $floppy > (Join-Path $root 'floppy.json')
if($LASTEXITCODE -ne 0){throw 'Own probe floppy creation failed.'}
foreach($size in 2,8,1536){
    dotnet $runner --create-disk (Join-Path $root ($size.ToString()+'.img')) --size $size --unit MiB > (Join-Path $root ($size.ToString()+'.created.json'))
    if($LASTEXITCODE -ne 0){throw 'Blank disk image creation failed.'}
}
$rows=[Collections.Generic.List[object]]::new();$negative=0
foreach($s in $systems){foreach($repeat in 1..$Repeats){
    $prefix=Join-Path $root ($s.name+'-'+$repeat);$cow=$prefix+'-system.cow'
    if($s.overlay){Copy-Item -LiteralPath $s.overlay -Destination $cow}
    $arguments=@($runner,'--disk',$s.disk,'--overlay',$cow,'--floppy-a',$floppy,'--save-machine',($prefix+'.dospc.json'))
    foreach($pair in @(@(1,2),@(2,8),@(3,1536))){$arguments+=@(('--disk'+$pair[0]),(Join-Path $root ($pair[1].ToString()+'.img')),('--overlay'+$pair[0]),($prefix+'-data'+$pair[0]+'.cow'))}
    dotnet @arguments > ($prefix+'.saved.json')
    if($LASTEXITCODE -ne 0){throw 'CLI machine configuration save failed.'}
    foreach($phase in 'write','persist'){
        $before=if($phase -eq 'persist'){@(Get-ChildItem -LiteralPath $root -Filter ($s.name+'-'+$repeat+'-*.cow') | Get-DosFileHash)}else{@()}
        $prompt='C:\>';$plan=@()
        if($s.name -ne 'freedos'){$plan+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
        $command=if($phase -eq 'persist'){"a:hd4test /r`r`n"}else{"a:hd4test`r`n"}
        $plan+=@{waitForScreen=$prompt;text=($command+"echo HD4-DOS-RETURN`r`n")}
        $output=$prefix+'-'+$phase
        ConvertTo-Json -InputObject $plan -Depth 6 | Set-Content -LiteralPath ($output+'.scenario.json') -Encoding utf8
        $stop=if($s.name -eq 'freedos'){"`r`nHD4-DOS-RETURN`r`nC:\>"}else{"`r`nHD4-DOS-RETURN`r`n`r`nC:\>"}
        dotnet $runner --machine ($prefix+'.dospc.json') --scenario ($output+'.scenario.json') --steps 300000000 --stop-text $stop --report ($output+'.json') --memory-dump ($output+'.ram.bin') --ide-io-trace ($output+'.io.jsonl') > ($output+'.stdout.json')
        if($LASTEXITCODE -ne 0){throw 'Four-disk DOS process failed.'}
        $oracle=@('--report',($output+'.json'),'--memory',($output+'.ram.bin'),'--program',(Join-Path $root 'HD4TEST.COM'),'--io',($output+'.io.jsonl'))
        if($phase -eq 'persist'){$oracle+='--persist'}
        if($s.name -like 'emm386-*'){$oracle+='--manager'}
        if($repeat -eq 1){$oracle+='--negative-controls'}
        python (Join-Path $script:DosWorkspaceRoot 'tests/verify-harddisks.py') @oracle > ($output+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent four-disk guest oracle failed.'}
        $v=Get-Content -LiteralPath ($output+'.verified.json') -Raw | ConvertFrom-Json
        $negative+=$v.negativeControlsRejected
        $rows.Add([pscustomobject]@{system=$s.name;repeat=$repeat;phase=$phase;report=$output+'.json';verification=$v})
        foreach($entry in $before){if((Get-DosFileHash -LiteralPath $entry.Path).Hash -ne $entry.Hash){throw 'Persistence read changed a private overlay.'}}
    }
}}
$guiResult=$null
if($Gui){
    $guiRoot=Join-Path $root 'gui';New-Item -ItemType Directory -Path $guiRoot | Out-Null
    $exe=Join-Path $buildRoot 'Release/net10.0-windows/DOSEmulator.exe'
    $machineFile=Join-Path $root ($systems[0].name+'-1.dospc.json')
    $arguments=@('--machine',('"{0}"' -f $machineFile),'--storage-acceptance',('"{0}"' -f $guiRoot),'--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')))
    $process=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        if(-not $process.WaitForExit(45000)){$process.Kill();throw 'Internal storage GUI acceptance timed out.'}
        if($process.ExitCode -ne 0){throw 'Storage GUI process failed.'}
    } finally {$process.Dispose()}
    $guiResult=Get-Content -LiteralPath (Join-Path $guiRoot 'storage-result.json') -Raw | ConvertFrom-Json
    if(-not $guiResult.passed -or $guiResult.status.fault -or $guiResult.captures.Count -ne 6 -or -not $guiResult.dialogs.passed){throw 'Storage GUI evidence incomplete.'}
    foreach($capture in $guiResult.captures){
        $graphics=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
        if($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100){throw 'OpenGL storage capture incomplete.'}
    }
    $shutdown=Get-Content -LiteralPath (Join-Path $guiRoot 'shutdown-result.json') -Raw | ConvertFrom-Json
    if(-not $shutdown.completed -or -not $shutdown.hostAudioTeardownAccepted -or $shutdown.audio.shutdownFault){throw 'Storage GUI did not shut down cleanly.'}
}
foreach($entry in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $entry.Path).Hash -ne $entry.Hash){throw 'An input or tested assembly changed.'}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count;cases=$rows;negativeControlsRejected=$negative;
    actualIdeSlots=4;largestDiskBytes=1610612736;gui=$guiResult;privateWriteOverlays=$true;originalsUnchanged=$true;nativeInputSent=$false;runningStateSnapshotImplemented=$false}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 12
