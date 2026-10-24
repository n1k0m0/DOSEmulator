param([switch]$SkipBuild, [switch]$Gui, [string]$OutputRoot = 'artifacts\m1-build')
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $OutputRoot))
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$assembler = Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
if (-not (Test-Path -LiteralPath $assembler)) { throw 'NASM is required to assemble our original DOS probes.' }
$baseImage = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
if (-not (Test-Path -LiteralPath $baseImage)) { throw 'Prepare the verified FreeDOS minimal IDE test image first.' }
$baseHash = (Get-FileHash -LiteralPath $baseImage -Algorithm SHA256).Hash
$testRoot = Join-Path $workspaceRoot ('artifacts\freedos-m2\vga-' + [guid]::NewGuid().ToString('N'))
$programs = Join-Path $testRoot 'programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
foreach ($mode in @('12','13')) {
    & $assembler -Werror -f bin ('-DTEST_MODE=0x'+$mode) -o (Join-Path $programs ('VGA'+$mode+'.COM')) (Join-Path $workspaceRoot 'tests\guests\vga-test.asm')
    if ($LASTEXITCODE -ne 0) { throw 'DOS VGA probe assembly failed.' }
}
$image = Join-Path $testRoot 'private-probes.img'
dotnet $runner --prepare-test-programs $baseImage --program-dir $programs --output $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private program disk preparation failed.' }
$imageHash = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
$results = @()
foreach ($mode in @('13','12')) {
    $commands = Join-Path $testRoot ('mode'+$mode+'-commands.txt')
    [IO.File]::WriteAllText($commands,('vga'+$mode+"`r`n"))
    $modeResults = @()
    foreach ($run in 1..2) {
        $report = Join-Path $testRoot ("mode$mode-$run.json")
        $frame = Join-Path $testRoot ("mode$mode-$run.ppm")
        dotnet $runner --disk $image --steps 60000000 --type-file $commands --stop-text ("VGA$mode-READY") --report $report --frame $frame | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Mode $mode cold boot $run failed." }
        $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
        if ($result.status.fault -or -not $result.status.debugOutput.Contains("VGA$mode-READY") -or $result.inputBytesSent -ne $result.inputBytesTotal -or -not $result.graphicsMode -or $result.ataSectorsRead -le 0) { throw 'DOS graphics probe did not reach its actual guest marker.' }
        $oracle = python (Join-Path $workspaceRoot 'tests\verify-vga-frame.py') $mode $frame $report
        if ($LASTEXITCODE -ne 0) { throw 'Independent VGA pixel verification failed.' }
        $oracle | Set-Content -LiteralPath ($frame+'.verified.json') -Encoding utf8
        $modeResults += $result
    }
    if ($modeResults[0].frameHash -ne $modeResults[1].frameHash -or $modeResults[0].status.virtualNanoseconds -ne $modeResults[1].status.virtualNanoseconds) { throw 'Independent graphics cold boots are not deterministic.' }
    $results += [pscustomobject]@{mode=$mode;width=$modeResults[0].width;height=$modeResults[0].height;frameHash=$modeResults[0].frameHash;coldBoots=2;everyPixelVerified=$true}
    if ($Gui) {
        $guiRoot = Join-Path $testRoot ('gui'+$mode)
        $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
        $arguments = @('--disk',('"{0}"' -f $image),'--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),
            '--acceptance-test',('"{0}"' -f $guiRoot),'--type-file',('"{0}"' -f $commands),'--capture-after',("VGA$mode-READY"))
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'VGA GUI acceptance timed out.' }
        if ($process.ExitCode -ne 0) { throw 'VGA GUI failed.' }
        $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'smoke-result.json') | ConvertFrom-Json
        if ($guiResult.status.fault -or $guiResult.captures.Count -ne 6 -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal) { throw 'VGA GUI evidence incomplete.' }
        foreach ($capture in $guiResult.captures) {
            $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
            if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100 -or $graphics.guestWidth -ne $modeResults[0].width -or $graphics.guestHeight -ne $modeResults[0].height) { throw 'VGA OpenGL frame or status bar incomplete.' }
        }
    }
}
$commands = Join-Path $testRoot 'return-commands.txt'
[IO.File]::WriteAllText($commands,"vga13 /a`r`nvga12 /a`r`necho M2-VGA-RETURN-OK`r`n")
$report = Join-Path $testRoot 'dos-return.json'
dotnet $runner --disk $image --steps 60000000 --type-file $commands --stop-text "`r`nM2-VGA-RETURN-OK`r`nC:\>" --report $report | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Graphics-to-DOS return failed.' }
$returned = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
foreach ($marker in @('VGA13-DOS-RETURN-OK','VGA12-DOS-RETURN-OK',"`r`nM2-VGA-RETURN-OK`r`nC:\>")) {
    if (-not $returned.status.debugOutput.Contains($marker)) { throw "Missing DOS return result: $marker" }
}
if ($returned.graphicsMode -or $returned.width -ne 640 -or $returned.height -ne 400 -or $returned.inputBytesSent -ne $returned.inputBytesTotal -or $returned.status.debugOutput.Contains('-FAILED')) { throw 'DOS text-mode return evidence incomplete.' }
if ((Get-FileHash -LiteralPath $baseImage -Algorithm SHA256).Hash -ne $baseHash -or (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash) { throw 'Read-only test media changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;modes=$results;dosReturn=$true;coldBoots=5;gui=[bool]$Gui;baseUnchanged=$true;probeImageUnchanged=$true}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
