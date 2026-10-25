#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Disk, [Parameter(Mandatory)][string]$Overlay,
    [switch]$SkipBuild, [string]$OutputRoot = 'artifacts\m1-build')
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$runner = Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$source = Join-Path $workspaceRoot 'Disks and CDs (DO NOT PUT INTO GITHUB)\Microsoft MS-DOS 6.22 [German] (3.5-1.44mb)\Disk01.img'
$Disk = (Resolve-Path -LiteralPath $Disk).Path; $Overlay = (Resolve-Path -LiteralPath $Overlay).Path
$sourceHashes = @((Get-DosFileHash -LiteralPath $Disk).Hash,(Get-DosFileHash -LiteralPath $Overlay).Hash,(Get-DosFileHash -LiteralPath $source).Hash)
$root = Join-Path $workspaceRoot ('artifacts\msdos-m2\format-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$privateHdd = Join-Path $root 'private-hdd.cow'; Copy-Item -LiteralPath $Overlay -Destination $privateHdd
$check = python (Join-Path $workspaceRoot 'tests\verify-msdos-install.py') $Disk $privateHdd $source
if ($LASTEXITCODE -ne 0) { throw 'Supply a verified local MS-DOS 6.22 installation overlay.' }
$check | Set-Content -LiteralPath (Join-Path $root 'hdd-source.verified.json') -Encoding utf8
$base = Join-Path $root 'private-scratch.img'; $cow = Join-Path $root 'private-b.cow'
$scratch = [byte[]]::new(1474560); [Array]::Fill[byte]($scratch,0x39)
$file = [IO.File]::Open($base,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write); try { $file.Write($scratch) } finally { $file.Dispose() }
$baseHash = (Get-DosFileHash -LiteralPath $base).Hash; $hddHash = (Get-DosFileHash -LiteralPath $privateHdd).Hash
function Plan([string]$Name, [object[]]$Steps) {
    $path = Join-Path $root ($Name+'.scenario.json'); ConvertTo-Json -InputObject $Steps -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8; return $path
}
function Guest([string]$Name, [string]$Scenario, [string]$Limit) {
    $prefix = Join-Path $root $Name
    dotnet $runner --disk $Disk --overlay $privateHdd --floppy-a $base --floppy-b $base --floppy-b-overlay $cow --boot ide --steps $Limit --scenario $Scenario --report ($prefix+'.json') --frame ($prefix+'.ppm') --memory-dump ($prefix+'.ram') --floppy-io-trace ($prefix+'.io.jsonl') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MS-DOS FORMAT guest failed: $prefix" }
    $result = Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if ($result.status.fault -or -not $result.scenario.complete -or $result.ataSectorsWritten -ne 0 -or -not $result.status.debugOutput.EndsWith('C:\>')) { throw "Incomplete MS-DOS FORMAT scenario: $prefix" }
    return $result
}
$format = Plan format @(
    @{waitForScreen='C:\>';text="kezb us`r`n"},
    @{waitForScreen='C:\>keyb us';text="format b: /u /v:M2TEST`r`n"},
    @{waitForScreen='Laufwerk B:';text="`r`n"},
    @{waitForScreen='weitere Diskette';text="n`r`necho M2-MSFORMAT-END`r`n"})
$result = Guest format $format '500000000'
if ($result.floppySectorsWritten -ne 2916 -or $result.floppySectorsRead -ne 2899 -or -not $result.status.debugOutput.Contains('Formatieren beendet') -or -not $result.status.debugOutput.Contains("`r`nM2-MSFORMAT-END`r`n")) { throw 'Original FORMAT.COM did not finish the complete disk.' }
Copy-Item -LiteralPath $cow -Destination (Join-Path $root 'formatted-empty.cow')
$check = python (Join-Path $workspaceRoot 'tests\verify-msdos-format.py') $base $cow (Join-Path $root 'format.io.jsonl') (Join-Path $root 'format.ram')
if ($LASTEXITCODE -ne 0) { throw 'Independent DOS FORMAT protocol/FAT/fill verification failed.' }
$check | Set-Content -LiteralPath (Join-Path $root 'format.verified.json') -Encoding utf8
$write = Plan write @(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="vol b:`r`necho M2-MSFORMAT-PERSISTED>b:\probe.txt`r`ntype b:\probe.txt`r`necho M2-MSFORMAT-WRITE-OK`r`n"})
$written = Guest write $write '50000000'
if ($written.floppySectorsWritten -eq 0 -or -not $written.status.debugOutput.Contains('M2TEST') -or -not $written.status.debugOutput.Contains("`r`nM2-MSFORMAT-PERSISTED`r`n") -or -not $written.status.debugOutput.Contains("`r`nM2-MSFORMAT-WRITE-OK`r`n")) { throw 'Freshly DOS-formatted filesystem is not writable.' }
$read = Plan reboot @(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="type b:\probe.txt`r`ndel b:\probe.txt`r`nif not exist b:\probe.txt echo M2-MSFORMAT-REBOOT-DELETE-OK`r`n"})
$rebooted = Guest reboot $read '50000000'
if ($rebooted.floppySectorsWritten -eq 0 -or -not $rebooted.status.debugOutput.Contains("`r`nM2-MSFORMAT-PERSISTED`r`n") -or -not $rebooted.status.debugOutput.Contains("`r`nM2-MSFORMAT-REBOOT-DELETE-OK`r`n")) { throw 'Formatted file did not survive a cold boot and deletion.' }
if ((Get-DosFileHash -LiteralPath $base).Hash -ne $baseHash -or (Get-DosFileHash -LiteralPath $privateHdd).Hash -ne $hddHash -or
    (Get-DosFileHash -LiteralPath $Disk).Hash -ne $sourceHashes[0] -or (Get-DosFileHash -LiteralPath $Overlay).Hash -ne $sourceHashes[1] -or (Get-DosFileHash -LiteralPath $source).Hash -ne $sourceHashes[2]) { throw 'Read-only original or private base changed.' }
$summary = [pscustomobject]@{passed=$true;directory=$root;coldBoots=3;originalFormatCom=$true;fdcFormatTracks=160;fdcDma2=$true;
    formattedSectorsWritten=$result.floppySectorsWritten;formattedSectorsRead=$result.floppySectorsRead;label='M2TEST';independentFatAndDataFillCheck=$true;
    postFormatDosWrites=$written.floppySectorsWritten;rebootReadsAndDeletion=$true;sourceImagesUnchanged=$true;hddUnchanged=$true}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
