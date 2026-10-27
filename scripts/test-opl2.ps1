# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$Gui, [string]$OutputRoot = 'artifacts\m1-build')
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$assembler = Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$baseImage = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$baseHash = (Get-DosFileHash -LiteralPath $baseImage -Algorithm SHA256).Hash
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\opl2-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
& $assembler -Werror -f bin -o (Join-Path $programs 'OPL2TEST.COM') (Join-Path $workspaceRoot 'tests\guests\opl2-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'OPL2 guest assembly failed.' }
$image = Join-Path $testRoot 'private-opl2.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private OPL2 probe disk preparation failed.' }
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$summaries = @()
foreach ($card in @('adlib-opl2-development','sb2-opl2-development')) {
    $profile = Join-Path $testRoot ($card+'.json')
    [IO.File]::WriteAllText($profile,('{"soundCard":"'+$card+'"}'))
    $commands = Join-Path $testRoot ($card+'-commands.txt')
    $guestCommand = if ($card -eq 'adlib-opl2-development') { 'opl2test' } else { 'opl2test /s' }
    [IO.File]::WriteAllText($commands,($guestCommand+"`r`necho M2-OPL2-END`r`n"))
    $results = @()
    foreach ($run in 1..2) {
        $prefix = Join-Path $testRoot ($card+'-'+$run)
        dotnet $runner --profile $profile --disk $image --steps 90000000 --type-file $commands --stop-text "`r`nM2-OPL2-END`r`nC:\>" --report ($prefix+'.json') --audio-out ($prefix+'.wav') --audio-io-trace ($prefix+'.io.jsonl') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'OPL2 DOS guest failed.' }
        $result = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
        foreach ($required in @('OPL2-TIMERS-OK','OPL2-SINE-440','OPL2-RECTIFIED-880','OPL2-ENVELOPE','OPL2-FM-FEEDBACK','OPL2-DOS-RETURN-OK',"`r`nM2-OPL2-END`r`nC:\>")) {
            if (-not $result.status.debugOutput.Contains($required)) { throw "Missing guest result: $required" }
        }
        if ($result.status.fault -or $result.status.debugOutput.Contains('OPL2-FAILED') -or $result.inputBytesSent -ne $result.inputBytesTotal -or
            $result.opl.activeOperators -ne 0 -or $result.opl.timerInterruptAsserted -or $result.opl.unsupportedProgramming -or $result.opl.rejectedWrites -ne 0 -or
            $result.opl.keyOnCount -ne 10 -or $result.opl.timer1Overflows -lt 1 -or $result.opl.timer2Overflows -lt 1 -or $result.audio.control -ne 0 -or
            $result.ataSectorsWritten -ne 0) { throw 'OPL2 guest did not return cleanly.' }
        if ($card -eq 'sb2-opl2-development') {
            $sb = $result.soundBlaster
            if ($sb.transferredSamples -lt 1600 -or $sb.active -or $sb.paused -or $sb.interruptAsserted -or $sb.speakerEnabled -or $sb.rejectedWrites -ne 0 -or
                $sb.unsupportedCommands -ne 0 -or $sb.resetCount -ne 2) { throw 'Triple-source guest did not stop its actual DMA1 PCM cleanly.' }
        }
        $oracle = python (Join-Path $workspaceRoot 'tests\verify-opl2-audio.py') ($prefix+'.wav') ($prefix+'.json') ($prefix+'.io.jsonl')
        if ($LASTEXITCODE -ne 0) { throw 'Independent OPL2 audio verification failed.' }
        $oracle | Set-Content -LiteralPath ($prefix+'.verified.json') -Encoding utf8
        $results += $result
    }
    if ($results[0].audio.dataSha256 -ne $results[1].audio.dataSha256 -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds -or $results[0].frameHash -ne $results[1].frameHash) { throw 'Independent OPL2 cold boots are not deterministic.' }
    $summaries += [pscustomobject]@{card=$card;coldBoots=2;pcmSha256=$results[0].audio.dataSha256;frames=$results[0].audio.recordedFrames;
        nativeSamples=$results[0].opl.generatedSamples;pcmSamples=$results[0].soundBlaster.transferredSamples;frameHash=$results[0].frameHash}
}
$hostAudio = $null; $ratio = $null
if ($Gui) {
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    foreach ($mode in @('guest','audio')) {
        $guiRoot = Join-Path $testRoot ('gui-'+$mode)
        New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
        $guiCommands = Join-Path $guiRoot 'commands.txt'
        $guestCommand = if ($mode -eq 'guest') { "opl2test /s`r`n" } else { "opl2test /h`r`n" }
        [IO.File]::WriteAllText($guiCommands,$guestCommand)
        $marker = if ($mode -eq 'guest') { 'OPL2-DOS-RETURN-OK' } else { 'OPL2-HOLD-READY' }
        $guiCard = if ($mode -eq 'guest') { 'sb2-opl2-development' } else { 'adlib-opl2-development' }
        $profile = Join-Path $testRoot ($guiCard+'.json')
        $arguments = @('--disk',('"{0}"' -f $image),'--profile',('"{0}"' -f $profile),
            '--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),'--acceptance-test',('"{0}"' -f $guiRoot),
            '--type-file',('"{0}"' -f $guiCommands),'--capture-after',$marker)
        if ($mode -eq 'audio') { $arguments += @('--audio-acceptance','--audio-out',('"{0}"' -f (Join-Path $guiRoot 'host-playback.wav'))) }
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "OPL2 GUI $mode acceptance timed out." }
        if ($process.ExitCode -ne 0) { throw "OPL2 GUI $mode failed." }
        $resultName = if ($mode -eq 'guest') { 'smoke-result.json' } else { 'audio-result.json' }
        $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot $resultName) | ConvertFrom-Json
        $expectedCaptures = if ($mode -eq 'guest') { 6 } else { 4 }
        if ($guiResult.status.fault -or $guiResult.audio.fault -or $guiResult.captures.Count -ne $expectedCaptures -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal) { throw "OPL2 GUI $mode evidence incomplete." }
        foreach ($capture in $guiResult.captures) {
            $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
            if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'OpenGL OPL2 capture incomplete.' }
        }
        if ($mode -eq 'audio') { $hostAudio = $guiResult }
    }
    if (-not $hostAudio.audio.available) { throw 'Native host FM audio unavailable.' }
    $measurements = @{}
    foreach ($measurement in $hostAudio.measurements) { $measurements[$measurement.phase] = $measurement }
    if ($measurements['muted-end'].nonSilent -ne $measurements['muted-start'].nonSilent) { throw 'Mute did not silence host FM.' }
    # Reads the independently measured audio-phase RMS from this run's captured evidence.
    function Rms([string]$Phase) {
        $first = $measurements[$Phase+'-start']; $last = $measurements[$Phase+'-end']
        if ($last.frames -le $first.frames) { throw 'No native audio callbacks during measurement.' }
        return [Math]::Sqrt(($last.energy - $first.energy) / ($last.frames - $first.frames))
    }
    $normal = Rms 'normal'; $reduced = Rms 'reduced'; $ratio = $reduced / $normal
    if ($normal -lt 0.05 -or $ratio -lt 0.2 -or $ratio -gt 0.45 -or $hostAudio.settings.audioMuted -or
        $hostAudio.settings.masterVolume -ne 50 -or $hostAudio.settings.fmVolume -ne 50 -or $hostAudio.settings.speakerVolume -ne 0 -or
        $hostAudio.settings.digitalVolume -ne 0) { throw "Independent FM volume incorrect: RMS ratio $ratio" }
}
if ((Get-DosFileHash -LiteralPath $baseImage -Algorithm SHA256).Hash -ne $baseHash -or (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash) { throw 'Read-only test media changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;profiles=$summaries;gui=[bool]$Gui;hostAudioAvailable=$hostAudio.audio.available;
    liveMuteTested=[bool]$Gui;independentFmVolumeTested=[bool]$Gui;hostRmsRatio=$ratio;mediaUnchanged=$true;classicRecoveryReads=$true;
    rhythmImplemented=$false;csmKeyingImplemented=$true;csmImplemented=$false;siliconExact=$false}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
