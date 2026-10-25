#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$Gui, [string]$OutputRoot = 'artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk, [Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
$Disk = (Resolve-Path -LiteralPath $Disk).Path
$Overlay = (Resolve-Path -LiteralPath $Overlay).Path
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$assembler = Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$baseHash = (Get-DosFileHash -LiteralPath $Disk -Algorithm SHA256).Hash
$sourceHash = (Get-DosFileHash -LiteralPath $Overlay -Algorithm SHA256).Hash
$testRoot = Join-Path $workspaceRoot ('artifacts\msdos-m2\fonts-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$probe = Join-Path $testRoot 'FONTCP.COM'
$floppy = Join-Path $testRoot 'own-probe.img'
& $assembler -Werror -f bin -o $probe (Join-Path $workspaceRoot 'tests\guests\font-codepage-test.asm')
if ($LASTEXITCODE -ne 0) { throw 'Font probe assembly failed.' }
python (Join-Path $workspaceRoot 'tests\prepare-font-media.py') $probe $floppy > (Join-Path $testRoot 'probe-media.json')
if ($LASTEXITCODE -ne 0) { throw 'Private own-probe media preparation failed.' }
$floppyHash = (Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash
function Write-Scenario($Name, $Steps) {
    $path = Join-Path $testRoot ($Name + '-scenario.json')
    $Steps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
function Invoke-Guest($Name, $Cow, $Scenario, [bool]$Capture) {
    $report = Join-Path $testRoot ($Name + '.json')
    $guestArguments = @($runner,'--disk',$Disk,'--overlay',$Cow,'--floppy-a',$floppy,'--steps','100000000',
        '--scenario',$Scenario,'--report',$report)
    if ($Capture) { $guestArguments += @('--stop-text','FONT-CP-READY','--frame',(Join-Path $testRoot ($Name+'.ppm')),
        '--video-io-trace',(Join-Path $testRoot ($Name+'-video.jsonl'))) }
    dotnet @guestArguments > (Join-Path $testRoot ($Name+'-stdout.json'))
    if ($LASTEXITCODE -ne 0) { throw "Guest $Name failed." }
    $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    if ($result.status.fault -or -not $result.scenario.complete -or $result.scenario.inputBytesSent -ne $result.scenario.inputBytesTotal) {
        throw "Guest $Name did not complete its real KBC input."
    }
    return $result
}
# ANSI.SYS is needed by Microsoft's MODE LINES command. Add it using real DOS
# commands in ONE NEW private COW, leaving the accepted installation unchanged.
$ansiCow = Join-Path $testRoot 'private-ansi.cow'
Copy-Item -LiteralPath $Overlay -Destination $ansiCow
$configure = Write-Scenario 'configure-ansi' @(
    @{waitForScreen='C:\>';text="kezb us`r`n"},
    @{waitForScreen='C:\>keyb us';text="copy config.sys config.pre`r`necho DEVICE=C:\DOS\ANSI.SYS >>config.sys`r`necho FONT-ANSI-CONFIG-OK`r`n"})
$configured = Invoke-Guest 'configure-ansi' $ansiCow $configure $false
if (-not $configured.text.Contains('FONT-ANSI-CONFIG-OK') -or -not $configured.text.TrimEnd().EndsWith('C:\>') -or $configured.ataSectorsWritten -eq 0) { throw 'ANSI preparation did not return to DOS.' }
python (Join-Path $workspaceRoot 'tests\verify-font-frame.py') --base $Disk --overlay $ansiCow --original-overlay $Overlay > (Join-Path $testRoot 'ansi-files.verified.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent private installation/ANSI verification failed.' }
$ansiHash = (Get-DosFileHash -LiteralPath $ansiCow -Algorithm SHA256).Hash
$cases = @(
    @{name='canonical25';source=$Overlay;points=16;rows=25;command='a:fontcp';mode=$null;gui=$true},
    @{name='bios28';source=$Overlay;points=14;rows=28;command='a:fontcp /14';mode=$null;gui=$true},
    @{name='mode25';source=$ansiCow;points=16;rows=25;command='a:fontcp';mode=25;gui=$false},
    @{name='mode43';source=$ansiCow;points=8;rows=43;command='a:fontcp';mode=43;gui=$false},
    @{name='mode50';source=$ansiCow;points=8;rows=50;command='a:fontcp';mode=50;gui=$true})
$results = @()
foreach ($case in $cases) {
    $steps = @(@{waitForScreen='C:\>';text="kezb us`r`n"})
    if ($null -ne $case.mode) {
        $steps += @{waitForScreen='C:\>keyb us';text=("mode con lines=$($case.mode)`r`necho FONT-MODE-$($case.mode)-OK`r`n")}
        $steps += @{waitForScreen="FONT-MODE-$($case.mode)-OK";text=($case.command+"`r`n")}
    } else { $steps += @{waitForScreen='C:\>keyb us';text=($case.command+"`r`n")} }
    $scenario = Write-Scenario $case.name $steps
    $runs = @()
    foreach ($run in 1..2) {
        $name = $case.name + '-' + $run
        $cow = Join-Path $testRoot ($name + '.cow')
        Copy-Item -LiteralPath $case.source -Destination $cow
        $cowHash = (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash
        $result = Invoke-Guest $name $cow $scenario $true
        if (-not $result.status.debugOutput.Contains('FONT-CP-READY')) { throw 'Font probe marker missing.' }
        python (Join-Path $workspaceRoot 'tests\verify-font-frame.py') --base $Disk --overlay $cow --points $case.points --rows $case.rows `
            --frame (Join-Path $testRoot ($name+'.ppm')) --report (Join-Path $testRoot ($name+'.json')) > (Join-Path $testRoot ($name+'.verified.json'))
        if ($LASTEXITCODE -ne 0) { throw 'Independent complete CPI/pixel verification failed.' }
        if ((Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $cowHash) { throw 'Read-only font guest changed its private COW.' }
        $runs += $result
    }
    if ($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds) { throw 'Font cold boots are not deterministic.' }
    $results += [pscustomobject]@{name=$case.name;points=$case.points;rows=$case.rows;width=$runs[0].width;height=$runs[0].height;
        frameHash=$runs[0].frameHash;virtualNanoseconds=$runs[0].status.virtualNanoseconds;coldBoots=2;everyPixelVerified=$true}
    if ($Gui -and $case.gui) {
        $guiRoot = Join-Path $testRoot ('gui-' + $case.name)
        $guiCow = Join-Path $testRoot ('gui-' + $case.name + '.cow')
        Copy-Item -LiteralPath $case.source -Destination $guiCow
        $guiCowHash = (Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash
        $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
        $arguments = @('--disk',('"{0}"' -f $Disk),'--overlay',('"{0}"' -f $guiCow),'--floppy-a',('"{0}"' -f $floppy),
            '--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),'--acceptance-test',('"{0}"' -f $guiRoot),
            '--scenario',('"{0}"' -f $scenario),'--capture-after','FONT-CP-READY')
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Own font GUI acceptance timed out.' }
        if ($process.ExitCode -ne 0) { throw 'Own font GUI acceptance failed.' }
        $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'smoke-result.json') | ConvertFrom-Json
        if ($guiResult.status.fault -or $guiResult.captures.Count -ne 6 -or -not $guiResult.scenario.complete) { throw 'Font GUI evidence incomplete.' }
        foreach ($capture in $guiResult.captures) {
            $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json
            if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100 -or $graphics.guestWidth -ne $runs[0].width -or $graphics.guestHeight -ne $runs[0].height) { throw 'Font OpenGL capture or status bar incomplete.' }
        }
        if ((Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash -ne $guiCowHash) { throw 'GUI font guest changed its COW.' }
    }
}
# Multiple live changes in one actual COMMAND.COM process, followed by normal
# program exits and a further DOS command. ANSI draws directly to VGA memory.
$returnCow = Join-Path $testRoot 'private-live-return.cow'
Copy-Item -LiteralPath $ansiCow -Destination $returnCow
$returnSteps = @(@{waitForScreen='C:\>';text="kezb us`r`n"})
$wait = 'C:\>keyb us'
foreach ($lines in @(50,43,25)) {
    $returnSteps += @{waitForScreen=$wait;text=("mode con lines=$lines`r`na:fontcp /a`r`necho FONT-RETURN-$lines-OK`r`n")}
    $wait = "FONT-RETURN-$lines-OK"
}
$returnScenario = Write-Scenario 'live-return' $returnSteps
$returned = Invoke-Guest 'live-return' $returnCow $returnScenario $false
if (-not $returned.text.Contains('FONT-RETURN-25-OK') -or ([regex]::Matches($returned.status.debugOutput,'FONT-CP-READY')).Count -ne 3) {
    throw 'Live font sequence did not execute all three probes. Earlier return screens are required by subsequent scenario gates.'
}
if (-not $returned.text.TrimEnd().EndsWith('C:\>') -or $returned.ataSectorsWritten -ne 0 -or $returned.floppySectorsWritten -ne 0) { throw 'Live font modes did not finish at a read-only DOS prompt.' }
if ((Get-DosFileHash -LiteralPath $Disk -Algorithm SHA256).Hash -ne $baseHash -or
    (Get-DosFileHash -LiteralPath $Overlay -Algorithm SHA256).Hash -ne $sourceHash -or
    (Get-DosFileHash -LiteralPath $ansiCow -Algorithm SHA256).Hash -ne $ansiHash -or
    (Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash -ne $floppyHash) { throw 'Private font inputs changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;cases=$results;coldBoots=12;guiProcesses=$(if($Gui){3}else{0});
    liveModeChanges=@(50,43,25);dosReturn=$true;originalInstallationUnchanged=$true;baseUnchanged=$true;onlyOwnProbeOnFloppy=$true}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
