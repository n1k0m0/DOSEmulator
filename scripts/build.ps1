# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$OutputRoot = 'artifacts\m1-build')
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
$locationPushed = $false
try {
    $env:MSBuildEnableWorkloadResolver = 'false'
    Push-Location -LiteralPath $workspaceRoot
    $locationPushed = $true
    & (Join-Path $PSScriptRoot 'build-firmware.ps1')
    $buildArguments = @('build','DOSEmulator.slnx','-c',$Configuration,'--ignore-failed-sources','-p:NuGetAudit=false','-p:UseSharedCompilation=false','-m:1','-nr:false','-v:minimal')
    if ($OutputRoot) { $buildArguments += ('-p:BaseOutputPath=' + (Get-DosBuildRoot -OutputRoot $OutputRoot).TrimEnd('\') + '\') }
    dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
} finally {
    if ($locationPushed) { Pop-Location }
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
}
