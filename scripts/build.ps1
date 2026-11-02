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
    $networkRuntime = Join-Path $workspaceRoot 'artifacts/network-runtime/win-x64'
    if (-not (Test-Path -LiteralPath (Join-Path $networkRuntime 'network-runtime.json'))) {
        python (Join-Path $PSScriptRoot 'prepare-network-runtime.py') --output $networkRuntime --download
        if ($LASTEXITCODE -ne 0) { throw 'Native IPv4 network runtime preparation failed.' }
    }
    & (Join-Path $PSScriptRoot 'build-firmware.ps1')
    python (Join-Path $PSScriptRoot 'build-cdrom-driver.py') --output (Join-Path $workspaceRoot 'artifacts/drivers/DOSECD.SYS')
    if ($LASTEXITCODE -ne 0) { throw 'DOS CD-ROM driver assembly failed.' }
    python (Join-Path $PSScriptRoot 'build-mmx-voodoo-test.py') --output (Join-Path $workspaceRoot 'artifacts/guests/mmx-voodoo-test.bin') --nasm (Join-Path $workspaceRoot 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Own MMX/SST-1 guest assembly failed.' }
    $timeToolsBuild = Join-Path $workspaceRoot ('artifacts/guest-tools/time-build-' + [Guid]::NewGuid().ToString('N'))
    python (Join-Path $PSScriptRoot 'build-dose-time.py') --output $timeToolsBuild --nasm (Join-Path $workspaceRoot 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Windows 95 time helper assembly failed.' }
    python (Join-Path $PSScriptRoot 'create-dose-time-media.py') --build $timeToolsBuild --output (Join-Path $timeToolsBuild 'dose-tools-time.img')
    if ($LASTEXITCODE -ne 0) { throw 'Windows 95 time tools data disk failed.' }
    python (Join-Path $PSScriptRoot 'create-dose-apm-media.py') --output (Join-Path $timeToolsBuild 'dose-tools-apm.img')
    if ($LASTEXITCODE -ne 0) { throw 'Windows APM tools data disk failed.' }
    $buildArguments = @('build','DOSEmulator.slnx','-c',$Configuration,'--ignore-failed-sources','-p:NuGetAudit=false','-p:UseSharedCompilation=false','-m:1','-nr:false','-v:minimal')
    if ($OutputRoot) { $buildArguments += ('-p:BaseOutputPath=' + (Get-DosBuildRoot -OutputRoot $OutputRoot).TrimEnd('\') + '\') }
    dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
    if ($OutputRoot) {
        $driverOutput = Join-Path (Get-DosBuildRoot -OutputRoot $OutputRoot) ($Configuration + '/drivers')
        New-Item -ItemType Directory -Path $driverOutput -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $workspaceRoot 'artifacts/drivers/DOSECD.SYS') -Destination (Join-Path $driverOutput 'DOSECD.SYS') -Force
        $hardwareDemoOutput = Join-Path (Get-DosBuildRoot -OutputRoot $OutputRoot) ($Configuration + '/guest-tools/hardware')
        New-Item -ItemType Directory -Path $hardwareDemoOutput -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $workspaceRoot 'artifacts/guests/mmx-voodoo-test.img') -Destination (Join-Path $hardwareDemoOutput 'mmx-voodoo-test.img') -Force
        $timeToolsOutput = Join-Path (Get-DosBuildRoot -OutputRoot $OutputRoot) ($Configuration + '/guest-tools/windows95')
        New-Item -ItemType Directory -Path $timeToolsOutput -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $timeToolsBuild 'DOSETIME.EXE') -Destination (Join-Path $timeToolsOutput 'SETUP.EXE') -Force
        Copy-Item -LiteralPath (Join-Path $timeToolsBuild 'dose-tools-time.img') -Destination (Join-Path $timeToolsOutput 'dose-tools-time.img') -Force
        Copy-Item -LiteralPath (Join-Path $workspaceRoot 'guest-tools/windows95/README.txt') -Destination (Join-Path $timeToolsOutput 'README.txt') -Force
        Copy-Item -LiteralPath (Join-Path $timeToolsBuild 'dose-tools-apm.img') -Destination (Join-Path $timeToolsOutput 'dose-tools-apm.img') -Force
        foreach ($apmFile in @('APM95.INF', 'APM98.INF', 'APM-README.txt')) {
            Copy-Item -LiteralPath (Join-Path $workspaceRoot ('guest-tools/windows95/' + $apmFile)) -Destination (Join-Path $timeToolsOutput $apmFile) -Force
        }
    }
} finally {
    if ($locationPushed) { Pop-Location }
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
}
