# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([string]$Version = '', [string]$OutputRoot = 'artifacts/release', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Version) { $Version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numeric three-part release version.' }
$candidates = @()
if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe' }
if ($env:LOCALAPPDATA) { $candidates += Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe' }
$iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$iscc) { throw 'Inno Setup 6 is required.' }
$releaseRoot = [IO.Path]::GetFullPath($OutputRoot, $root)
$privateRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (!$releaseRoot.StartsWith($privateRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output must stay inside private artifacts.' }
$publish = Join-Path $releaseRoot 'publish'
$output = Join-Path $releaseRoot 'installer'
if (!$SkipPublish) {
    if (Test-Path -LiteralPath $publish) { throw 'Publish output already exists; use a clean release workspace.' }
    dotnet publish (Join-Path $root 'src/DOSEmulator/DOSEmulator.csproj') -c Release -r win-x64 --self-contained true -o $publish --artifacts-path (Join-Path $releaseRoot 'build') -p:Version=$Version -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -nr:false -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'DOSE publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSES/Apache-2.0.txt') -Destination (Join-Path $publish 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $publish
    [IO.File]::WriteAllText((Join-Path $publish 'dose-installed.txt'), 'DOSE installer release ' + $Version + [Environment]::NewLine)
}
$network = Join-Path $root 'artifacts/release/network-runtime'
if (!(Test-Path -LiteralPath (Join-Path $network 'network-runtime.json'))) {
    python (Join-Path $root 'scripts/prepare-network-runtime.py') --output $network --download
    if ($LASTEXITCODE -ne 0) { throw 'Pinned native network runtime preparation failed.' }
}
$nativeTarget = Join-Path $publish 'native/win-x64'
if (!(Test-Path -LiteralPath (Join-Path $nativeTarget 'network-runtime.json'))) {
    [IO.Directory]::CreateDirectory($nativeTarget) | Out-Null
    Copy-Item -Path (Join-Path $network '*') -Destination $nativeTarget -Recurse
}
$exe = Join-Path $publish 'DOSEmulator.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Published GUI executable missing.' }
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ($actual -ne $Version -and !$actual.StartsWith($Version + '+', [StringComparison]::Ordinal)) { throw "Published version $actual does not match $Version." }
[IO.Directory]::CreateDirectory($output) | Out-Null
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$output" (Join-Path $root 'installer/DOSEmulator.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output "DOSEmulator-$Version-win-x64-setup.exe"
if (!(Test-Path -LiteralPath $setup)) { throw 'Setup file missing.' }
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Select-Object Path,Hash
