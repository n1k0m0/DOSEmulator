# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[switch]$Gui,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $OutputRoot}
$testRoot=Join-Path $workspaceRoot ('artifacts\sb2-resources-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$results=@()
foreach($basePort in @(0x220,0x240)){foreach($irq in @(3,5,7,9)){
    $native=[bool]$Gui -and $basePort -eq 0x240 -and $irq -eq 9
    $text=(& (Join-Path $PSScriptRoot 'test-sb2-input.ps1') -SkipBuild -OutputRoot $OutputRoot -Disk $Disk -Overlay $Overlay -BasePort $basePort -Irq $irq -ResourceProbe -Gui:$native) -join "`n"
    $result=$text | ConvertFrom-Json
    if(-not $result.passed -or $result.headlessColdBoots -ne 8 -or -not $result.fmAliasTested){throw 'SB2 resource matrix incomplete'}
    $results+=$result
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot ("a{0:X3}-i{1}.json" -f $basePort,$irq)) -Encoding utf8
}}
python (Join-Path $workspaceRoot 'tests\verify-sb2-resources.py') $testRoot > (Join-Path $testRoot 'resources-independently-verified.json')
if($LASTEXITCODE -ne 0){throw 'Independent FM access, DSP vector or PIC restoration check failed'}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;resourcePairs=8;headlessColdBoots=64;nativeProcesses=$(if($Gui){2}else{0});
    nativeCaptures=$(if($Gui){10}else{0});dmaChannel=1;separatePicVectorsTested=$true;fmAliasesTested=$true;
    hostMicrophoneOpened=$false;results=$results;overallM2Complete=$false}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 10
