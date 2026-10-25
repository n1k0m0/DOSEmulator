#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$Gui, [string]$OutputRoot = 'artifacts\m1-build', [string]$MsdosDisk, [string]$MsdosOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$assembler = Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$testRoot = Join-Path $workspaceRoot ('artifacts\mouse-m2\acceptance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
foreach ($probe in @(@('mouse-driver-test.asm','MOUSECHK.COM'),@('mouse-graphics-test.asm','MOUSEGFX.COM'))) {
    & $assembler -Werror -f bin -o (Join-Path $testRoot $probe[1]) (Join-Path $workspaceRoot ('tests\guests\'+$probe[0]))
    if ($LASTEXITCODE -ne 0) { throw 'Mouse probe assembly failed.' }
}
$original = Join-Path $workspaceRoot 'artifacts\freedos-original\FD14LITE.img'
$freeDos = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$floppy = Join-Path $testRoot 'driver.img'
python (Join-Path $workspaceRoot 'tests\prepare-mouse-media.py') $original (Join-Path $testRoot 'MOUSECHK.COM') $floppy | Set-Content -LiteralPath (Join-Path $testRoot 'media.json')
if ($LASTEXITCODE -ne 0) { throw 'Private mouse fixture preparation failed.' }
$profile = Join-Path $workspaceRoot 'profiles\virtual486-ps2-dev-v1.json'
$protected = @($original,$freeDos,$floppy,$profile)
if ($MsdosDisk -or $MsdosOverlay) {
    if (-not ($MsdosDisk -and $MsdosOverlay)) { throw 'Supply both private MS-DOS base and pristine installed overlay.' }
    $MsdosDisk = (Resolve-DosPath -Path $MsdosDisk); $MsdosOverlay = (Resolve-DosPath -Path $MsdosOverlay)
    $protected += @($MsdosDisk,$MsdosOverlay)
}
$hashes = @{}; foreach ($path in $protected) { $hashes[$path] = (Get-DosFileHash -LiteralPath $path -Algorithm SHA256).Hash }
$stages = @()
foreach ($guest in @('freedos-1','freedos-2') + $(if ($MsdosDisk) { @('msdos622') } else { @() })) {
    $prefix = Join-Path $testRoot $guest
    $steps = @()
    $diskArgs = @('--disk',$freeDos)
    if ($guest -eq 'msdos622') {
        $privateCow = $prefix+'.cow'; Copy-Item -LiteralPath $MsdosOverlay -Destination $privateCow
        $diskArgs = @('--disk',$MsdosDisk,'--overlay',$privateCow)
        $steps += @{waitForScreen='C:\>';text="kezb us`r`n"}
        $ready = 'C:\>keyb us'
    } else { $ready = 'C:\>' }
    $steps += @(@{waitForScreen=$ready;text="a:\ctmouse /p`r`na:\mousechk`r`n"},
        @{waitForScreen='PS2-DRIVER-READY';mouse=@{x=4;y=-3;buttons=1}},
        @{waitForScreen='PS2-LEFT-MOTION-OK';mouse=@{x=-2;y=1;buttons=2}},
        @{waitForScreen='PS2-RIGHT-MOTION-OK';mouse=@{x=0;y=0;buttons=0}},
        @{waitForScreen='PS2-DRIVER-RETURN-OK';text="a:\ctmouse /u`r`na:\ctmouse /p`r`na:\mousegfx`r`n"},
        @{waitForDebug='PS2-GRAPHICS-13-READY';mouse=@{x=0;y=0;buttons=1}},
        @{waitForDebug='PS2-GRAPHICS-12-READY';mouse=@{x=0;y=0;buttons=0}},
        @{waitForScreen='PS2-GRAPHICS-RETURN-OK';text="echo PS2-FINISH`r`n"})
    [IO.File]::WriteAllText(($prefix+'.scenario.json'),($steps | ConvertTo-Json -Depth 5))
    dotnet $runner @diskArgs --floppy-a $floppy --profile $profile --scenario ($prefix+'.scenario.json') --steps 100000000 --report ($prefix+'.json') --frame ($prefix+'.ppm') --mouse-io-trace ($prefix+'.io.jsonl') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Mouse guest failed: $guest" }
    python (Join-Path $workspaceRoot 'tests\verify-mouse.py') ($prefix+'.json') ($prefix+'.io.jsonl') | Set-Content -LiteralPath ($prefix+'.verified.json')
    if ($LASTEXITCODE -ne 0) { throw "Independent KBC/IRQ mouse verification failed: $guest" }
    $result = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
    if ($guest -eq 'msdos622' -and (Get-DosFileHash -LiteralPath $privateCow).Hash -ne $hashes[$MsdosOverlay]) { throw 'MS-DOS read-only mouse acceptance modified installed overlay.' }
    $stages += [pscustomobject]@{guest=$guest;instructions=$result.status.instructions;nanoseconds=$result.status.virtualNanoseconds;frameHash=$result.frameHash;ataRead=$result.ataSectorsRead;floppyRead=$result.floppySectorsRead;packets=$result.ps2Mouse.packetCount}
}
if ($stages[0].frameHash -ne $stages[1].frameHash -or $stages[0].nanoseconds -ne $stages[1].nanoseconds) { throw 'Mouse cold boots differ.' }
# Negative detection must use the same disk/driver with absent hardware.
$none = Get-Content -Raw -LiteralPath $profile | ConvertFrom-Json; $none.mouse='none'
$none | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'none.json')
$negative = Join-Path $testRoot 'negative'
[IO.File]::WriteAllText(($negative+'.txt'),"a:\ctmouse /p`r`na:\mousechk /n`r`necho PS2-NEGATIVE-END`r`n")
dotnet $runner --disk $freeDos --floppy-a $floppy --profile (Join-Path $testRoot 'none.json') --type-file ($negative+'.txt') --steps 80000000 --report ($negative+'.json') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Negative mouse guest failed.' }
$negativeResult=Get-Content -Raw -LiteralPath ($negative+'.json') | ConvertFrom-Json
if ($negativeResult.status.fault -or $negativeResult.ps2Mouse -or -not $negativeResult.status.debugOutput.Contains('PS2-ABSENT-OK') -or $negativeResult.inputBytesSent -ne $negativeResult.inputBytesTotal) { throw 'Absent mouse was incorrectly detected.' }
if ($Gui) {
    foreach ($mode in @(13,12)) {
        $guiRoot = Join-Path $testRoot ('gui-mode'+$mode); New-Item -ItemType Directory -Path $guiRoot | Out-Null
        $guiSteps = @(@{waitForScreen='C:\>';text="a:\ctmouse /p`r`na:\mousegfx`r`n"})
        if ($mode -eq 12) { $guiSteps += @{waitForDebug='PS2-GRAPHICS-13-READY';mouse=@{x=0;y=0;buttons=1}} }
        $scenario = Join-Path $guiRoot 'scenario.json'; [IO.File]::WriteAllText($scenario,(ConvertTo-Json -InputObject @($guiSteps) -Depth 5))
        $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
        $arguments = @('--disk',('"{0}"' -f $freeDos),'--floppy-a',('"{0}"' -f $floppy),'--profile',('"{0}"' -f $profile),
            '--scenario',('"{0}"' -f $scenario),'--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),
            '--acceptance-test',('"{0}"' -f $guiRoot),'--capture-after',('PS2-GRAPHICS-'+$mode+'-READY'))
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "Native mode$mode mouse acceptance timed out." }
        if ($process.ExitCode -ne 0) { throw 'Native mouse GUI failed.' }
        $result = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'smoke-result.json') | ConvertFrom-Json
        if ($result.status.fault -or $result.captures.Count -ne 6 -or -not $result.scenario.complete) { throw 'Native mouse evidence incomplete.' }
        foreach ($capture in $result.captures) {
            $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
            if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'Native mouse GL error.' }
        }
        Copy-Item -LiteralPath (Join-Path $guiRoot '00-frame.png') -Destination (Join-Path $guiRoot ('ps2-native-mode'+$mode+'.png'))
    }
}
foreach ($path in $protected) { if ((Get-DosFileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$path]) { throw 'Protected mouse test input changed.' } }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;guests=$stages;negativeDetection=$true;nativeGui=[bool]$Gui;ps2Id=0;buttons=2;wheel=$false;protectedHashes=$hashes;
    limitations='Byte-level PS/2; no electrical serial timing. Native capture is wired to OpenTK, relative/release state unit-tested; automated GUI scenarios exercise guest hardware and rendering, not physical OS pointer capture.'}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json')
$summary | ConvertTo-Json -Depth 6
