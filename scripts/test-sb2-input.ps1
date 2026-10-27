# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [ValidateSet(544,576)][int]$BasePort=0x220,[ValidateSet(3,5,7,9)][int]$Irq=5,[switch]$ResourceProbe,[switch]$Gui,
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
$Disk=(Resolve-Path -LiteralPath $Disk).Path
$Overlay=(Resolve-Path -LiteralPath $Overlay).Path
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$freeDos=Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$testRoot=Join-Path $workspaceRoot ('artifacts\sb2-input-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
if($Gui -and -not $ResourceProbe){throw 'This GUI acceptance exercises the SB2+OPL2 resource menu; select ResourceProbe'}
$profile=Join-Path $testRoot 'profile.json'
$card=if($ResourceProbe){'sb2-opl2-development'}else{'sb2-dsp-development'}
@{name="SB2 ADC resource probe A$('{0:X3}' -f $BasePort) I$Irq D1";soundCard=$card;soundBlasterBasePort=$BasePort;soundBlasterIrq=$Irq;soundBlasterDmaChannel=1} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash -Algorithm SHA256
$probe=Join-Path $testRoot 'SBINPUT.COM'
$assemblerArgs=@('-Werror','-f','bin',('-DSB_BASE='+$BasePort),('-DSB_IRQ='+$Irq))
if($ResourceProbe){$assemblerArgs+='-DSB_RESOURCE_TEST=1'}
& (Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') @assemblerArgs (Join-Path $workspaceRoot 'tests\guests\sb2-input-test.asm') -o $probe
if($LASTEXITCODE -ne 0){throw 'Own ADC guest assembly failed'}
$floppy=Join-Path $testRoot 'only-own-adc-probe.img'
python (Join-Path $workspaceRoot 'tests\prepare-font-media.py') $probe $floppy SBINPUT.COM > (Join-Path $testRoot 'probe-media.json')
if($LASTEXITCODE -ne 0){throw 'Own ADC-only FAT12 preparation failed'}
$floppyHash=(Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash
$results=@()
foreach($os in @('msdos','freedos')){foreach($signal in @('ramp','midpoint')){
    $name="$os-$signal";$steps=@();$base=$freeDos;$wait='C:\>'
    if($os -eq 'msdos'){
        $base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'
    }
    $option=if($signal -eq 'midpoint'){' /s'}else{''}
    $steps+=@{waitForScreen=$wait;text="a:sbinput$option`r`nver`r`necho ADC-RUN-END`r`n"}
    $plan=Join-Path $testRoot ($name+'.scenario.json')
    ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
    $runs=@();$verifiedRuns=@()
    foreach($number in 1..2){
        $prefix=Join-Path $testRoot ($name+'-'+$number);$cow=$null
        $stop=if($os -eq 'msdos'){"`r`nADC-RUN-END`r`n`r`nC:\>"}else{"`r`nADC-RUN-END`r`nC:\>"}
        $arguments=@($runner,'--disk',$base,'--profile',$profile,'--floppy-a',$floppy,'--scenario',$plan,'--steps','120000000',
            '--stop-text',$stop,'--report',($prefix+'.json'),'--memory-dump',($prefix+'.private-memory.bin'),
            '--audio-out',($prefix+'.wav'),'--adc-trace',($prefix+'.adc.jsonl'),'--audio-io-trace',($prefix+'.io.jsonl'),'--audio-irq-trace',($prefix+'.irq.jsonl'))
        # The default board wiring is the unconnected midpoint. Only the ramp
        # case installs a diagnostic source; neither case opens a microphone.
        if($signal -eq 'ramp'){$arguments+=@('--adc-test-signal','ramp')}
        if($os -eq 'msdos'){
            $cow=$prefix+'.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
            $before=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash
            $arguments+=@('--overlay',$cow)
        }
        dotnet @arguments > ($prefix+'.stdout.json')
        if($LASTEXITCODE -ne 0){throw "ADC guest $name/$number failed"}
        $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        foreach($marker in @('ADC-SINGLE-128-OK','ADC-AUTO-256-OK','ADC-HIGH-SINGLE-128-OK','ADC-HIGH-AUTO-256-OK','ADC-PLAYBACK-128-OK','ADC-DOS-RETURN-OK')){
            if(-not $result.status.debugOutput.Contains($marker)){throw "Missing ADC guest result $marker"}
        }
        if($ResourceProbe -and (-not $result.status.debugOutput.Contains('SB2-RESOURCE-FM-ALIASES-OK') -or $result.opl.rejectedWrites -ne 0 -or
            $result.opl.unsupportedProgramming -or $result.opl.registerWrites -ne 5 -or $result.opl.timer1Overflows -lt 1)){throw 'Relocated FM alias guest failed'}
        python (Join-Path $workspaceRoot 'tests\verify-sb2-input.py') --report ($prefix+'.json') --memory ($prefix+'.private-memory.bin') --audio ($prefix+'.wav') --io ($prefix+'.io.jsonl') --adc ($prefix+'.adc.jsonl') --signal $signal --base $BasePort --irq-line $Irq --irq-trace ($prefix+'.irq.jsonl') > ($prefix+'.verified.json')
        if($LASTEXITCODE -ne 0){throw 'Independent complete ADC/DMA/PCM verification failed'}
        $verifiedRuns+=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        if($cow -and (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $before){throw 'ADC guest changed its private COW'}
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].audio.dataSha256 -ne $runs[1].audio.dataSha256 -or
        $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'ADC cold boots differ'}
    $results+=[pscustomobject]@{name=$name;coldBoots=2;dmaInputBytesPerBoot=768;directAdcBytesPerBoot=1;dmaOutputBytesPerBoot=128;
        verifiedAdcEventsPerBoot=$verifiedRuns[0].checkedAdcEvents;verifiedPcmFramesPerBoot=$verifiedRuns[0].independentlyReconstructedFrames;
        largestPcm16Difference=($verifiedRuns | Measure-Object -Property largestPcm16Difference -Maximum).Maximum;
        pcmSha256=$runs[0].audio.dataSha256;frameHash=$runs[0].frameHash;nanoseconds=$runs[0].status.virtualNanoseconds;
        normalInputHz=8000;highSpeedInputHz=(1000000000/68000);guestReturned=$true;noMediaWrites=$true}
}}
$nativeResults=@()
if($Gui){foreach($os in @('msdos','freedos')){
    $base=$freeDos;$guiCow=$null;$before=$null
    $guiRoot=Join-Path $testRoot ('gui-'+$os)
    $arguments=@('--disk',('"'+$base+'"'),'--profile',('"'+$profile+'"'),'--floppy-a',('"'+$floppy+'"'),
        '--scenario',('"'+(Join-Path $testRoot ($os+'-midpoint.scenario.json'))+'"'),'--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),
        '--acceptance-test',('"'+$guiRoot+'"'),'--capture-after','ADC-RUN-END','--sb2-resource-acceptance')
    if($os -eq 'msdos'){
        $base=$Disk;$arguments[1]='"'+$base+'"'
        $guiCow=Join-Path $testRoot 'gui-msdos.cow';Copy-Item -LiteralPath $Overlay -Destination $guiCow
        $before=(Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash
        $arguments+=@('--overlay',('"'+$guiCow+'"'))
    }
    $process=Start-Process -FilePath (Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Native SB2 resource acceptance timed out'}
    if($process.ExitCode -ne 0){throw 'Native SB2 resource acceptance failed'}
    $native=Get-Content -LiteralPath (Join-Path $guiRoot 'resource-result.json') -Raw | ConvertFrom-Json
    if(-not $native.passed -or $native.guestStatus.fault -or $native.finalStatus.fault -or $native.captures.Count -ne 5 -or
        -not $native.scenario.complete -or $native.scenario.inputBytesSent -ne $native.scenario.inputBytesTotal){throw 'Native SB2 resource evidence incomplete'}
    foreach($capture in $native.captures){
        $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
        if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100){throw 'Native SB2 resource capture incomplete'}
    }
    if($guiCow -and (Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash -ne $before){throw 'Native resource menu changed its COW'}
    $nativeResults+=[pscustomobject]@{os=$os;captures=5;baseChangedViaMenu=$native.baseChangedViaMenu;irqChangedViaMenu=$native.irqChangedViaMenu;mediaRetained=$true;noWrites=$true}
}}
foreach($original in $originals){if((Get-DosFileHash -LiteralPath $original.Path -Algorithm SHA256).Hash -ne $original.Hash){throw 'Protected DOS source changed'}}
if((Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash -ne $floppyHash){throw 'Own ADC-only floppy changed'}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;headlessColdBoots=8;cases=$results;
    basePort=$BasePort;irq=$Irq;dmaChannel=1;fmAliasTested=[bool]$ResourceProbe;nativeResults=$nativeResults;
    directAdcTested=$true;normalInputTested=$true;highSpeedInputTested=$true;capturePlaybackTested=$true;
    originalsUnchanged=$true;onlyOwnProgramOnFloppy=$true;hostMicrophoneOpened=$false;siliconComparison=$false}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
