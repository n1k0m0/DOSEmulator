# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('win95','win98','win95-apm','win98-apm','win95-load','win98-load','win95-media','win98-media','doom2-media')][string]$Variant, [switch]$Inspect, [ValidateSet('whpx','software')][string]$Engine = 'whpx')
$engineOption = if ($PSBoundParameters.ContainsKey('Engine')) { '--engine' } else { '--default-engine' }
. (Join-Path $PSScriptRoot 'runtime.ps1')
$variants = @{
    'win95' = @('artifacts/Windows95-Drivers-C47r2-20261031/win95.dosebranch/branch.dospc.json','artifacts/Windows95/ui-100MHz-c18')
    'win98' = @('artifacts/Windows98/Windows98-SE.dosebranch/branch.dospc.json','artifacts/Windows98/ui')
    'win95-apm' = @('artifacts/Windows95-Drivers-C47r2-20261031/win95-apm.dosebranch/branch.dospc.json','artifacts/Windows-Realtime-C44b/UI-Windows95')
    'win98-apm' = @('artifacts/Windows-Realtime-C44b/Windows98.dosebranch/branch.dospc.json','artifacts/Windows-Realtime-C44b/UI-Windows98')
    'win95-load' = @('artifacts/Windows95-Drivers-C47r2-20261031/win95-load.dosebranch/branch.dospc.json','artifacts/Windows-Load-C45/UI-Windows95')
    'win98-load' = @('artifacts/Windows-Load-C45/Windows98.dosebranch/branch.dospc.json','artifacts/Windows-Load-C45/UI-Windows98')
    'win95-media' = @('artifacts/Windows95-Drivers-C47r2-20261031/win95-media.dosebranch/branch.dospc.json','artifacts/Games-Media-C46/UI-Windows95')
    'win98-media' = @('artifacts/Games-Media-C46/Windows98.dosebranch/branch.dospc.json','artifacts/Games-Media-C46/UI-Windows98')
    'doom2-media' = @('artifacts/Games-Media-C46/doom133.dospc.json','artifacts/Games-Media-C46/UI-doom133')
}
$release = Get-DosRuntimeRelease -BuildIfMissing
$exe = Join-Path $release 'net10.0-windows/DOSEmulator.exe'
$selectionFile = Join-Path $script:DosWorkspaceRoot 'artifacts/current/current.json'
if ($Engine -eq 'whpx' -and (Test-Path -LiteralPath $selectionFile)) {
    $selection = Get-Content -LiteralPath $selectionFile -Raw | ConvertFrom-Json
    if (($selection.PSObject.Properties.Name -contains 'launcherMachines') -and ($selection.launcherMachines.PSObject.Properties.Name -contains $Variant)) { $variants[$Variant][0] = $selection.launcherMachines.$Variant }
}
$machine = Join-Path $script:DosWorkspaceRoot $variants[$Variant][0]
$data = Join-Path $script:DosWorkspaceRoot $variants[$Variant][1]
if (-not (Test-Path -LiteralPath $machine)) { throw ('The private PC configuration is missing: ' + $Variant) }
if ($Inspect) { [pscustomobject]@{variant=$Variant;runtime=$release;engine=$Engine;executable=$exe;machine=$machine;settings=$data;nativeWindowStarted=$false;mediaOpened=$false} | ConvertTo-Json; return }
& $exe $engineOption $Engine --machine $machine --data-dir $data
exit $LASTEXITCODE
