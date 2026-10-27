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
$testDirectory = Join-Path $workspaceRoot 'artifacts\freedos-m1'
$image = Join-Path $testDirectory 'FD14LITE-minimal.img'
$commands = Join-Path $testDirectory 'acceptance-commands.txt'
[IO.File]::WriteAllText($commands,"ver`r`ndir`r`ntype fdconfig.sys`r`necho M1-INTERACTIVE-OK`r`n")
$originalHash = (Get-DosFileHash -LiteralPath (Join-Path $workspaceRoot 'artifacts\freedos-original\FD14LITE.img') -Algorithm SHA256).Hash
$imageHash = (Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash
$results = @()
foreach ($number in 1..2) {
    $report = Join-Path $testDirectory "cold-boot-$number.json"
    $frame = Join-Path $testDirectory "cold-boot-$number.ppm"
    dotnet $runner --disk $image --steps 60000000 --type-file $commands --report $report --frame $frame | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FreeDOS cold boot $number failed." }
    $result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    foreach ($required in @('FreeDOS kernel 2043','FreeCom version 0.86','M1-FREEDOS-BOOT-OK','Directory of C:\','SHELL=C:\COMMAND.COM',"`r`nM1-INTERACTIVE-OK`r`nC:\>")) {
        if (-not $result.status.debugOutput.Contains($required)) { throw "Missing guest result in run $number`: $required" }
    }
    if ($result.status.fault -or $result.inputBytesSent -ne $result.inputBytesTotal) { throw "Incomplete guest execution in run $number." }
    $results += $result
}
if ($results[0].frameHash -ne $results[1].frameHash -or $results[0].status.virtualNanoseconds -ne $results[1].status.virtualNanoseconds) { throw 'Independent cold boots are not deterministic.' }
if ((Get-DosFileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $imageHash -or (Get-DosFileHash -LiteralPath (Join-Path $workspaceRoot 'artifacts\freedos-original\FD14LITE.img') -Algorithm SHA256).Hash -ne $originalHash) { throw 'FreeDOS media changed during acceptance.' }
$summary = [pscustomobject]@{passed=$true;coldBoots=2;interactive=$true;frameHash=$results[0].frameHash;virtualNanoseconds=$results[0].status.virtualNanoseconds;ataSectors=$results[0].ataSectorsRead;originalUnchanged=$true;derivedImageUnchanged=$true}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testDirectory 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json
