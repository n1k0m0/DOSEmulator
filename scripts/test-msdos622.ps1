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
$mediaRoot = Join-Path $workspaceRoot 'Disks and CDs (DO NOT PUT INTO GITHUB)\Microsoft MS-DOS 6.22 [German] (3.5-1.44mb)'
$disks = @(1..3 | ForEach-Object { Join-Path $mediaRoot ('Disk0'+$_+'.img') })
$originalHashes = @($disks | ForEach-Object { (Get-DosFileHash -LiteralPath $_ -Algorithm SHA256).Hash })
$testRoot = Join-Path $workspaceRoot ('artifacts\msdos-m2\install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$image = Join-Path $testRoot 'private-blank-32m.img'; $cow = Join-Path $testRoot 'private-install.cow'
$file = [IO.File]::Open($image,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
try { $file.SetLength(33554432) } finally { $file.Dispose() }
$baseHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$stages = @()
# Writes an explicit private guest-input plan with bounded waits and required output markers.
function Scenario([string]$Name, [object[]]$Plan) {
    $path = Join-Path $testRoot ($Name+'.scenario.json')
    ConvertTo-Json -InputObject $Plan -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
# Runs the actual headless guest with explicit private media, captures evidence and rejects execution failures.
function Guest([string]$Name, [string[]]$Arguments, [string[]]$Required) {
    $prefix = Join-Path $testRoot $Name
    dotnet $runner --disk $image --overlay $cow @Arguments --report ($prefix+'.json') --frame ($prefix+'.ppm') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MS-DOS guest failed: $prefix" }
    $result = Get-Content -Raw -LiteralPath ($prefix+'.json') | ConvertFrom-Json
    if ($result.status.fault -or ($null -ne $result.scenario -and -not $result.scenario.complete)) { throw "Incomplete MS-DOS scenario: $prefix" }
    foreach ($text in $Required) { if (-not $result.status.debugOutput.Contains($text)) { throw "Missing MS-DOS output '$text': $prefix" } }
    $script:stages += [pscustomobject]@{name=$Name;instructions=$result.status.instructions;nanoseconds=$result.status.virtualNanoseconds;
        read=$result.ataSectorsRead;written=$result.ataSectorsWritten;floppyRead=$result.floppySectorsRead;frameHash=$result.frameHash;scenario=$result.scenario}
    return $result
}
$partition = Scenario partition @(
    @{waitForScreen='Willkommen zum Setup.';text="`r`n"},
    @{waitForScreen='Nichtzugeordneten Speicherplatz konfigurieren';text="`r`n"})
$result = Guest partition @('--floppy-a',$disks[0],'--boot','floppy-a','--steps','200000000','--scenario',$partition,
    '--stop-screen','Computer jetzt neu starten','--bios-trace',(Join-Path $testRoot 'partition.bios.jsonl')) @()
if (-not $result.text.Contains('Computer jetzt neu starten') -or $result.ataSectorsWritten -ne 3 -or $result.floppySectorsWritten -ne 0) { throw 'Original setup did not partition the private disk.' }
# Explicit host cold boot at setup's requested reboot boundary. Guest hardware-reset
# continuation is a separate pending feature, not silently claimed by this test.
$install = Scenario install @(
    @{waitForScreen='Die Einstellungen sind richtig.';text="`r`n"},
    @{waitForScreen='folgenden Verzeichnis';text="`r`n"},
    @{waitForScreen='Setup-Diskette 2';insertFloppy=@{drive=0;path=$disks[1]};text="`r`n"},
    @{waitForScreen='Setup-Diskette 3';insertFloppy=@{drive=0;path=$disks[2]};text="`r`n"})
$result = Guest install @('--floppy-a',$disks[0],'--boot','floppy-a','--steps','3000000000','--scenario',$install,
    '--stop-screen','alle Disketten','--bios-trace',(Join-Path $testRoot 'install.bios.jsonl')) @()
if (-not $result.text.Contains('alle Disketten') -or $result.scenario.mediaChanges -ne 2 -or $result.ataSectorsRead -lt 65500 -or $result.ataSectorsWritten -lt 12000 -or $result.floppySectorsWritten -ne 0) { throw 'Original three-disk installation or full disk verification did not complete.' }
Copy-Item -LiteralPath $cow -Destination (Join-Path $testRoot 'installed-pristine.cow')
# Checks the private installed FAT image with the independent read-only Python oracle.
function Verify-Fat([string]$Name) {
    $verification = python (Join-Path $workspaceRoot 'tests\verify-msdos-install.py') $image $cow $disks[0]
    if ($LASTEXITCODE -ne 0) { throw "Independent MS-DOS FAT/source comparison failed: $Name" }
    $verification | Set-Content -LiteralPath (Join-Path $testRoot ($Name+'.verified.json')) -Encoding utf8
}
Verify-Fat installed
# Builds the explicit keyboard-input/wait plan for the requested DOS command sequence.
function Dos-Scenario([string]$Name, [string]$Commands) {
    # SETUP selected KEYB GR. The test typer emits physical US set-1, so 'kezb'
    # becomes 'keyb' under GR. Select US through the real DOS command first,
    # without changing the installer-generated AUTOEXEC/CONFIG files.
    return Scenario $Name @(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text=$Commands})
}
$bootResults = @()
foreach ($number in 1..2) {
    $plan = Dos-Scenario "cold-$number" "ver`r`ndir c:\`r`ntype c:\config.sys`r`necho M2-MSDOS-COLD-OK`r`n"
    $oldHash = (Get-DosFileHash -LiteralPath $cow).Hash
    $result = Guest "cold-$number" @('--steps','30000000','--scenario',$plan) @('MS-DOS Version 6.22','DEVICE=C:\DOS\SETVER.EXE',"`r`nM2-MSDOS-COLD-OK`r`n")
    if ($result.ataSectorsWritten -ne 0 -or $result.floppySectorsRead -ne 0 -or $result.floppySectorsWritten -ne 0 -or -not $result.status.debugOutput.EndsWith('C:\>') -or (Get-DosFileHash -LiteralPath $cow).Hash -ne $oldHash) { throw 'HDD-only cold boot changed the installed medium or did not return.' }
    $bootResults += $result
}
if ($bootResults[0].frameHash -ne $bootResults[1].frameHash -or $bootResults[0].status.virtualNanoseconds -ne $bootResults[1].status.virtualNanoseconds) { throw 'Independent MS-DOS HDD boots differ.' }
$plan = Dos-Scenario files "ver`r`necho M2-MSDOS-PERSISTED>c:\probe.txt`r`nmd c:\m2test`r`ncopy c:\probe.txt c:\m2test\copy.txt`r`nren c:\m2test\copy.txt kept.txt`r`ntype c:\m2test\kept.txt`r`necho M2-MSDOS-WRITE-OK`r`n"
$result = Guest files @('--steps','30000000','--scenario',$plan) @("`r`nM2-MSDOS-PERSISTED`r`n","`r`nM2-MSDOS-WRITE-OK`r`n")
if ($result.ataSectorsWritten -eq 0 -or -not $result.status.debugOutput.EndsWith('C:\>')) { throw 'DOS file creation did not finish.' }
Verify-Fat files
$plan = Dos-Scenario persisted "type c:\probe.txt`r`ntype c:\m2test\kept.txt`r`nren c:\m2test\kept.txt again.txt`r`ntype c:\m2test\again.txt`r`ndel c:\m2test\again.txt`r`nrd c:\m2test`r`ndel c:\probe.txt`r`necho M2-MSDOS-REBOOT-DELETE-OK`r`n"
$result = Guest persisted @('--steps','30000000','--scenario',$plan) @("`r`nM2-MSDOS-REBOOT-DELETE-OK`r`n")
if ([regex]::Matches($result.status.debugOutput,"`r`nM2-MSDOS-PERSISTED`r`n").Count -ne 3 -or $result.ataSectorsWritten -eq 0 -or -not $result.status.debugOutput.EndsWith('C:\>')) { throw 'Persisted DOS files or rename/delete differ.' }
$plan = Dos-Scenario deleted "if not exist c:\probe.txt echo M2-MSDOS-FILE-DELETED`r`nif not exist c:\m2test echo M2-MSDOS-DIR-DELETED`r`necho M2-MSDOS-FINAL-OK`r`n"
$result = Guest deleted @('--steps','30000000','--scenario',$plan) @("`r`nM2-MSDOS-FILE-DELETED`r`n","`r`nM2-MSDOS-DIR-DELETED`r`n","`r`nM2-MSDOS-FINAL-OK`r`n")
if ($result.ataSectorsWritten -ne 0 -or -not $result.status.debugOutput.EndsWith('C:\>')) { throw 'Deletion persistence did not finish cleanly.' }
Verify-Fat final
$pristine = Get-Content -LiteralPath (Join-Path $testRoot 'installed.verified.json') -Raw | ConvertFrom-Json
$finalFiles = Get-Content -LiteralPath (Join-Path $testRoot 'final.verified.json') -Raw | ConvertFrom-Json
foreach ($path in @('CONFIG.SYS','AUTOEXEC.BAT')) {
    if ($pristine.allFileSha256.$path -ne $finalFiles.allFileSha256.$path) { throw 'Installer-generated startup files changed.' }
}
if ($Gui) {
    $guiRoot = Join-Path $testRoot 'gui'; New-Item -ItemType Directory -Path $guiRoot -Force | Out-Null
    $commands = Join-Path $guiRoot 'commands.txt'; [IO.File]::WriteAllText($commands,"ver`r`n")
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    $arguments = @('--disk',('"{0}"' -f $image),'--overlay',('"{0}"' -f $cow),'--data-dir',('"{0}"' -f (Join-Path $guiRoot 'settings')),
        '--acceptance-test',('"{0}"' -f $guiRoot),'--type-file',('"{0}"' -f $commands),'--capture-after','"MS-DOS Version 6.22"')
    $oldHash = (Get-DosFileHash -LiteralPath $cow).Hash
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'MS-DOS native GUI acceptance timed out.' }
    if ($process.ExitCode -ne 0) { throw 'MS-DOS native GUI failed.' }
    $guiResult = Get-Content -Raw -LiteralPath (Join-Path $guiRoot 'smoke-result.json') | ConvertFrom-Json
    if ($guiResult.status.fault -or $guiResult.captures.Count -ne 6 -or $guiResult.inputBytesSent -ne $guiResult.inputBytesTotal -or -not $guiResult.status.debugOutput.Contains('MS-DOS Version 6.22') -or (Get-DosFileHash -LiteralPath $cow).Hash -ne $oldHash) { throw 'MS-DOS GUI or read-only guest commands failed.' }
    foreach ($capture in $guiResult.captures) { $graphics = Get-Content -Raw -LiteralPath ($capture+'.json') | ConvertFrom-Json; if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw 'MS-DOS OpenGL capture incomplete.' } }
}
foreach ($number in 0..2) { if ((Get-DosFileHash -LiteralPath $disks[$number] -Algorithm SHA256).Hash -ne $originalHashes[$number]) { throw 'Original MS-DOS floppy changed.' } }
if ((Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $baseHash) { throw 'Blank base image changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$testRoot;source='Local German MS-DOS 6.22 disks 1..3';coldBoots=7;hostColdBootAtPartitionBoundary=$true;
    originalSetup=$true;sourceMediaUnchanged=$true;baseUnchanged=$true;privateOverlay=$true;hddOnlyBoots=5;guestWritesAndPersistence=$true;
    installedStartupUnmodified=$true;himemEmm386Tested=$false;allRealModeProgramsSupported=$false;gui=[bool]$Gui;stages=$stages}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
