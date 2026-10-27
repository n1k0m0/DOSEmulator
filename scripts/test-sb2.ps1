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
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\sb2-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
& $assembler -Werror -f bin -o (Join-Path $programs 'SB2TEST.COM') (Join-Path $workspaceRoot 'tests\guests\sb2-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'SB2 guest assembly failed.' }
$profile = Join-Path $testRoot 'profile.json'
[IO.File]::WriteAllText($profile,'{"soundCard":"sb2-dsp-development"}')
$image = Join-Path $testRoot 'private-sb2.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private SB2 probe disk preparation failed.' }
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$commands = Join-Path $testRoot 'commands.txt'
[IO.File]::WriteAllText($commands,"sb2test`r`necho M2-SB2-END`r`n")
$results = @()
foreach ($run in 1..2) {
    $report = Join-Path $testRoot ("cold-boot-$run.json")
    $audio = Join-Path $testRoot ("cold-boot-$run.wav")
    $ioTrace = Join-Path $testRoot ("cold-boot-$run.io.jsonl")
    dotnet $runner --profile $profile --disk $image --steps 90000000 --type-file $commands --stop-text "`r`nM2-SB2-END`r`nC:\>" --report $report --audio-out $audio --audio-io-trace $ioTrace | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SB2 DOS guest failed.' }
    $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    foreach ($required in @('SB2-SINGLE-IRQ-OK','SB2-AUTO-FOUR-IRQS-OK','SB2-RATE-10989-343-HZ','SB2-MIX-250-440-HZ','SB2-DOS-RETURN-OK',"`r`nM2-SB2-END`r`nC:\>")) {
        if (-not $result.status.debugOutput.Contains($required)) { throw "Missing guest result: $required" }
    }
    $sb = $result.soundBlaster
    if ($result.status.fault -or $result.status.debugOutput.Contains('SB2-FAILED') -or $result.inputBytesSent -ne $result.inputBytesTotal -or
        $sb.transferredSamples -ne 32768 -or $sb.completedBlocks -ne 10 -or $sb.interruptRequests -ne 10 -or $sb.active -or $sb.paused -or
        $sb.interruptAsserted -or $sb.speakerEnabled -or $sb.rejectedWrites -ne 0 -or $sb.unsupportedCommands -ne 0 -or $result.audio.control -ne 0 -or
        $result.ataSectorsWritten -ne 0) { throw 'SB2 guest did not return cleanly with expected DMA/IRQ counts.' }
    $oracle = python (Join-Path $workspaceRoot 'tests\verify-sb2-audio.py') $audio $report $ioTrace
    if ($LASTEXITCODE -ne 0) { throw 'Independent SB2 audio verification failed.' }
    $oracle | Set-Content -LiteralPath ($audio+'.verified.json') -Encoding utf8
    $results += $result
}
if ($results[0].audio.dataSha256 -ne $results[1].audio.dataSha256 -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds -or $results[0].frameHash -ne $results[1].frameHash) { throw 'Independent SB2 cold boots are not deterministic.' }
$hostAudio = $null
$ratio = $null
if ($Gui) {
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    foreach ($mode in @('guest','audio')) {
        $guiRoot = Join-Path $testRoot ('gui-'+$mode)
        New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
        $guiCommands = Join-Path $guiRoot 'commands.txt'
        $command = if ($mode -eq 'guest') { "sb2test`r`n" } else { "sb2test /h`r`n" }
        [IO.File]::WriteAllText($guiCommands,$command)
        $marker = if ($mode -eq 'guest') { 'SB2-DOS-RETURN-OK' } else { 'SB2-HOLD-READY' }
        $arguments = @('--disk',('"{0}"' -f $image),'--profile',('"{0}"' -f $profile),
            '--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),'--acceptance-test',('"{0}"' -f $guiRoot),
            '--type-file',('"{0}"' -f $guiCommands),'--capture-after',$marker)
        if ($mode -eq 'audio') { $arguments += @('--audio-acceptance','--audio-out',('"{0}"' -f (Join-Path $guiRoot 'host-playback.wav'))) }
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "SB2 GUI $mode acceptance timed out." }
        if ($process.ExitCode -ne 0) { throw "SB2 GUI $mode failed." }
        $resultName = if ($mode -eq 'guest') { 'smoke-result.json' } else { 'audio-result.json' }
        $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot $resultName) | ConvertFrom-Json
        $expectedCaptures = if ($mode -eq 'guest') { 6 } else { 4 }
        if ($guiResult.status.fault -or $guiResult.audio.fault -or $guiResult.captures.Count -ne $expectedCaptures -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal -or $guiResult.status.configuration.soundCard -ne 'sb2-dsp-development') { throw "SB2 GUI $mode evidence incomplete." }
        foreach ($capture in $guiResult.captures) {
            $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
            if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'OpenGL SB2 capture incomplete.' }
        }
        if ($mode -eq 'audio') { $hostAudio = $guiResult }
    }
    if (-not $hostAudio.audio.available) { throw 'Native host audio unavailable.' }
    $measurements = @{}
    foreach ($measurement in $hostAudio.measurements) { $measurements[$measurement.phase] = $measurement }
    if ($measurements['muted-end'].nonSilent -ne $measurements['muted-start'].nonSilent) { throw 'Mute did not silence SB2 host PCM.' }
    # Reads the independently measured audio-phase RMS from this run's captured evidence.
    function Rms([string]$Phase) {
        $first = $measurements[$Phase+'-start']; $last = $measurements[$Phase+'-end']
        if ($last.frames -le $first.frames) { throw 'No native audio callbacks during measurement.' }
        return [Math]::Sqrt(($last.energy - $first.energy) / ($last.frames - $first.frames))
    }
    $normal = Rms 'normal'; $reduced = Rms 'reduced'; $ratio = $reduced / $normal
    if ($normal -lt 0.05 -or $ratio -lt 0.2 -or $ratio -gt 0.45 -or $hostAudio.settings.audioMuted -or
        $hostAudio.settings.masterVolume -ne 50 -or $hostAudio.settings.digitalVolume -ne 50 -or $hostAudio.settings.speakerVolume -ne 0) { throw "Independent digital volume incorrect: RMS ratio $ratio" }
}
if ((Get-DosFileHash -LiteralPath $baseImage -Algorithm SHA256).Hash -ne $baseHash -or (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash) { throw 'Read-only test media changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;coldBoots=2;pcmSha256=$results[0].audio.dataSha256;recordedFrames=$results[0].audio.recordedFrames;
    transferredSamples=$results[0].soundBlaster.transferredSamples;completedBlocks=$results[0].soundBlaster.completedBlocks;gui=[bool]$Gui;
    hostAudioAvailable=$hostAudio.audio.available;liveMuteTested=[bool]$Gui;independentDigitalVolumeTested=[bool]$Gui;hostRmsRatio=$ratio;mediaUnchanged=$true;fmImplemented=$false}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
