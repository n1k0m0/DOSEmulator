# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build')
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$work=Join-Path $workspaceRoot ('artifacts\m4-selector\guest-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$program=Join-Path $work 'SELECTOR.BIN'
& (Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $workspaceRoot 'tests\guests\p54c-selector-test.asm') -o $program
if($LASTEXITCODE -ne 0){throw 'Own selector guest assembly failed.'}
$assemblies=@{}
foreach($name in @('Contracts','Core','Cpu.X86','Hardware','Headless','Runtime','Tests')){
    $path=Join-Path $buildRoot "Release\net10.0\DOSEmulator.$name.dll";$assemblies[$path]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$results=@()
foreach($model in @('p54c','486')){
    $previous=$null
    foreach($repeat in @(1,2)){
        $evidence=Join-Path $work "$model-$repeat";$inputPath=Join-Path $work "$model-$repeat.json"
        @{program=$program;output=$evidence;model=$model} | ConvertTo-Json | Set-Content -LiteralPath $inputPath -Encoding utf8
        dotnet (Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Tests.dll') --selector-guest $inputPath
        if($LASTEXITCODE -ne 0){throw 'Own selector gate continuation failed.'}
        python (Join-Path $workspaceRoot 'tests\verify-selector.py') --program $program --evidence $evidence
        if($LASTEXITCODE -ne 0){throw 'Independent gate/frame/stack oracle failed.'}
        $result=Get-Content -LiteralPath (Join-Path $evidence 'result.json') -Raw | ConvertFrom-Json
        $oracle=Get-Content -LiteralPath (Join-Path $evidence 'oracle.json') -Raw | ConvertFrom-Json
        if($null -ne $previous){foreach($field in @('ramSha256','stateSha256','futureSha256','finalTime')){if($result.$field -ne $previous.$field){throw "Repeated selector guest differs: $field"}}}
        $previous=$result;$results+=@{evidence=$evidence;result=$result;oracle=$oracle}
    }
}
foreach($path in $assemblies.Keys){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $assemblies[$path]){throw 'A tested assembly changed.'}}
@{passed=$true;resetStarts=4;selectedGraphContinuations=4;fullPcSnapshot=$false;assemblies=$assemblies;runs=$results} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $work 'acceptance.json') -Encoding utf8
Write-Output $work
