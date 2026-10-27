# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk, [Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk, [string]$NoEmsOverlay, [string]$RamOverlay,
    [ValidateRange(1,2)][int]$Repeats=2)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk = Resolve-DosPath $Disk; $Overlay = Resolve-DosPath $Overlay
$systems = @(
    [pscustomobject]@{ name='msdos'; disk=$Disk; overlay=$Overlay },
    [pscustomobject]@{ name='freedos'; disk=(Resolve-DosPath 'artifacts/freedos-m1/FD14LITE-minimal.img'); overlay=$null })
$configured = @($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if ($configured.Count -notin 0,3) { throw 'Supply all three EMM386 paths together.' }
if ($configured.Count -eq 3) {
    $systems += @(
        [pscustomobject]@{ name='emm386-noems'; disk=(Resolve-DosPath $ConfiguredDisk); overlay=(Resolve-DosPath $NoEmsOverlay) },
        [pscustomobject]@{ name='emm386-ram'; disk=(Resolve-DosPath $ConfiguredDisk); overlay=(Resolve-DosPath $RamOverlay) })
}
$inputs = @($systems | ForEach-Object { $_.disk; if ($_.overlay) { $_.overlay } }) | Select-Object -Unique
foreach ($inputPath in $inputs) { if ($inputPath -match '(?i)doom2-play') { throw 'Personal play media are excluded.' } }
$originals = @($inputs | Get-DosFileHash)
$root = Join-Path $script:DosWorkspaceRoot ('artifacts\ata-boundary\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner = Join-Path $buildRoot 'Release/net10.0/DOSEmulator.Headless.dll'
$assemblies = @(Get-ChildItem -LiteralPath (Split-Path $runner -Parent) -Filter '*.dll' | Get-DosFileHash)
$assemblies | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$probe = Join-Path $root 'ATABND.COM'; $floppy = Join-Path $root 'own-probe.img'; $scratch = Join-Path $root 'scratch.img'
& (Resolve-DosPath 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe') -Werror -f bin (Resolve-DosPath 'tests/guests/ata-boundary-test.asm') -o $probe
if ($LASTEXITCODE -ne 0) { throw 'Own boundary probe assembly failed.' }
python (Resolve-DosPath 'tests/prepare-font-media.py') $probe $floppy ATABND.COM > (Join-Path $root 'own-probe.json')
if ($LASTEXITCODE -ne 0) { throw 'Own probe media failed.' }
dotnet $runner --create-disk $scratch --size 2 --unit MiB > (Join-Path $root 'scratch-created.json')
if ($LASTEXITCODE -ne 0) { throw 'Private scratch creation failed.' }
$rows = @()
foreach ($system in $systems) { foreach ($repeat in 1..$Repeats) {
    $prefix = Join-Path $root ($system.name+'-'+$repeat); $cow = $prefix+'.cow'
    if ($system.overlay) { Copy-Item -LiteralPath $system.overlay -Destination $cow }
    $inputCowHash = if ($system.overlay) { (Get-DosFileHash -LiteralPath $cow).Hash } else { $null }
    $plan = @()
    if ($system.name -ne 'freedos') {
        $plan += @{ waitForScreen='C:\>'; text="kezb us`r`n" }
        $prompt = 'C:\>keyb us'
    } else { $prompt = 'C:\>' }
    $plan += @{ waitForScreen=$prompt; text="a:atabnd`r`necho ATA-BOUNDARY-DOS-RETURN`r`n" }
    ConvertTo-Json -InputObject $plan -Depth 5 | Set-Content -LiteralPath ($prefix+'.scenario.json') -Encoding utf8
    $stop = if ($system.name -eq 'freedos') { "`r`nATA-BOUNDARY-DOS-RETURN`r`nC:\>" } else { "`r`nATA-BOUNDARY-DOS-RETURN`r`n`r`nC:\>" }
    $guestArguments = @($runner,'--disk',$system.disk,'--overlay',$cow,'--disk1',$scratch,'--overlay1',($prefix+'-data.cow'),
        '--floppy-a',$floppy,'--scenario',($prefix+'.scenario.json'),'--steps','150000000',
        '--stop-text',$stop,'--report',($prefix+'.json'),
        '--memory-dump',($prefix+'.ram.bin'),'--ide-io-trace',($prefix+'.io.jsonl'))
    dotnet @guestArguments > ($prefix+'.stdout.json')
    if ($LASTEXITCODE -ne 0) { throw ('Guest failed: '+$system.name) }
    $oracle = @('--report',($prefix+'.json'),'--memory',($prefix+'.ram.bin'),'--io',($prefix+'.io.jsonl'),'--program',$probe)
    if ($system.name -like 'emm386-*') { $oracle += '--manager' }
    if ($repeat -eq 1) { $oracle += '--negative-controls' }
    python (Resolve-DosPath 'tests/verify-ata-boundary.py') @oracle > ($prefix+'.verified.json')
    if ($LASTEXITCODE -ne 0) { throw ('Independent boundary oracle failed: '+$system.name) }
    if ($inputCowHash -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $inputCowHash) { throw 'System journal changed.' }
    $rows += [pscustomobject]@{ system=$system.name; repeat=$repeat; report=$prefix+'.json';
        verification=(Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json) }
} }
foreach ($entry in @($originals)+@($assemblies)) {
    if ((Get-DosFileHash -LiteralPath $entry.Path).Hash -ne $entry.Hash) { throw ('Input changed: '+$entry.Path) }
}
$summary = [pscustomobject]@{ passed=$true; directory=$root; headlessColdBoots=$rows.Count; cases=$rows;
    privateScratchOnly=$true; originalsUnchanged=$true; nativeInputSent=$false }
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 10
