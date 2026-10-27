# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$Gui, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk, [Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
$Disk=(Resolve-Path -LiteralPath $Disk).Path
$Overlay=(Resolve-Path -LiteralPath $Overlay).Path
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$testRoot=Join-Path $workspaceRoot ('artifacts\vga-text-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$freeDos=Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash -Algorithm SHA256
$probe=Join-Path $testRoot 'TEXTPAGE.COM'
$floppy=Join-Path $testRoot 'only-own-probe.img'
& (Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $workspaceRoot 'tests\guests\text-pages-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Text-page guest assembly failed'}
python (Join-Path $workspaceRoot 'tests\prepare-font-media.py') $probe $floppy 'TEXTPAGE.COM' > (Join-Path $testRoot 'probe-media.json')
if($LASTEXITCODE -ne 0){throw 'Own private probe media failed'}
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
    if($Capture){$arguments+=@('--stop-text','TEXT-PAGES-READY','--frame',($prefix+'.ppm'),'--memory-dump',($prefix+'.private-memory.bin'),'--video-io-trace',($prefix+'.video.jsonl'))}
    dotnet @arguments > ($prefix+'.stdout.json')
    if($LASTEXITCODE -ne 0){throw "Guest $Name failed"}
    $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($result.status.fault -or -not $result.scenario.complete -or $result.scenario.inputBytesSent -ne $result.scenario.inputBytesTotal -or $result.status.debugOutput.Contains('TEXT-PAGES-FAILED')){throw "Guest $Name incomplete"}
    return $result
}
# Microsoft's MODE LINES command requires real ANSI.SYS. Prepare it by guest
# commands in a new COW; neither canonical startup file is edited on the host.
$ansi=Join-Path $testRoot 'private-ansi.cow'
Copy-Item -LiteralPath $Overlay -Destination $ansi
$configure=Scenario 'configure-ansi' @(@{waitForScreen='C:\>';text="kezb us`r`n"},
    @{waitForScreen='C:\>keyb us';text="copy config.sys config.pre`r`necho DEVICE=C:\DOS\ANSI.SYS >>config.sys`r`necho TEXT-ANSI-CONFIG-OK`r`n"})
$configured=Guest 'configure-ansi' $Disk $ansi $configure $false
if(-not $configured.text.Contains('TEXT-ANSI-CONFIG-OK') -or -not $configured.text.TrimEnd().EndsWith('C:\>') -or $configured.ataSectorsWritten -eq 0){throw 'ANSI preparation did not return to DOS'}
python (Join-Path $workspaceRoot 'tests\verify-font-frame.py') --base $Disk --overlay $ansi --original-overlay $Overlay > (Join-Path $testRoot 'ansi-files.verified.json')
if($LASTEXITCODE -ne 0){throw 'Private ANSI configuration verification failed'}
$ansiHash=(Get-DosFileHash -LiteralPath $ansi -Algorithm SHA256).Hash
$cases=@(
    @{name='msdos25-page0';base=$Disk;source=$Overlay;rows=25;points=16;page=0;pages=8;delay=0;blink=$true;gui=$false},
    @{name='msdos25-page7';base=$Disk;source=$Overlay;rows=25;points=16;page=7;pages=8;delay=0;gui=$true},
    @{name='msdos50-page3';base=$Disk;source=$ansi;rows=50;points=8;page=3;pages=4;delay=100000000;gui=$true},
    @{name='freedos25-page7';base=$freeDos;source=$null;rows=25;points=16;page=7;pages=8;delay=0;gui=$false})
