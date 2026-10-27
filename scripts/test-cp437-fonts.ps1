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
$testRoot=Join-Path $workspaceRoot ('artifacts\cp437-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash -Algorithm SHA256
$probe=Join-Path $testRoot 'CP437.COM'
$assembler=Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
& $assembler -Werror -f bin (Join-Path $workspaceRoot 'tests\guests\cp437-font-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Own CP437 guest assembly failed'}
$floppy=Join-Path $testRoot 'only-own-cp437-probe.img'
python (Join-Path $workspaceRoot 'tests\prepare-font-media.py') $probe $floppy CP437.COM > (Join-Path $testRoot 'probe-media.json')
if($LASTEXITCODE -ne 0){throw 'Own CP437-only FAT12 disk preparation failed'}
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
    $arguments=@($runner,'--disk',$Base,'--floppy-a',$floppy,'--scenario',$Plan,'--steps','100000000','--report',($prefix+'.json'))
    if($Cow){$arguments+=@('--overlay',$Cow)}
    if($Capture){$arguments+=@('--stop-text','CP437-FONT-READY','--frame',($prefix+'.ppm'),'--memory-dump',($prefix+'.private-memory.bin'))}
    dotnet @arguments > ($prefix+'.stdout.json')
    if($LASTEXITCODE -ne 0){throw "CP437 guest $Name failed"}
    $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($result.status.fault -or -not $result.scenario.complete -or $result.scenario.inputBytesSent -ne $result.scenario.inputBytesTotal -or $result.status.debugOutput.Contains('CP437-FONT-FAILED') -or $result.ataSectorsWritten -ne 0 -or $result.floppySectorsWritten -ne 0){throw "CP437 guest $Name incomplete or wrote a disk"}
    return $result
}
$cases=@()
foreach($points in @(8,14,16)){foreach($width in @(8,9)){
    $cases+=@{name="freedos-$points-cell$width";points=$points;width=$width;msdos=$false;gui=($points -eq 8 -and $width -eq 9)}
}}
foreach($points in @(8,14,16)){
    $width=8;if($points -eq 14){$width=9}
    $cases+=@{name="msdos-$points-cell$width";points=$points;width=$width;msdos=$true;gui=($points -ne 8)}
}
$results=@()
foreach($case in $cases){
    $steps=@();$base=$freeDos;$wait='C:\>'
    if($case.msdos){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
    $nine='';if($case.width -eq 9){$nine=' /9'}
    $steps+=@{waitForScreen=$wait;text="a:cp437 /$($case.points)$nine`r`n"}
    $plan=Scenario $case.name $steps
    $runs=@()
    foreach($number in 1..2){
        $name=$case.name+'-'+$number;$prefix=Join-Path $testRoot $name
        $cow=$null
        if($case.msdos){$cow=Join-Path $testRoot ($name+'.cow');Copy-Item -LiteralPath $Overlay -Destination $cow;$before=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash}
        $result=Guest $name $base $cow $plan $true
        python (Join-Path $workspaceRoot 'tests\verify-cp437-frame.py') --frame ($prefix+'.ppm') --report ($prefix+'.json') --memory ($prefix+'.private-memory.bin') --points $case.points --cell-width $case.width > ($prefix+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent whole CP437 artwork/pixel verification failed'}
        $verified=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        if($verified.displayQuerySubstituted -ne [bool]$case.msdos){throw 'Unexpected resident DISPLAY query path'}
        if($cow -and (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $before){throw 'CP437 guest changed its COW'}
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'CP437 cold boots differ'}
    $results+=[pscustomobject]@{name=$case.name;points=$case.points;cellWidth=$case.width;width=$runs[0].width;height=$runs[0].height;coldBoots=2;frameHash=$runs[0].frameHash;nanoseconds=$runs[0].status.virtualNanoseconds;everyPixelVerified=$true;all256Glyphs=$true}
    if($Gui -and $case.gui){
        $guiRoot=Join-Path $testRoot ('gui-'+$case.name);$guiCow=$null
        $exe=Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
        $arguments=@('--disk',('"'+$base+'"'),'--floppy-a',('"'+$floppy+'"'),'--scenario',('"'+$plan+'"'),
            '--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),'--acceptance-test',('"'+$guiRoot+'"'),'--capture-after','CP437-FONT-READY')
        if($case.msdos){
            $guiCow=Join-Path $testRoot ('gui-'+$case.name+'.cow');Copy-Item -LiteralPath $Overlay -Destination $guiCow
            $before=(Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash
            $arguments+=@('--overlay',('"'+$guiCow+'"'))
        }
        $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Native CP437 acceptance timed out'}
        if($process.ExitCode -ne 0){throw 'Native CP437 acceptance failed'}
        $native=Get-Content -LiteralPath (Join-Path $guiRoot 'smoke-result.json') -Raw | ConvertFrom-Json
        if($native.status.fault -or -not $native.scenario.complete -or $native.captures.Count -ne 6 -or -not $native.status.debugOutput.Contains('CP437-PLANE-AND-256-CELLS-OK')){throw 'Native CP437 checks incomplete'}
        foreach($capture in $native.captures){
            $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
            if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100 -or $gl.guestWidth -ne $runs[0].width -or $gl.guestHeight -ne $runs[0].height){throw 'Native CP437 GL capture incomplete'}
        }
        if($guiCow -and (Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash -ne $before){throw 'Native CP437 guest changed its COW'}
    }
}
$returns=@()
foreach($os in @('msdos','freedos')){
    $cow=$null;$base=$freeDos;$steps=@();$wait='C:\>'
    if($os -eq 'msdos'){
        $base=$Disk;$cow=Join-Path $testRoot 'msdos-return.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
        $before=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash
        $steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'
    }
    foreach($points in @(8,14,16)){
        $tag="CP437-RETURN-$points-OK"
        $steps+=@{waitForScreen=$wait;text="a:cp437 /$points /9 /a`r`nver`r`necho $tag`r`n"};$wait=$tag
    }
    $plan=Scenario ($os+'-return') $steps
    $returned=Guest ($os+'-return') $base $cow $plan $false
    if(-not $returned.text.Contains('CP437-RETURN-16-OK') -or -not $returned.text.TrimEnd().EndsWith('C:\>') -or ([regex]::Matches($returned.status.debugOutput,'CP437-ORIGINAL-FONT-RESTORED-OK')).Count -ne 3 -or ([regex]::Matches($returned.status.debugOutput,'CP437-DOS-RETURN-OK')).Count -ne 3){throw 'Font/width switches did not restore original font and return cleanly to DOS'}
    if($cow -and (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $before){throw 'CP437 return changed its COW'}
    $returns+=[pscustomobject]@{os=$os;transitions=3;originalFontBytesRestored=$true;cleanPrompt=$true;noWrites=$true}
}
foreach($original in $originals){if((Get-DosFileHash -LiteralPath $original.Path -Algorithm SHA256).Hash -ne $original.Hash){throw 'Protected DOS source changed'}}
if((Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash -ne $floppyHash){throw 'Own CP437-only floppy changed'}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;cases=$results;headlessColdBoots=20;nativeProcesses=$(if($Gui){3}else{0});nativeCaptures=$(if($Gui){18}else{0});returns=$returns;originalsUnchanged=$true;onlyOwnProgramOnFloppy=$true}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
