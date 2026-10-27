# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build')
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$work = Join-Path $workspaceRoot ('artifacts\m4-p54c\guest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$program = Join-Path $work 'P54CORE.BIN'
& (Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $workspaceRoot 'tests\guests\p54c-core-test.asm') -o $program
if ($LASTEXITCODE -ne 0) { throw 'Own P54C reset guest assembly failed.' }
$inputPath = Join-Path $work 'input.json'
$evidence = Join-Path $work 'evidence'
@{program=$program;output=$evidence} | ConvertTo-Json | Set-Content -LiteralPath $inputPath -Encoding utf8
dotnet (Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Tests.dll') --p54c-core-guest $inputPath
if ($LASTEXITCODE -ne 0) { throw 'Own P54C CPU-only continuation failed.' }
python (Join-Path $workspaceRoot 'tests\verify-p54c-core.py') --program $program --evidence $evidence
if ($LASTEXITCODE -ne 0) { throw 'Independent P54C reset guest oracle failed.' }
Write-Output $work
