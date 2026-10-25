#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$Runner)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ($Runner) { $Runner = (Resolve-Path -LiteralPath $Runner).Path }
$outputRoot = Join-Path $workspaceRoot 'artifacts\m1-build'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $outputRoot }
if (-not $Runner) { $Runner = Join-Path $outputRoot 'Release\net10.0\DOSEmulator.Headless.dll' }
& (Join-Path $PSScriptRoot 'prepare-freedos-floppy.ps1') -Runner $Runner | Out-Host
$image = Join-Path $workspaceRoot 'artifacts\freedos-m2\floppy\FD14-minimal.img'
$original = Join-Path $workspaceRoot 'artifacts\freedos-floppy-original\FD14BOOT144.img'
$baseHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$originalHash = (Get-DosFileHash -LiteralPath $original -Algorithm SHA256).Hash
$directory = Join-Path $workspaceRoot ('artifacts\freedos-m2\floppy-acceptance-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$overlayA = Join-Path $directory 'a.cow'
$overlayB = Join-Path $directory 'b.cow'
$results = @()
function Run-Guest([string]$Name,[string[]]$Commands,[string[]]$Required,[string[]]$MediaArguments) {
    $inputPath = Join-Path $directory ($Name + '.txt')
    $report = Join-Path $directory ($Name + '.json')
    [IO.File]::WriteAllText($inputPath,($Commands -join "`r`n") + "`r`n")
    dotnet $Runner --floppy-a $image @MediaArguments --steps 100000000 --type-file $inputPath --type-after 'A:\>' --report $report --frame (Join-Path $directory ($Name + '.ppm')) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Floppy guest run failed: $report" }
    $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    if ($result.status.fault -or $result.inputBytesSent -ne $result.inputBytesTotal -or $result.floppySectorsRead -eq 0 -or $result.ataSectorsRead -ne 0) { throw "Invalid FDC boot evidence: $report" }
    foreach ($text in $Required) { if (-not $result.status.debugOutput.Contains("`r`n$text`r`n")) { throw "Missing actual guest output '$text': $report" } }
    if ($result.status.debugOutput.Contains("`r`nM2-ERROR`r`n") -or -not $result.status.debugOutput.EndsWith('A:\>')) { throw "Guest did not finish cleanly: $report" }
    return $result
}
$commands = @('ver','dir a:\','type a:\fdconfig.sys','echo M2-FLOPPY-INTERACTIVE-OK')
foreach ($number in 1..2) {
    $result = Run-Guest "cold-boot-$number" $commands @('M2-FLOPPY-BOOT-OK','SHELL=A:\FREEDOS\BIN\COMMAND.COM A:\FREEDOS\BIN /E:512 /P=A:\FDAUTO.BAT','M2-FLOPPY-INTERACTIVE-OK') @()
    $results += [pscustomobject]@{name="cold-boot-$number";read=$result.floppySectorsRead;write=$result.floppySectorsWritten;frameHash=$result.frameHash}
}
if ($results[0].frameHash -ne $results[1].frameHash) { throw 'Independent floppy cold boots differ.' }
$writable = @('--floppy-a-overlay',$overlayA,'--floppy-b',$image,'--floppy-b-overlay',$overlayB)
$commands = @('echo M2-FLOPPY-PERSISTED>a:\probe.txt','copy a:\probe.txt b:\probe.txt','md b:\m2dir','copy a:\probe.txt b:\m2dir\copy.txt','echo M2-SWAP-CONTENT>b:\swap.txt','type a:\probe.txt','type b:\probe.txt','type b:\m2dir\copy.txt','echo M2-FLOPPY-WRITE-OK')
$result = Run-Guest 'create-both-drives' $commands @('M2-FLOPPY-PERSISTED','M2-FLOPPY-WRITE-OK') $writable
if ($result.floppySectorsWritten -eq 0 -or [regex]::Matches($result.status.debugOutput,"`r`nM2-FLOPPY-PERSISTED`r`n").Count -ne 3) { throw 'Two-drive file creation did not complete.' }
$results += [pscustomobject]@{name='create-both-drives';read=$result.floppySectorsRead;write=$result.floppySectorsWritten}
$commands = @('type a:\probe.txt','type b:\probe.txt','type b:\m2dir\copy.txt','ren b:\m2dir\copy.txt kept.txt','type b:\m2dir\kept.txt','del b:\m2dir\kept.txt','rd b:\m2dir','del a:\probe.txt','del b:\probe.txt','echo M2-FLOPPY-REBOOT-DELETE-OK')
$result = Run-Guest 'reboot-read-rename-delete' $commands @('M2-FLOPPY-REBOOT-DELETE-OK') $writable
if ([regex]::Matches($result.status.debugOutput,"`r`nM2-FLOPPY-PERSISTED`r`n").Count -ne 4 -or $result.floppySectorsWritten -eq 0) { throw 'Rebooted two-drive contents did not match.' }
$results += [pscustomobject]@{name='reboot-read-rename-delete';read=$result.floppySectorsRead;write=$result.floppySectorsWritten}
$commands = @('if not exist a:\probe.txt echo M2-A-DELETED','if not exist b:\probe.txt echo M2-B-DELETED','if not exist b:\m2dir echo M2-DIR-DELETED','if exist a:\probe.txt echo M2-ERROR','if exist b:\probe.txt echo M2-ERROR','if exist b:\m2dir echo M2-ERROR','echo M2-FLOPPY-PERSISTENCE-OK')
$result = Run-Guest 'reboot-check-deleted' $commands @('M2-A-DELETED','M2-B-DELETED','M2-DIR-DELETED','M2-FLOPPY-PERSISTENCE-OK') $writable
$results += [pscustomobject]@{name='reboot-check-deleted';read=$result.floppySectorsRead;write=$result.floppySectorsWritten}
$commands = @('if not exist b:\swap.txt echo M2-SWAP-EMPTY','echo M2-SWAP-NOW','type b:\swap.txt','echo M2-SWAP-OK')
$swap = @('--floppy-b',$image,'--swap-floppy','b','--swap-image',$image,'--swap-overlay',$overlayB,'--swap-after',"`r`nM2-SWAP-NOW`r`n")
$result = Run-Guest 'live-media-change' $commands @('M2-SWAP-EMPTY','M2-SWAP-CONTENT','M2-SWAP-OK') $swap
if (-not $result.mediaSwapApplied) { throw 'Live floppy change did not run.' }
$results += [pscustomobject]@{name='live-media-change';read=$result.floppySectorsRead;write=$result.floppySectorsWritten}
if ((Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $baseHash -or (Get-DosFileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $originalHash) { throw 'Floppy base/original changed.' }
$summary = [pscustomobject]@{passed=$true;coldBoots=6;twoDrives=$true;fdcDmaPath=$true;persistentWrites=$true;liveMediaChange=$true;originalUnchanged=$true;baseUnchanged=$true;directory=$directory;stages=$results}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
