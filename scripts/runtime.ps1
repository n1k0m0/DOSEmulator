# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
. (Join-Path $PSScriptRoot 'common.ps1')

# Resolves one atomically selected immutable runtime; active processes keep their previous version directory.
function Get-DosRuntimeRelease {
    [CmdletBinding()]
    param([switch]$BuildIfMissing)
    $currentRoot = Join-Path $script:DosWorkspaceRoot 'artifacts/current'
    $pointer = Join-Path $currentRoot 'current.json'
    if (Test-Path -LiteralPath $pointer) {
        $selection = Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json
        if ($selection.schemaVersion -ne 1 -or $selection.version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$') { throw 'Invalid current runtime selection.' }
        $release = [IO.Path]::GetFullPath((Join-Path $currentRoot ('versions/' + $selection.version + '/Release')))
        foreach ($name in @('DOSEmulator.Contracts','DOSEmulator.Core','DOSEmulator.Cpu.X86','DOSEmulator.Hardware','DOSEmulator.Runtime')) {
            foreach ($framework in @('net10.0','net10.0-windows')) {
                $path = Join-Path $release ($framework + '/' + $name + '.dll')
                $expected = $selection.modules.$name
                if ($expected -notmatch '^[0-9A-F]{64}$' -or (Get-DosFileHash -LiteralPath $path).Hash -cne $expected) { throw ('The current runtime is incomplete or changed: ' + $name) }
            }
        }
        if ($selection.modules.PSObject.Properties.Name -contains 'DOSEmulator.Whpx') {
            foreach ($framework in @('net10.0','net10.0-windows')) {
                $native = Join-Path $release ($framework + '/DOSEmulator.Whpx.dll')
                if ((Get-DosFileHash -LiteralPath $native).Hash -cne $selection.modules.'DOSEmulator.Whpx') { throw 'The selected WHPX backend changed or is incomplete.' }
            }
        }
        return $release
    }
    $fallback = Join-Path $script:DosWorkspaceRoot 'artifacts/m1-build/Release'
    if ($BuildIfMissing -and -not (Test-Path -LiteralPath (Join-Path $fallback 'net10.0-windows/DOSEmulator.exe'))) {
        & (Join-Path $PSScriptRoot 'build.ps1') | Out-Host
    }
    if (-not (Test-Path -LiteralPath (Join-Path $fallback 'net10.0-windows/DOSEmulator.exe'))) { throw 'No local runtime is prepared. Run scripts/build.ps1 first.' }
    return $fallback
}
