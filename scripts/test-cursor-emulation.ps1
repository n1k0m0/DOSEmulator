# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[switch]$Gui,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
$Disk=(Resolve-Path -LiteralPath $Disk).Path
$Overlay=(Resolve-Path -LiteralPath $Overlay).Path
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$freeDos=Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$testRoot=Join-Path $workspaceRoot ('artifacts\cursor-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash -Algorithm SHA256
$probe=Join-Path $testRoot 'CURSOR.COM'
& (Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $workspaceRoot 'tests\guests\cursor-emulation-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Own cursor guest assembly failed'}
$floppy=Join-Path $testRoot 'only-own-cursor-probe.img'
python (Join-Path $workspaceRoot 'tests\prepare-font-media.py') $probe $floppy CURSOR.COM > (Join-Path $testRoot 'probe-media.json')
if($LASTEXITCODE -ne 0){throw 'Own cursor-only FAT12 preparation failed'}
$floppyHash=(Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash
# Writes an explicit private guest-input plan with bounded waits and required output markers.
function Scenario($Name,$Steps){
    $path=Join-Path $testRoot ($Name+'.scenario.json')
    ConvertTo-Json -InputObject $Steps -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
# Runs the actual headless guest with explicit private media, captures evidence and rejects execution failures.
function Guest($Name,$Base,$Cow,$Plan,[bool]$Capture){
    $prefix=Join-Path $testRoot $Name
    $arguments=@($runner,'--disk',$Base,'--floppy-a',$floppy,'--scenario',$Plan,'--steps','200000000','--report',($prefix+'.json'))
    if($Cow){$arguments+=@('--overlay',$Cow)}
    if($Capture){$arguments+=@('--stop-text','CURSOR-READY','--frame',($prefix+'.ppm'),'--memory-dump',($prefix+'.private-memory.bin'))}
    dotnet @arguments > ($prefix+'.stdout.json')
    if($LASTEXITCODE -ne 0){throw "Cursor guest $Name failed"}
    $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($result.status.fault -or -not $result.scenario.complete -or $result.scenario.inputBytesSent -ne $result.scenario.inputBytesTotal -or $result.status.debugOutput.Contains('CURSOR-FAILED') -or $result.ataSectorsWritten -ne 0 -or $result.floppySectorsWritten -ne 0){throw "Cursor guest $Name incomplete or wrote a disk"}
    return $result
}
$cases=@()
foreach($os in @('msdos','freedos')){foreach($points in @(8,14,16)){foreach($enabled in @(0,1)){
    $policy=if($enabled){'on'}else{'off'}
    $cases+=@{name="$os-$points-$policy";os=$os;points=$points;enabled=$enabled;policy=$policy;gui=(($os -eq 'msdos' -and $points -eq 16) -or ($os -eq 'freedos' -and $points -eq 14 -and $enabled -eq 1))}
}}}
$results=@();$visible=0
foreach($case in $cases){
    $steps=@();$base=$freeDos;$wait='C:\>'
    if($case.os -eq 'msdos'){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
    $timerOption=if($case.os -eq 'freedos'){' /t5'}else{''}
    $steps+=@{waitForScreen=$wait;text="a:cursor /$($case.points) /$($case.policy)$timerOption`r`n"}
    $plan=Scenario $case.name $steps
    $runs=@()
    foreach($number in 1..2){
        $name=$case.name+'-'+$number;$prefix=Join-Path $testRoot $name;$cow=$null
        if($case.os -eq 'msdos'){$cow=Join-Path $testRoot ($name+'.cow');Copy-Item -LiteralPath $Overlay -Destination $cow;$before=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash}
        $result=Guest $name $base $cow $plan $true
        $oracle=@((Join-Path $workspaceRoot 'tests\verify-cursor-frame.py'),'--frame',($prefix+'.ppm'),'--report',($prefix+'.json'),'--memory',($prefix+'.private-memory.bin'),'--guest',$case.os,'--points',$case.points,'--enabled',$case.enabled)
        if($cow){$oracle+=@('--base',$base,'--overlay',$cow)}
        python @oracle > ($prefix+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent complete cursor/font pixel check failed'}
        $verified=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        if($number -eq 1 -and $verified.cursorVisible){$visible++}
        if($cow -and (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $before){throw 'Cursor guest changed its COW'}
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'Cursor cold boots differ'}
    $results+=[pscustomobject]@{name=$case.name;points=$case.points;enabled=[bool]$case.enabled;width=$runs[0].width;height=$runs[0].height;coldBoots=2;frameHash=$runs[0].frameHash;nanoseconds=$runs[0].status.virtualNanoseconds;everyPixelVerified=$true;cursorVisible=$verified.cursorVisible;crtcStart=$verified.crtcStart;crtcEnd=$verified.crtcEnd}
    if($Gui -and $case.gui){
        $guiRoot=Join-Path $testRoot ('gui-'+$case.name);$guiCow=$null
        $arguments=@('--disk',('"'+$base+'"'),'--floppy-a',('"'+$floppy+'"'),'--scenario',('"'+$plan+'"'),
            '--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),'--acceptance-test',('"'+$guiRoot+'"'),'--capture-after','CURSOR-READY')
        if($case.os -eq 'msdos'){
            $guiCow=Join-Path $testRoot ('gui-'+$case.name+'.cow');Copy-Item -LiteralPath $Overlay -Destination $guiCow
            $before=(Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash
            $arguments+=@('--overlay',('"'+$guiCow+'"'))
        }
        $process=Start-Process -FilePath (Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Native cursor acceptance timed out'}
        if($process.ExitCode -ne 0){throw 'Native cursor acceptance failed'}
        $native=Get-Content -LiteralPath (Join-Path $guiRoot 'smoke-result.json') -Raw | ConvertFrom-Json
        if($native.status.fault -or -not $native.scenario.complete -or $native.captures.Count -ne 6 -or -not $native.status.debugOutput.Contains('CURSOR-POLICY-AND-SHAPE-OK')){throw 'Native cursor guest incomplete'}
        foreach($capture in $native.captures){
            $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
            if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100 -or $gl.guestWidth -ne $runs[0].width -or $gl.guestHeight -ne $runs[0].height){throw 'Native cursor GL capture incomplete'}
        }
        if($guiCow -and (Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash -ne $before){throw 'Native cursor guest changed its COW'}
    }
}
if($visible -eq 0){throw 'No headless capture contains the real visible cursor'}
$returns=@()
foreach($os in @('msdos','freedos')){
    $cow=$null;$base=$freeDos;$steps=@();$wait='C:\>'
    if($os -eq 'msdos'){
        $base=$Disk;$cow=Join-Path $testRoot 'msdos-return.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
        $before=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash
        $steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'
    }
    foreach($transition in @(@{points=8;policy='on'},@{points=14;policy='off'},@{points=16;policy='on'})){
        $tag="CURSOR-RETURN-$($transition.points)-OK"
        $steps+=@{waitForScreen=$wait;text="a:cursor /$($transition.points) /$($transition.policy) /a`r`nver`r`necho $tag`r`n"};$wait=$tag
    }
    $plan=Scenario ($os+'-return') $steps
    $returned=Guest ($os+'-return') $base $cow $plan $false
    if(-not $returned.text.Contains('CURSOR-RETURN-16-OK') -or -not $returned.text.TrimEnd().EndsWith('C:\>') -or ([regex]::Matches($returned.status.debugOutput,'CURSOR-RESTORE-OK')).Count -ne 3 -or ([regex]::Matches($returned.status.debugOutput,'CURSOR-DOS-RETURN-OK')).Count -ne 3){throw 'Cursor switches did not restore original policy/shape and return cleanly to DOS'}
    if($cow -and (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $before){throw 'Cursor return changed its COW'}
    $returns+=[pscustomobject]@{os=$os;transitions=3;originalPolicyAndShapesRestored=$true;cleanPrompt=$true;noWrites=$true}
}
foreach($original in $originals){if((Get-DosFileHash -LiteralPath $original.Path -Algorithm SHA256).Hash -ne $original.Hash){throw 'Protected DOS source changed'}}
if((Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash -ne $floppyHash){throw 'Own cursor-only floppy changed'}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;cases=$results;headlessColdBoots=26;visibleCursorCases=$visible;nativeProcesses=$(if($Gui){3}else{0});nativeCaptures=$(if($Gui){18}else{0});returns=$returns;originalsUnchanged=$true;onlyOwnProgramOnFloppy=$true}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
