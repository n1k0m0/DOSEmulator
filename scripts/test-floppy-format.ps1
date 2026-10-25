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
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\floppy-format-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
& $assembler -Werror -f bin -o (Join-Path $programs 'BIOSFMT.COM') (Join-Path $workspaceRoot 'tests\guests\floppy-format-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'Floppy format probe assembly failed.' }
$image = Join-Path $testRoot 'private-probe.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private format probe disk preparation failed.' }
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$a = Join-Path $testRoot 'private-scratch-a.img'; $b = Join-Path $testRoot 'private-scratch-b.img'
$scratch = [byte[]]::new(1474560); [Array]::Fill[byte]($scratch,0x39)
foreach ($path in @($a,$b)) { $file = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write); try { $file.Write($scratch) } finally { $file.Dispose() } }
$scratchHash = (Get-DosFileHash -LiteralPath $a -Algorithm SHA256).Hash
$results = @(); $stages = @()
foreach ($run in 1..2) {
    $aCow = Join-Path $testRoot ("a-$run.cow"); $bCow = Join-Path $testRoot ("b-$run.cow")
    foreach ($mode in @('write','verify')) {
        $prefix = Join-Path $testRoot ("$mode-$run")
        $commands = $prefix+'.txt'; $argument = if ($mode -eq 'write') { '' } else { ' /v' }
        [IO.File]::WriteAllText($commands,"biosfmt$argument`r`necho M2-FORMAT-END`r`n")
        $oldOverlays = if ($mode -eq 'verify') { @((Get-DosFileHash -LiteralPath $aCow).Hash,(Get-DosFileHash -LiteralPath $bCow).Hash) } else { @() }
        dotnet $runner --disk $image --floppy-a $a --floppy-a-overlay $aCow --floppy-b $b --floppy-b-overlay $bCow --boot ide --steps 90000000 --type-file $commands --stop-text "`r`nM2-FORMAT-END`r`nC:\>" --report ($prefix+'.json') --frame ($prefix+'.ppm') --floppy-io-trace ($prefix+'.io.jsonl') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Floppy format guest failed: $prefix" }
        $result = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
        $required = @('FORMAT-READBACK-PERSISTENCE-OK','BIOS-FORMAT-DOS-RETURN-OK',"`r`nM2-FORMAT-END`r`nC:\>")
        if ($mode -eq 'write') { $required += @('FORMAT-A-FULL-TRACK-OK','FORMAT-B-INTERLEAVED-IDS-OK','FORMAT-INVALID-DESCRIPTOR-OK') }
        foreach ($text in $required) { if (-not $result.status.debugOutput.Contains($text)) { throw "Missing actual DOS result '$text': $prefix" } }
        $expectedWrites = if ($mode -eq 'write') { 27 } else { 0 }
        if ($result.status.fault -or $result.status.debugOutput.Contains('BIOS-FORMAT-FAILED') -or $result.inputBytesSent -ne $result.inputBytesTotal -or $result.ataSectorsWritten -ne 0 -or $result.floppySectorsWritten -ne $expectedWrites -or $result.floppySectorsRead -ne 37) { throw "Unexpected guest format state: $prefix" }
        if ($mode -eq 'verify' -and ($oldOverlays[0] -ne (Get-DosFileHash -LiteralPath $aCow).Hash -or $oldOverlays[1] -ne (Get-DosFileHash -LiteralPath $bCow).Hash)) { throw 'Persistence read-back changed overlays.' }
        $oracle = python (Join-Path $workspaceRoot 'tests\verify-floppy-format.py') ($prefix+'.io.jsonl') $mode $a $aCow $b $bCow
        if ($LASTEXITCODE -ne 0) { throw 'Independent format verification failed.' }
        $oracle | Set-Content -LiteralPath ($prefix+'.verified.json') -Encoding utf8
        $stages += [pscustomobject]@{name="$mode-$run";read=$result.floppySectorsRead;written=$result.floppySectorsWritten;frameHash=$result.frameHash;nanoseconds=$result.status.virtualNanoseconds}
        if ($mode -eq 'write') { $results += $result }
    }
}
if ($results[0].frameHash -ne $results[1].frameHash -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds) { throw 'Format cold boots differ.' }
$prefix = Join-Path $testRoot 'readonly'
$commands = $prefix+'.txt'; [IO.File]::WriteAllText($commands,"biosfmt /r`r`necho M2-FORMAT-END`r`n")
dotnet $runner --disk $image --floppy-a $a --floppy-b $b --boot ide --steps 90000000 --type-file $commands --stop-text "`r`nM2-FORMAT-END`r`nC:\>" --report ($prefix+'.json') --floppy-io-trace ($prefix+'.io.jsonl') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Read-only format guest failed.' }
$ro = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
if ($ro.status.fault -or $ro.inputBytesSent -ne $ro.inputBytesTotal -or $ro.floppySectorsWritten -ne 0 -or $ro.floppySectorsRead -ne 1 -or $ro.ataSectorsWritten -ne 0 -or -not $ro.status.debugOutput.Contains('FORMAT-WRITE-PROTECT-OK') -or -not $ro.status.debugOutput.EndsWith("`r`nM2-FORMAT-END`r`nC:\>")) { throw 'Read-only format verification failed.' }
$oracle = python (Join-Path $workspaceRoot 'tests\verify-floppy-format.py') ($prefix+'.io.jsonl') readonly
if ($LASTEXITCODE -ne 0) { throw 'Independent read-only protocol verification failed.' }
$oracle | Set-Content -LiteralPath ($prefix+'.verified.json') -Encoding utf8
$stages += [pscustomobject]@{name='readonly';read=$ro.floppySectorsRead;written=0;frameHash=$ro.frameHash;nanoseconds=$ro.status.virtualNanoseconds}
if ($Gui) {
    $guiRoot = Join-Path $testRoot 'gui'; New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
    $guiCommands = Join-Path $guiRoot 'commands.txt'; [IO.File]::WriteAllText($guiCommands,"biosfmt /r`r`n")
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    $arguments = @('--disk',('"{0}"' -f $image),'--floppy-a',('"{0}"' -f $a),'--floppy-b',('"{0}"' -f $b),'--boot','ide',
        '--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),'--acceptance-test',('"{0}"' -f $guiRoot),
        '--type-file',('"{0}"' -f $guiCommands),'--capture-after','BIOS-FORMAT-DOS-RETURN-OK')
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Floppy format GUI acceptance timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Floppy format GUI acceptance failed.' }
    $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'smoke-result.json') | ConvertFrom-Json
    if ($guiResult.status.fault -or $guiResult.captures.Count -ne 6 -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal) { throw 'Native format GUI evidence incomplete.' }
    foreach ($capture in $guiResult.captures) { $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json; if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'OpenGL format capture incomplete.' } }
}
foreach ($pair in @(@($baseImage,$baseHash),@($image,$imageHash),@($a,$scratchHash),@($b,$scratchHash))) {
    if ((Get-DosFileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne $pair[1]) { throw 'Read-only format test base changed.' }
}
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;coldBoots=5;actualDosGuest=$true;fdcDma2=$true;allScratchSectorsIndependentlyChecked=$true;
    baseImagesUnchanged=$true;privateOverlays=$true;gui=[bool]$Gui;stages=$stages}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
