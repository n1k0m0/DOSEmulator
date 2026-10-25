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
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\sb2-highspeed-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
& $assembler -Werror -f bin -o (Join-Path $programs 'SB2HST.COM') (Join-Path $workspaceRoot 'tests\guests\sb2-highspeed-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'High-speed probe assembly failed.' }
$profile = Join-Path $testRoot 'profile.json'
[IO.File]::WriteAllText($profile,'{"soundCard":"sb2-dsp-development"}')
$image = Join-Path $testRoot 'private-highspeed.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private high-speed probe disk preparation failed.' }
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$commands = Join-Path $testRoot 'commands.txt'
[IO.File]::WriteAllText($commands,"sb2hst`r`necho M2-HS-END`r`n")
$results = @()
foreach ($run in 1..2) {
    $prefix = Join-Path $testRoot ("cold-boot-$run")
    dotnet $runner --profile $profile --disk $image --steps 90000000 --type-file $commands --stop-text "`r`nM2-HS-END`r`nC:\>" --report ($prefix+'.json') --audio-out ($prefix+'.wav') --audio-io-trace ($prefix+'.io.jsonl') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'High-speed DOS guest failed.' }
    $result = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
    foreach ($required in @('SB2-HS-SINGLE-IRQ-OK','SB2-HS-AUTO-THREE-IRQS-RESET-OK','SB2-HS-PRESERVED-TC-IRQ-OK','SB2-HS-RESTORED-BLOCK-OK','SB2-HS-ABORT-NO-IRQ-OK','SB2-HS-DOS-RETURN-OK',"`r`nM2-HS-END`r`nC:\>")) {
        if (-not $result.status.debugOutput.Contains($required)) { throw "Missing guest result: $required" }
    }
    $sb = $result.soundBlaster
    if ($result.status.fault -or $result.status.debugOutput.Contains('SB2-HS-FAILED') -or $result.inputBytesSent -ne $result.inputBytesTotal -or
        $sb.transferredSamples -ne ($sb.highSpeedSamples + 2048) -or $sb.highSpeedSamples -le 10240 -or $sb.completedBlocks -ne 6 -or $sb.interruptRequests -ne 6 -or
        $sb.highSpeedTransfers -ne 3 -or $sb.highSpeedResets -ne 2 -or $sb.ignoredHighSpeedWrites -ne 10 -or $sb.resetCount -ne 4 -or
        $sb.active -or $sb.paused -or $sb.highSpeed -or $sb.interruptAsserted -or $sb.speakerEnabled -or $sb.rejectedWrites -ne 0 -or
        $sb.unsupportedCommands -ne 0 -or $result.audio.control -ne 0 -or $result.ataSectorsWritten -ne 0) { throw 'High-speed guest did not return cleanly with expected DMA/IRQ state.' }
    $oracle = python (Join-Path $workspaceRoot 'tests\verify-sb2-highspeed.py') ($prefix+'.wav') ($prefix+'.json') ($prefix+'.io.jsonl')
    if ($LASTEXITCODE -ne 0) { throw 'Independent high-speed verification failed.' }
    $oracle | Set-Content -LiteralPath ($prefix+'.verified.json') -Encoding utf8
    $results += $result
}
if ($results[0].audio.dataSha256 -ne $results[1].audio.dataSha256 -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds -or $results[0].frameHash -ne $results[1].frameHash) { throw 'High-speed cold boots are not deterministic.' }
$hostAudio = $null; $ratio = $null; $audioWindows = @()
if ($Gui) {
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    foreach ($mode in @('guest','audio')) {
        $guiRoot = Join-Path $testRoot ('gui-'+$mode)
        New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
        $guiCommands = Join-Path $guiRoot 'commands.txt'
        $guestCommand = if ($mode -eq 'guest') { "sb2hst`r`n" } else { "sb2hst /h`r`n" }
        [IO.File]::WriteAllText($guiCommands,$guestCommand)
        $marker = if ($mode -eq 'guest') { 'SB2-HS-DOS-RETURN-OK' } else { 'SB2-HS-HOLD-READY' }
        $arguments = @('--disk',('"{0}"' -f $image),'--profile',('"{0}"' -f $profile),
            '--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),'--acceptance-test',('"{0}"' -f $guiRoot),
            '--type-file',('"{0}"' -f $guiCommands),'--capture-after',$marker)
        if ($mode -eq 'audio') { $arguments += @('--audio-acceptance','--audio-out',('"{0}"' -f (Join-Path $guiRoot 'host-playback.wav'))) }
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "High-speed GUI $mode acceptance timed out." }
        if ($process.ExitCode -ne 0) { throw "High-speed GUI $mode failed." }
        $resultName = if ($mode -eq 'guest') { 'smoke-result.json' } else { 'audio-result.json' }
        $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot $resultName) | ConvertFrom-Json
        $expectedCaptures = if ($mode -eq 'guest') { 6 } else { 4 }
        if ($guiResult.status.fault -or $guiResult.audio.fault -or $guiResult.captures.Count -ne $expectedCaptures -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal) { throw "High-speed GUI $mode evidence incomplete." }
        foreach ($capture in $guiResult.captures) {
            $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
            if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'OpenGL high-speed capture incomplete.' }
        }
        if ($mode -eq 'audio') { $hostAudio = $guiResult }
    }
    if (-not $hostAudio.audio.available) { throw 'Native high-speed audio unavailable.' }
    $measurements = @{}
    foreach ($measurement in $hostAudio.measurements) { $measurements[$measurement.phase] = $measurement }
    if ($measurements['muted-end'].nonSilent -ne $measurements['muted-start'].nonSilent) { throw 'Mute did not silence high-speed PCM.' }
    function Rms([string]$Phase) {
        $first = $measurements[$Phase+'-start']; $last = $measurements[$Phase+'-end']
        if ($last.frames -le $first.frames) { throw 'No native audio callbacks during measurement.' }
        return [Math]::Sqrt(($last.energy - $first.energy) / ($last.frames - $first.frames))
    }
    $normal = Rms 'normal'; $reduced = Rms 'reduced'; $ratio = $reduced / $normal
    foreach ($phase in @('normal','muted','reduced')) {
        $first = $measurements[$phase+'-start']; $last = $measurements[$phase+'-end']
        $audioWindows += [pscustomobject]@{phase=$phase;frames=($last.frames - $first.frames);
            droppedFrames=($last.output.droppedFrames - $first.output.droppedFrames);
            underflowFrames=($last.output.underflowFrames - $first.output.underflowFrames)}
        if ($null -eq $first.output -or $null -eq $last.output -or $audioWindows[-1].droppedFrames -ne 0 -or $audioWindows[-1].underflowFrames -ne 0) {
            throw "Host audio discontinuity during the $phase measurement window."
        }
    }
    if ($normal -lt 0.05 -or $ratio -lt 0.2 -or $ratio -gt 0.45 -or $hostAudio.settings.audioMuted -or
        $hostAudio.settings.masterVolume -ne 50 -or $hostAudio.settings.digitalVolume -ne 50 -or $hostAudio.settings.speakerVolume -ne 0) { throw "Independent high-speed PCM volume incorrect: $ratio" }
}
if ((Get-DosFileHash -LiteralPath $baseImage -Algorithm SHA256).Hash -ne $baseHash -or (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash) { throw 'Read-only test media changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;coldBoots=2;pcmSha256=$results[0].audio.dataSha256;frames=$results[0].audio.recordedFrames;
    transferredSamples=$results[0].soundBlaster.transferredSamples;highSpeedSamples=$results[0].soundBlaster.highSpeedSamples;completedBlocks=$results[0].soundBlaster.completedBlocks;
    ignoredHighSpeedWrites=$results[0].soundBlaster.ignoredHighSpeedWrites;highSpeedResets=$results[0].soundBlaster.highSpeedResets;frameHash=$results[0].frameHash;
    gui=[bool]$Gui;hostAudioAvailable=$hostAudio.audio.available;liveMuteTested=[bool]$Gui;independentDigitalVolumeTested=[bool]$Gui;hostRmsRatio=$ratio;
    audioMeasurementWindows=$audioWindows;hostDroppedFrames=$hostAudio.audio.droppedFrames;hostUnderflowFrames=$hostAudio.audio.underflowFrames;
    mediaUnchanged=$true;highSpeedInputTested=$false;adpcmTested=$false;siliconComparison=$false}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
