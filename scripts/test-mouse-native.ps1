#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$original=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-original\FD14LITE.img'
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\mouse-native-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay,$freeDos,$original) | Get-DosFileHash
$profile=Join-Path $root 'profile.json'
@{name='Native Windows input through real CuteMouse';mouse='ps2-two-button'} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
$probe=Join-Path $root 'MOUSECHK.COM'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\mouse-native-test.asm') -o $probe
$floppy=Join-Path $root 'private-cutemouse-and-own-probe.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-mouse-media.py') $original $probe $floppy > (Join-Path $root 'probe-media.private.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$cases=@()
foreach($os in @('msdos','freedos')){
    $steps=@();$base=$freeDos;$wait='C:\>'
    if($os -eq 'msdos'){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
    $steps+=@{waitForScreen=$wait;text="a:\ctmouse /p`r`na:\mousechk`r`n"}
    $steps+=@{waitForScreen='NATIVE-MOUSE-DOS-RETURN-OK';text="ver`r`necho NATIVE-MOUSE-RUN-END`r`n"}
    $plan=Join-Path $root ($os+'.scenario.json')
    ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
    $guiRoot=Join-Path $root ('gui-'+$os);$cow=$null
    $arguments=@('--disk',('"'+$base+'"'),'--profile',('"'+$profile+'"'),'--floppy-a',('"'+$floppy+'"'),
        '--scenario',('"'+$plan+'"'),'--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),
        '--acceptance-test',('"'+$guiRoot+'"'),'--mouse-native-acceptance')
    if($os -eq 'msdos'){
        $cow=Join-Path $root 'gui-msdos.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
        $hash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',('"'+$cow+'"'))
    }
    # The opt-in test uses Windows events only after confirming the foreground
    # belongs to this child process. Its auxiliary focus target is also its own.
    $process=Start-Process -FilePath (Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Native Windows mouse acceptance timed out'}
    if($process.ExitCode -ne 0){throw 'Native mouse process failed'}
    $native=Get-Content -LiteralPath (Join-Path $guiRoot 'mouse-native-result.json') -Raw | ConvertFrom-Json
    if(-not $native.passed -or $native.status.fault -or -not $native.scenario.complete -or $native.scenario.inputBytesSent -ne $native.scenario.inputBytesTotal){throw "Native mouse $os failed: $($native | ConvertTo-Json -Depth 5 -Compress)"}
    if($native.captures.Count -ne 3 -or $native.packets -lt 4){throw 'Incomplete native mouse evidence'}
    foreach($capture in $native.captures){
        $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
        if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100){throw 'Native mouse GL capture failed'}
    }
    if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'Native mouse probe changed COW'}
    $cases+=@{os=$os;captures=3;packets=$native.packets;osCapture=$native.osCaptureTested;osFocusLoss=$native.osFocusLossTested;
        controlAlt=$native.controlAltTested;recapture=$native.recaptureTested;guestInt33=$native.guestInt33Tested;guestReturned=$true}
}
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'Native mouse protected media changed'}}
$summary=@{passed=$true;directory=$root;nativeProcesses=2;cases=$cases;originalsUnchanged=$true;
    actualWindowsEvents=$true;humanPhysicalDeviceMeasured=$false;browserUsed=$false}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
