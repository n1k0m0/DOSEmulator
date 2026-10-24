param([switch]$SkipBuild, [switch]$Gui, [string]$OutputRoot = 'artifacts\m1-build')
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $OutputRoot))
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$assembler = Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$baseImage = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$baseHash = (Get-FileHash -LiteralPath $baseImage -Algorithm SHA256).Hash
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\opl2-csm-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
& $assembler -Werror -f bin -o (Join-Path $programs 'OPL2CSM.COM') (Join-Path $workspaceRoot 'tests\guests\opl2-csm-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'CSM probe assembly failed.' }
$image = Join-Path $testRoot 'private-csm.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private CSM probe disk preparation failed.' }
$imageHash = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
$summaries = @()
foreach ($card in @('adlib-opl2-development','sb2-opl2-development')) {
    $profile = Join-Path $testRoot ($card+'.json')
    [IO.File]::WriteAllText($profile,('{"soundCard":"'+$card+'"}'))
    $commands = Join-Path $testRoot ($card+'-commands.txt')
    $guestCommand = if ($card -eq 'adlib-opl2-development') { 'opl2csm' } else { 'opl2csm /s' }
    [IO.File]::WriteAllText($commands,($guestCommand+"`r`necho M2-CSM-END`r`n"))
    $results = @()
    foreach ($run in 1..2) {
        $prefix = Join-Path $testRoot ($card+'-'+$run)
        dotnet $runner --profile $profile --disk $image --steps 70000000 --type-file $commands --stop-text "`r`nM2-CSM-END`r`nC:\>" --report ($prefix+'.json') --audio-out ($prefix+'.wav') --audio-io-trace ($prefix+'.io.jsonl') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CSM DOS guest failed.' }
        $result = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
        foreach ($required in @('OPL2-CSM-TIMER2-SILENT','OPL2-CSM-SLOW','OPL2-CSM-RESET-FLAG','OPL2-CSM-FAST','OPL2-CSM-STOPPED','OPL2-CSM-MELODIC-RETURN','OPL2-CSM-KEYING-DOS-OK',"`r`nM2-CSM-END`r`nC:\>")) {
            if (-not $result.status.debugOutput.Contains($required)) { throw "Missing guest result: $required" }
        }
        if ($result.status.fault -or $result.status.debugOutput.Contains('OPL2-CSM-FAILED') -or $result.inputBytesSent -ne $result.inputBytesTotal -or
            $result.opl.activeOperators -ne 0 -or $result.opl.timerInterruptAsserted -or $result.opl.unsupportedProgramming -or $result.opl.rejectedWrites -ne 0 -or
            $result.opl.csmKeyPulses -lt 100 -or $result.opl.csmPulseActive -or $result.opl.csmEnabled -or
            $result.opl.keyOnCount -ne ($result.opl.csmKeyPulses * 18 + 2) -or $result.audio.control -ne 0 -or $result.ataSectorsWritten -ne 0) { throw 'CSM guest did not return cleanly.' }
        if ($card -eq 'sb2-opl2-development' -and ($result.soundBlaster.transferredSamples -ne 0 -or $result.soundBlaster.interruptRequests -ne 0)) { throw 'CSM incorrectly depends on DSP/DMA.' }
        $oracle = python (Join-Path $workspaceRoot 'tests\verify-opl2-csm.py') ($prefix+'.wav') ($prefix+'.json') ($prefix+'.io.jsonl')
        if ($LASTEXITCODE -ne 0) { throw 'Independent CSM timer/audio verification failed.' }
        $oracle | Set-Content -LiteralPath ($prefix+'.verified.json') -Encoding utf8
        $results += $result
    }
    if ($results[0].audio.dataSha256 -ne $results[1].audio.dataSha256 -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds -or $results[0].frameHash -ne $results[1].frameHash) { throw 'CSM cold boots are not deterministic.' }
    $summaries += [pscustomobject]@{card=$card;coldBoots=2;pcmSha256=$results[0].audio.dataSha256;frames=$results[0].audio.recordedFrames;
        nativeSamples=$results[0].opl.generatedSamples;csmPulses=$results[0].opl.csmKeyPulses;operatorKeyOns=$results[0].opl.keyOnCount;frameHash=$results[0].frameHash}
}
$guiResult = $null
if ($Gui) {
    $guiRoot = Join-Path $testRoot 'gui'
    New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
    $guiCommands = Join-Path $guiRoot 'commands.txt'
    [IO.File]::WriteAllText($guiCommands,"opl2csm /s`r`n")
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    $profile = Join-Path $testRoot 'sb2-opl2-development.json'
    $arguments = @('--disk',('"{0}"' -f $image),'--profile',('"{0}"' -f $profile),
        '--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),'--acceptance-test',('"{0}"' -f $guiRoot),
        '--type-file',('"{0}"' -f $guiCommands),'--capture-after','OPL2-CSM-KEYING-DOS-OK')
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'CSM GUI acceptance timed out.' }
    if ($process.ExitCode -ne 0) { throw 'CSM GUI failed.' }
    $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'smoke-result.json') | ConvertFrom-Json
    if ($guiResult.status.fault -or $guiResult.audio.fault -or -not $guiResult.audio.available -or $guiResult.captures.Count -ne 6 -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal) { throw 'CSM GUI evidence incomplete.' }
    foreach ($capture in $guiResult.captures) {
        $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
        if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'OpenGL CSM capture incomplete.' }
    }
}
if ((Get-FileHash -LiteralPath $baseImage -Algorithm SHA256).Hash -ne $baseHash -or (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash) { throw 'Read-only test media changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;profiles=$summaries;gui=[bool]$Gui;hostAudioAvailable=$guiResult.audio.available;
    classicRecoveryReads=$true;csmKeyingTested=$true;fullCsmAccuracy=$false;rhythmImplemented=$false;siliconComparison=$false;mediaUnchanged=$true}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