$results=@()
$cursorVisibleCases=0
foreach($case in $cases){
    $steps=@()
    if($case.source){$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}else{$wait='C:\>'}
    $command=if($case.rows -eq 50){"mode con lines=50`r`na:textpage /$($case.page)`r`n"}else{"a:textpage /$($case.page)`r`n"}
    if($case.blink){$command="a:textpage /$($case.page) /b`r`n"}
    $steps+=@{waitForScreen=$wait;text=$command}
    $steps+=@{waitForDebug='TEXT-PAGES-READY';settleNanoseconds=$case.delay}
    $plan=Scenario $case.name $steps
    $runs=@()
    foreach($number in 1..2){
        $name=$case.name+'-'+$number
        $cow=$null
        if($case.source){$cow=Join-Path $testRoot ($name+'.cow');Copy-Item -LiteralPath $case.source -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash}
        $result=Guest $name $case.base $cow $plan $true
        if(-not $result.status.debugOutput.Contains('TEXT-PAGES-CHECKS-OK') -or $result.ataSectorsWritten -ne 0 -or $result.floppySectorsWritten -ne 0 -or $result.floppySectorsRead -eq 0 -or -not $result.text.Contains("PAGE: $($case.page) / PAGES: $($case.pages) / ROWS: $($case.rows)")){throw 'Actual guest page checks failed'}
        if($case.source){
            $prefix=Join-Path $testRoot $name
            $verified=python (Join-Path $workspaceRoot 'tests\verify-text-pages.py') --base $case.base --overlay $cow --frame ($prefix+'.ppm') --report ($prefix+'.json') --points $case.points --rows $case.rows --page $case.page --pages $case.pages --memory ($prefix+'.private-memory.bin')
            if($LASTEXITCODE -ne 0){throw 'Independent CP850 page/cursor pixel check failed'}
            $verified | Set-Content -LiteralPath ($prefix+'.verified.json') -Encoding utf8
            if($number -eq 1 -and ($verified | ConvertFrom-Json).cursorVisible){$cursorVisibleCases++}
            if((Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $cowHash){throw 'Read-only probe changed its private overlay'}
        }
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'Text-page cold boots are not deterministic'}
    $results+=[pscustomobject]@{name=$case.name;rows=$case.rows;points=$case.points;page=$case.page;pages=$case.pages;coldBoots=2;frameHash=$runs[0].frameHash;nanoseconds=$runs[0].status.virtualNanoseconds;everyPixelVerified=[bool]$case.source;guestChecksAllPages=$true}
    if($Gui -and $case.gui){
        $guiRoot=Join-Path $testRoot ('gui-'+$case.name)
        $guiCow=Join-Path $testRoot ('gui-'+$case.name+'.cow');Copy-Item -LiteralPath $case.source -Destination $guiCow
        $guiHash=(Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash
        $exe=Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
        $arguments=@('--disk',('"'+$case.base+'"'),'--overlay',('"'+$guiCow+'"'),'--floppy-a',('"'+$floppy+'"'),
            '--scenario',('"'+$plan+'"'),'--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),'--acceptance-test',('"'+$guiRoot+'"'),'--capture-after','TEXT-PAGES-READY')
        $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(45000)){$process.Kill();throw 'Native text-page acceptance timed out'}
        if($process.ExitCode -ne 0){throw 'Native text-page acceptance failed'}
        $guiResult=Get-Content -LiteralPath (Join-Path $guiRoot 'smoke-result.json') -Raw | ConvertFrom-Json
        if($guiResult.status.fault -or -not $guiResult.scenario.complete -or $guiResult.captures.Count -ne 6 -or -not $guiResult.status.debugOutput.Contains('TEXT-PAGES-CHECKS-OK')){throw 'Native text-page guest incomplete'}
        foreach($capture in $guiResult.captures){
            $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
            if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100 -or $gl.guestWidth -ne 640 -or $gl.guestHeight -ne $case.rows*$case.points){throw 'Native text-page OpenGL capture incomplete'}
        }
        if((Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash -ne $guiHash){throw 'Native read-only probe changed its overlay'}
    }
}
if($cursorVisibleCases -eq 0){throw 'No independently verified guest frame contains a visible colored cursor'}
# Multiple page/row changes in one live DOS process, then ordinary commands.
foreach($os in @('msdos','freedos')){
    $steps=@()
    $cow=$null
    if($os -eq 'msdos'){
        $base=$Disk;$cow=Join-Path $testRoot 'live-return.cow';Copy-Item -LiteralPath $ansi -Destination $cow
        $steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'
        foreach($rows in @(50,43,25)){
            $steps+=@{waitForScreen=$wait;text="mode con lines=$rows`r`na:textpage /a`r`nver`r`necho TEXT-RETURN-$rows-OK`r`n"}
            $wait="TEXT-RETURN-$rows-OK"
        }
    }else{$base=$freeDos;$steps+=@{waitForScreen='C:\>';text="a:textpage /a`r`nver`r`necho TEXT-FREEDOS-RETURN-OK`r`n"}}
    $plan=Scenario ($os+'-live-return') $steps
    $returned=Guest ($os+'-live-return') $base $cow $plan $false
    $expected=if($os -eq 'msdos'){'TEXT-RETURN-25-OK'}else{'TEXT-FREEDOS-RETURN-OK'}
    $count=if($os -eq 'msdos'){3}else{1}
    if(-not $returned.text.Contains($expected) -or -not $returned.text.TrimEnd().EndsWith('C:\>') -or ([regex]::Matches($returned.status.debugOutput,'TEXT-PAGES-DOS-RETURN-OK')).Count -ne $count -or $returned.ataSectorsWritten -ne 0 -or $returned.floppySectorsWritten -ne 0){throw 'Live page/row switches did not return cleanly to DOS'}
}
foreach($original in $originals){if((Get-DosFileHash -LiteralPath $original.Path -Algorithm SHA256).Hash -ne $original.Hash){throw 'Original private installation/base changed'}}
if((Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash -ne $floppyHash -or (Get-DosFileHash -LiteralPath $ansi -Algorithm SHA256).Hash -ne $ansiHash){throw 'Private test source changed'}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;cases=$results;coldBoots=11;guiProcesses=$(if($Gui){2}else{0});verifiedCursorVisibleCases=$cursorVisibleCases;liveRows=@(50,43,25);bothDosReturns=$true;originalsUnchanged=$true;onlyOwnProbeOnFloppy=$true}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
