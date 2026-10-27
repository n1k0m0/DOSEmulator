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
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\speaker-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
& $assembler -Werror -f bin -o (Join-Path $programs 'SPEAKER.COM') (Join-Path $workspaceRoot 'tests\guests\speaker-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'Speaker guest assembly failed.' }
$image = Join-Path $testRoot 'private-speaker.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private probe disk preparation failed.' }
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$commands = Join-Path $testRoot 'commands.txt'
[IO.File]::WriteAllText($commands,"speaker`r`necho M2-SPEAKER-END`r`n")
$results = @()
foreach ($run in 1..2) {
    $report = Join-Path $testRoot ("cold-boot-$run.json")
    $audio = Join-Path $testRoot ("cold-boot-$run.wav")
    dotnet $runner --disk $image --steps 60000000 --type-file $commands --stop-text "`r`nM2-SPEAKER-END`r`nC:\>" --report $report --audio-out $audio | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Speaker DOS guest failed.' }
    $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    foreach ($required in @('SPEAKER-440-HZ','SPEAKER-880-HZ','SPEAKER-DOS-RETURN-OK',"`r`nM2-SPEAKER-END`r`nC:\>")) {
        if (-not $result.status.debugOutput.Contains($required)) { throw "Missing guest result: $required" }
    }
    if ($result.status.fault -or $result.status.debugOutput.Contains('SPEAKER-FAILED') -or $result.inputBytesSent -ne $result.inputBytesTotal -or ($result.audio.control -band 3) -ne 0) { throw 'Speaker guest did not return cleanly.' }
    $oracle = python (Join-Path $workspaceRoot 'tests\verify-speaker-audio.py') $audio $report
    if ($LASTEXITCODE -ne 0) { throw 'Independent audio verification failed.' }
    $oracle | Set-Content -LiteralPath ($audio+'.verified.json') -Encoding utf8
    $results += $result
}
if ($results[0].audio.dataSha256 -ne $results[1].audio.dataSha256 -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds -or $results[0].frameHash -ne $results[1].frameHash) { throw 'Independent speaker cold boots are not deterministic.' }
$hostAudio = $null
if ($Gui) {
    $guiRoot = Join-Path $testRoot 'gui'
    New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
    $commands = Join-Path $guiRoot 'hold-commands.txt'
    [IO.File]::WriteAllText($commands,"speaker /h`r`n")
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    $arguments = @('--disk',('"{0}"' -f $image),'--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),
        '--acceptance-test',('"{0}"' -f $guiRoot),'--type-file',('"{0}"' -f $commands),'--audio-acceptance','--audio-out',('"{0}"' -f (Join-Path $guiRoot 'host-playback.wav')))
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Speaker GUI acceptance timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Speaker GUI failed.' }
    $hostAudio = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'audio-result.json') | ConvertFrom-Json
    if (-not $hostAudio.audio.available -or $hostAudio.audio.fault -or $hostAudio.status.fault -or $hostAudio.captures.Count -ne 4 -or $hostAudio.inputBytesSent -ne $hostAudio.inputBytesTotal) { throw 'Host audio evidence incomplete.' }
    $measurements = @{}
    foreach ($measurement in $hostAudio.measurements) { $measurements[$measurement.phase] = $measurement }
    if ($measurements['muted-end'].nonSilent -ne $measurements['muted-start'].nonSilent) { throw 'Mute did not silence host PCM.' }
    # Reads the independently measured audio-phase RMS from this run's captured evidence.
    function Rms([string]$Phase) {
        $first = $measurements[$Phase+'-start']; $last = $measurements[$Phase+'-end']
        return [Math]::Sqrt(($last.energy - $first.energy) / ($last.frames - $first.frames))
    }
    $normal = Rms 'normal'; $reduced = Rms 'reduced'; $ratio = $reduced / $normal
    if ($normal -lt 0.05 -or $ratio -lt 0.2 -or $ratio -gt 0.45 -or $hostAudio.settings.audioMuted -or $hostAudio.settings.masterVolume -ne 50 -or $hostAudio.settings.speakerVolume -ne 50) { throw "Live volume result incorrect: RMS ratio $ratio" }
    foreach ($capture in $hostAudio.captures) {
        $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
        if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'OpenGL audio-menu capture incomplete.' }
    }
}
if ((Get-DosFileHash -LiteralPath $baseImage -Algorithm SHA256).Hash -ne $baseHash -or (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash) { throw 'Read-only test media changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;coldBoots=2;pcmSha256=$results[0].audio.dataSha256;recordedFrames=$results[0].audio.recordedFrames;
    gui=[bool]$Gui;hostAudioAvailable=$hostAudio.audio.available;liveMuteTested=[bool]$Gui;liveVolumeTested=[bool]$Gui;mediaUnchanged=$true}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
