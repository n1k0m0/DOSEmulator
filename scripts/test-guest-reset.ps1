# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$Install, [switch]$Gui, [string]$OutputRoot = 'artifacts\m1-build', [string]$MsdosDisk, [string]$MsdosOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $OutputRoot }
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$testRoot = Join-Path $workspaceRoot ('artifacts\reset-m2\acceptance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$canonical = Join-Path $workspaceRoot 'artifacts\msdos-m2\install-16e08d9720dc4b76b7d250fa926a277b'
$disk = if ($MsdosDisk) { (Resolve-Path -LiteralPath $MsdosDisk).Path } else { Join-Path $canonical 'private-blank-32m.img' }
$originalCow = if ($MsdosOverlay) { (Resolve-Path -LiteralPath $MsdosOverlay).Path } else { Join-Path $canonical 'installed-pristine.cow' }
$cowHash = (Get-DosFileHash -LiteralPath $originalCow -Algorithm SHA256).Hash
$media = Join-Path $workspaceRoot 'Disks and CDs (DO NOT PUT INTO GITHUB)\Microsoft MS-DOS 6.22 [German] (3.5-1.44mb)'
$mediaHashes = Get-ChildItem -LiteralPath $media -Filter '*.img' | Get-DosFileHash -Algorithm SHA256
$probe = Join-Path $testRoot 'RESET.COM'; $floppy = Join-Path $testRoot 'reset-tools.img'
& (Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $workspaceRoot 'tests\guests\hardware-reset-test.asm') -o $probe
if ($LASTEXITCODE -ne 0) { throw 'Reset guest assembly failed' }
python (Join-Path $workspaceRoot 'tests\prepare-font-media.py') $probe $floppy 'RESET.COM' > (Join-Path $testRoot 'own-floppy.json')
if ($LASTEXITCODE -ne 0) { throw 'Reset probe floppy failed' }
$stages = @()
# Writes an explicit private guest-input plan with bounded waits and required output markers.
function Scenario([string]$Name, [object[]]$Plan) {
    $path = Join-Path $testRoot ($Name+'.scenario.json')
    ConvertTo-Json -InputObject $Plan -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
foreach ($number in 1..2) {
    $overlay = Join-Path $testRoot ('msdos-'+$number+'.cow')
    Copy-Item -LiteralPath $originalCow -Destination $overlay
    $plan = Scenario ('msdos-'+$number) @(
        @{waitForScreen='C:\>';text="kezb us`r`n"},
        @{waitForScreen='C:\>keyb us';text="echo PERSISTENT-RESET-DATA>c:\reset.txt`r`na:\reset`r`n"},
        @{waitForScreen='C:\>';minimumResetCount=1;text="kezb us`r`n"},
        @{waitForScreen='C:\>keyb us';minimumResetCount=1;text="type c:\reset.txt`r`nver`r`na:\reset /92`r`n"},
        @{waitForScreen='C:\>';minimumResetCount=2;text="kezb us`r`n"},
        @{waitForScreen='C:\>keyb us';minimumResetCount=2;scanCodes=@(0x1d,0x38,0x53,0xd3,0xb8,0x9d)},
        # Microsoft's resident KEYB handles Ctrl+Alt+Del by jumping to BIOS, not by pulsing RESET.
        @{waitForScreen='C:\>';minimumResetCount=2;minimumPost10Count=4;text="kezb us`r`n"},
        @{waitForScreen='C:\>keyb us';minimumResetCount=2;minimumPost10Count=4;text="type c:\reset.txt`r`nver`r`necho M2-THREE-GUEST-RESETS-OK`r`n"})
    $prefix = Join-Path $testRoot ('msdos-'+$number)
    dotnet $runner --disk $disk --overlay $overlay --floppy-a $floppy --boot ide --steps 150000000 --scenario $plan --stop-text "`r`nM2-THREE-GUEST-RESETS-OK`r`n" --report ($prefix+'.json') --frame ($prefix+'.ppm') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MS-DOS reset guest failed: $prefix" }
    $result = Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if ($result.status.fault -or $result.hardwareResetCount -ne 2 -or $result.status.reset.post10Count -ne 4 -or -not $result.scenario.complete -or $result.floppySectorsWritten -ne 0 -or $result.status.reset.warmBootFlag -ne 0x1234 -or $result.status.reset.source -ne 'port92') { throw "Reset sequence incomplete: $prefix" }
    if (-not $result.text.Contains('PERSISTENT-RESET-DATA') -or -not $result.text.Contains('MS-DOS Version 6.22')) { throw 'Post-reset file read/version missing' }
    python (Join-Path $workspaceRoot 'tests\verify-msdos-install.py') $disk $overlay (Join-Path $media 'Disk01.img') > ($prefix+'.verified.json')
    if ($LASTEXITCODE -ne 0) { throw 'Independent post-reset FAT check failed' }
    python (Join-Path $workspaceRoot 'tests\verify-reset-media.py') $disk $originalCow $overlay 'RESET.TXT' 'PERSISTENT-RESET-DATA' > ($prefix+'.files-verified.json')
    if ($LASTEXITCODE -ne 0) { throw 'Unexpected change to the original DOS installation' }
    $stages += [pscustomobject]@{name=('msdos-'+$number);resets=$result.hardwareResetCount;reset=$result.status.reset;frameHash=$result.frameHash;nanoseconds=$result.status.virtualNanoseconds;scenario=$result.scenario}
}
if ($stages[0].frameHash -ne $stages[1].frameHash -or $stages[0].nanoseconds -ne $stages[1].nanoseconds) { throw 'MS-DOS guest resets are not deterministic' }
$freeDos = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$freeDosHash = (Get-DosFileHash -LiteralPath $freeDos -Algorithm SHA256).Hash
foreach ($number in 1..2) {
    $overlay = Join-Path $testRoot ('freedos-'+$number+'.cow')
    $plan = Scenario ('freedos-'+$number) @(
        @{waitForScreen='C:\>';text="echo PERSISTENT-FREEDOS-RESET>c:\reset.txt`r`na:\reset`r`n"},
        @{waitForScreen='C:\>';minimumResetCount=1;text="type c:\reset.txt`r`na:\reset /92`r`n"},
        @{waitForScreen='C:\>';minimumResetCount=2;text="type c:\reset.txt`r`nver`r`necho M2-FREEDOS-RESETS-OK`r`n"})
    $prefix = Join-Path $testRoot ('freedos-'+$number)
    dotnet $runner --disk $freeDos --overlay $overlay --floppy-a $floppy --boot ide --steps 100000000 --scenario $plan --stop-text "`r`nM2-FREEDOS-RESETS-OK`r`n" --report ($prefix+'.json') --frame ($prefix+'.ppm') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'FreeDOS reset guest failed' }
    $result = Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if ($result.status.fault -or $result.hardwareResetCount -ne 2 -or $result.status.reset.post10Count -ne 3 -or -not $result.scenario.complete -or $result.floppySectorsWritten -ne 0 -or -not $result.text.Contains('PERSISTENT-FREEDOS-RESET')) { throw 'FreeDOS reset sequence incomplete' }
    python (Join-Path $workspaceRoot 'tests\verify-reset-media.py') $freeDos '-' $overlay 'RESET.TXT' 'PERSISTENT-FREEDOS-RESET' > ($prefix+'.files-verified.json')
    if ($LASTEXITCODE -ne 0) { throw 'Independent FreeDOS reset persistence verification failed' }
    $stages += [pscustomobject]@{name=('freedos-'+$number);resets=$result.hardwareResetCount;reset=$result.status.reset;frameHash=$result.frameHash;nanoseconds=$result.status.virtualNanoseconds;scenario=$result.scenario}
}
if ($stages[2].frameHash -ne $stages[3].frameHash -or $stages[2].nanoseconds -ne $stages[3].nanoseconds) { throw 'FreeDOS reset runs differ' }
if ((Get-DosFileHash -LiteralPath $freeDos -Algorithm SHA256).Hash -ne $freeDosHash) { throw 'FreeDOS base changed' }
if ($Gui) {
    $guiRoot = Join-Path $testRoot 'gui'; New-Item -ItemType Directory -Path $guiRoot | Out-Null
    $guiOverlay = Join-Path $testRoot 'gui-private.cow'; Copy-Item -LiteralPath $originalCow -Destination $guiOverlay
    $exe = Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
    $guiArguments = @('--disk',('"'+$disk+'"'),'--overlay',('"'+$guiOverlay+'"'),'--floppy-a',('"'+$floppy+'"'),'--boot','ide',
        '--scenario',('"'+(Join-Path $testRoot 'msdos-1.scenario.json')+'"'),'--acceptance-test',('"'+$guiRoot+'"'),
        '--capture-after','M2-THREE-GUEST-RESETS-OK','--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'))
    $process = Start-Process -FilePath $exe -ArgumentList $guiArguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Native reset acceptance timed out' }
    if ($process.ExitCode -ne 0) { throw 'Native reset acceptance failed' }
    $result = Get-Content -LiteralPath (Join-Path $guiRoot 'smoke-result.json') -Raw | ConvertFrom-Json
    if ($result.status.fault -or $result.status.reset.count -ne 2 -or $result.status.reset.post10Count -ne 4 -or -not $result.scenario.complete -or $result.captures.Count -ne 6) { throw 'Native reset or menu captures incomplete' }
    python (Join-Path $workspaceRoot 'tests\verify-reset-media.py') $disk $originalCow $guiOverlay 'RESET.TXT' 'PERSISTENT-RESET-DATA' > (Join-Path $guiRoot 'files-verified.json')
    if ($LASTEXITCODE -ne 0) { throw 'Native guest file check failed' }
    $stages += [pscustomobject]@{name='native-msdos-resets';reset=$result.status.reset;captures=$result.captures;scenario=$result.scenario}
}
if ($Install) {
    $installBase = Join-Path $testRoot 'new-blank-32m.img'; $installCow = Join-Path $testRoot 'new-install.cow'
    $file = [IO.File]::Open($installBase,[IO.FileMode]::CreateNew); try { $file.SetLength(33554432) } finally { $file.Dispose() }
    $plan = Scenario setup @(
        @{waitForScreen='Willkommen zum Setup.';text="`r`n"},
        @{waitForScreen='Nichtzugeordneten Speicherplatz konfigurieren';text="`r`n"},
        @{waitForScreen='Computer jetzt neu starten';text="`r`n"},
        @{waitForScreen='Die Einstellungen sind richtig.';text="`r`n"},
        @{waitForScreen='folgenden Verzeichnis';text="`r`n"},
        @{waitForScreen='Setup-Diskette 2';insertFloppy=@{drive=0;path=(Join-Path $media 'Disk02.img')};text="`r`n"},
        @{waitForScreen='Setup-Diskette 3';insertFloppy=@{drive=0;path=(Join-Path $media 'Disk03.img')};text="`r`n"})
    $prefix = Join-Path $testRoot 'setup'
    dotnet $runner --disk $installBase --overlay $installCow --floppy-a (Join-Path $media 'Disk01.img') --boot floppy-a --steps 3000000000 --scenario $plan --stop-screen 'alle Disketten' --bios-trace ($prefix+'.bios.jsonl') --report ($prefix+'.json') --frame ($prefix+'.ppm') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Continuous original MS-DOS installation failed' }
    $result = Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if ($result.status.fault -or -not $result.scenario.complete -or $result.scenario.mediaChanges -ne 2 -or -not $result.text.Contains('alle Disketten') -or $result.ataSectorsWritten -lt 12000 -or $result.floppySectorsWritten -ne 0) { throw 'Original setup did not reboot itself and complete' }
    python (Join-Path $workspaceRoot 'tests\verify-msdos-install.py') $installBase $installCow (Join-Path $media 'Disk01.img') > ($prefix+'.verified.json')
    if ($LASTEXITCODE -ne 0) { throw 'Independent original setup verification failed' }
    $stages += [pscustomobject]@{name='continuous-original-setup';resets=$result.hardwareResetCount;reset=$result.status.reset;ataRead=$result.ataSectorsRead;ataWritten=$result.ataSectorsWritten;floppyRead=$result.floppySectorsRead;scenario=$result.scenario}
}
foreach ($hash in $mediaHashes) { if ((Get-DosFileHash -LiteralPath $hash.Path -Algorithm SHA256).Hash -ne $hash.Hash) { throw 'Original media changed' } }
if ((Get-DosFileHash -LiteralPath $originalCow -Algorithm SHA256).Hash -ne $cowHash) { throw 'Canonical installation changed' }
[pscustomobject]@{passed=$true;readOnlyOriginals=$true;testRoot=$testRoot;stages=$stages} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
Write-Output (Join-Path $testRoot 'acceptance-summary.json')
