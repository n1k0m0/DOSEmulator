# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$Runner)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ($Runner) { $Runner = (Resolve-Path -LiteralPath $Runner).Path }
$outputRoot = Join-Path $workspaceRoot 'artifacts\m1-build'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $outputRoot }
if (-not $Runner) { $Runner = Join-Path $outputRoot 'Release\net10.0\DOSEmulator.Headless.dll' }
& (Join-Path $PSScriptRoot 'prepare-freedos.ps1') -Runner $Runner | Out-Host
$testDirectory = Join-Path $workspaceRoot ('artifacts\freedos-m2\writes-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$image = Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$original = Join-Path $workspaceRoot 'artifacts\freedos-original\FD14LITE.img'
$overlay = Join-Path $testDirectory 'session.cow'
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$originalHash = (Get-DosFileHash -LiteralPath $original -Algorithm SHA256).Hash
$stages = @(
    [pscustomobject]@{
        name='create'
        commands=@('echo M2-PERSISTED-CONTENT>c:\m2probe.txt','md c:\m2test','copy c:\m2probe.txt c:\m2test\copy.txt','type c:\m2test\copy.txt','echo M2-WRITE-OK')
        required=@('M2-PERSISTED-CONTENT','M2-WRITE-OK')
        contentCount=1
        writes=$true
    },
    [pscustomobject]@{
        name='reboot-read-delete'
        commands=@('type c:\m2probe.txt','type c:\m2test\copy.txt','ren c:\m2test\copy.txt moved.txt','type c:\m2test\moved.txt','echo M2-REBOOT-READ-OK','del c:\m2test\moved.txt','rd c:\m2test','del c:\m2probe.txt','echo M2-DELETE-OK')
        required=@('M2-PERSISTED-CONTENT','M2-REBOOT-READ-OK','M2-DELETE-OK')
        contentCount=3
        writes=$true
    },
    [pscustomobject]@{
        name='reboot-check-deleted'
        commands=@('if not exist c:\m2probe.txt echo M2-ROOT-DELETED','if not exist c:\m2test echo M2-DIR-DELETED','if exist c:\m2probe.txt echo M2-ERROR','if exist c:\m2test echo M2-ERROR','echo M2-PERSISTENCE-OK')
        required=@('M2-ROOT-DELETED','M2-DIR-DELETED','M2-PERSISTENCE-OK')
        contentCount=0
        writes=$false
    }
)
$results = @()
foreach ($stage in $stages) {
    $commands = Join-Path $testDirectory ($stage.name + '.txt')
    [IO.File]::WriteAllText($commands, ($stage.commands -join "`r`n") + "`r`n")
    $report = Join-Path $testDirectory ($stage.name + '.json')
    dotnet $Runner --disk $image --overlay $overlay --steps 60000000 --type-file $commands --report $report | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FreeDOS overlay stage $($stage.name) failed. See $report" }
    $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    if ($result.status.fault -or $result.inputBytesSent -ne $result.inputBytesTotal) { throw "Incomplete guest execution: $report" }
    foreach ($required in $stage.required) {
        if (-not $result.status.debugOutput.Contains("`r`n$required`r`n")) { throw "Missing actual guest output '$required': $report" }
    }
    if ($stage.writes -and $result.ataSectorsWritten -eq 0) { throw "Guest did not write ATA sectors: $report" }
    if ([regex]::Matches($result.status.debugOutput,"`r`nM2-PERSISTED-CONTENT`r`n").Count -ne $stage.contentCount) { throw "Guest file contents not verified at every step: $report" }
    if ($result.status.debugOutput.Contains("`r`nM2-ERROR`r`n")) { throw "Deleted guest file survived: $report" }
    $results += [pscustomobject]@{name=$stage.name;passed=$true;sectorsRead=$result.ataSectorsRead;sectorsWritten=$result.ataSectorsWritten;inputBytes=$result.inputBytesSent}
}
if ((Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash -or (Get-DosFileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $originalHash) { throw 'Base media changed during write acceptance.' }
$summary = [pscustomobject]@{passed=$true;coldBoots=3;createReadCopyRenameDelete=$true;originalUnchanged=$true;baseUnchanged=$true;overlay=$overlay;stages=$results}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testDirectory 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
