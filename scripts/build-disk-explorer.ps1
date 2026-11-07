# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputRoot = 'VMs/Tools/DiskExplorer', [switch]$RunTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputRoot, $root)
$build = Join-Path $output '_work/Build'
$app = Join-Path $output 'App'
$previousLocation = Get-Location
try {
    Set-Location -LiteralPath $root
    if ($RunTests) {
        dotnet build tests/DOSEmulator.DiskTools.Tests/DOSEmulator.DiskTools.Tests.csproj -c Release --artifacts-path $build -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -nr:false -v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'Disk tools test build failed.' }
        dotnet (Join-Path $build 'bin/DOSEmulator.DiskTools.Tests/release/DOSEmulator.DiskTools.Tests.dll')
        if ($LASTEXITCODE -ne 0) { throw 'Disk tools storage tests failed.' }
    }
    foreach ($project in @('DOSEmulator.DiskExplorer', 'DOSEmulator.DiskTool')) {
        dotnet publish (Join-Path $root ('src/' + $project + '/' + $project + '.csproj')) -c Release --no-self-contained --artifacts-path $build -o $app -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -nr:false -v:minimal
        if ($LASTEXITCODE -ne 0) { throw ('Disk tools publish failed: ' + $project) }
    }
    $licenses = Join-Path $app 'LICENSES'
    [IO.Directory]::CreateDirectory($licenses) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'LICENSES/Apache-2.0.txt') -Destination (Join-Path $licenses 'DOSE-Apache-2.0.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'LICENSES/LicenseRef-DiscUtils-MIT.txt') -Destination (Join-Path $licenses 'DiscUtils-MIT.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'LICENSES/LicenseRef-LTRData-Extensions-MIT.txt') -Destination (Join-Path $licenses 'LTRData-Extensions-MIT.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs/DISK-EXPLORER.md') -Destination (Join-Path $output 'README.md') -Force
    $launcher = @'
@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal DisableDelayedExpansion
"%~dp0App\DOSEmulator.DiskExplorer.exe" --vm-root "%~dp0..\.." %*
exit /b %errorlevel%
'@
    [IO.File]::WriteAllText((Join-Path $output 'Start.cmd'), $launcher)
    if ($output -eq [IO.Path]::GetFullPath('VMs/Tools/DiskExplorer', $root)) {
        $shortcut = @'
@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal DisableDelayedExpansion
call "%~dp0Tools\DiskExplorer\Start.cmd" %*
exit /b %errorlevel%
'@
        [IO.File]::WriteAllText((Join-Path $root 'VMs/Open-Disks.cmd'), $shortcut)
    }
    $files = @(Get-ChildItem -LiteralPath $app -File -Recurse | ForEach-Object {
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($app, $_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    [pscustomobject]@{product='DOSE Disk Explorer';framework='net10.0-windows';sourceCommit=(git rev-parse HEAD);files=$files} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding utf8
    Write-Output ('Disk Explorer ready: ' + (Join-Path $output 'Start.cmd'))
} finally { Set-Location -LiteralPath $previousLocation }
