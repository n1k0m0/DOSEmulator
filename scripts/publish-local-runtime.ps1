# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$')][string]$Version, [string]$LauncherBindingsPath)
. (Join-Path $PSScriptRoot 'common.ps1')
$source = Resolve-DosPath -Path $SourceDirectory
$current = Join-Path $script:DosWorkspaceRoot 'artifacts/current'
$versions = Join-Path $current 'versions'
$target = [IO.Path]::GetFullPath((Join-Path $versions $Version))
if (-not $target.StartsWith(([IO.Path]::GetFullPath($versions) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime destination escaped its version directory.' }
if (Test-Path -LiteralPath $target) { throw 'An immutable runtime version already exists. Choose a new version name.' }
New-Item -ItemType Directory -Path (Join-Path $target 'Release') -Force | Out-Null
$products = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $destination = Join-Path (Join-Path $target 'Release') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    $hash = (Get-DosFileHash -LiteralPath $file.FullName).Hash
    if ((Get-DosFileHash -LiteralPath $destination).Hash -cne $hash) { throw ('Runtime copy failed: ' + $relative) }
    $products[$relative.Replace('\','/')] = $hash
}
$modules = [ordered]@{}
$moduleNames = @('DOSEmulator.Contracts','DOSEmulator.Core','DOSEmulator.Cpu.X86','DOSEmulator.Hardware','DOSEmulator.Runtime')
if ($products['net10.0/DOSEmulator.Whpx.dll'] -and $products['net10.0-windows/DOSEmulator.Whpx.dll']) { $moduleNames += 'DOSEmulator.Whpx' }
foreach ($name in $moduleNames) {
    $a = $products['net10.0/' + $name + '.dll']; $b = $products['net10.0-windows/' + $name + '.dll']
    if (-not $a -or $a -cne $b) { throw ('Frontend VM modules differ: ' + $name) }
    $modules[$name] = $a
}
$manifest = [ordered]@{ schemaVersion=1; version=$Version; modules=$modules; products=$products; selectedUtc=[DateTime]::UtcNow.ToString('o') }
if ($LauncherBindingsPath) {
    $bindings = Get-Content -LiteralPath (Resolve-DosPath -Path $LauncherBindingsPath) -Raw | ConvertFrom-Json
    if ($bindings.schemaVersion -ne 1 -or -not $bindings.machines) { throw 'Invalid launcher bindings.' }
    $manifest['launcherMachines'] = $bindings.machines
}
$text = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $target 'products.json'), $text)
$temporary = Join-Path $current ('current-' + [guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($temporary, $text)
[IO.File]::Move($temporary, (Join-Path $current 'current.json'), $true)
Write-Output ('Selected local runtime ' + $Version + ' / ' + $products.Count + ' verified products. Existing running processes retain their files.')
