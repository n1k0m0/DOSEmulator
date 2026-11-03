# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$PreviousRoot = (Join-Path $env:USERPROFILE 'Desktop/Anwendungsentwicklung mit KI/DOSEmulator'),
    [switch]$Inspect
)
. (Join-Path $PSScriptRoot 'common.ps1')
$currentRoot = [IO.Path]::GetFullPath($script:DosWorkspaceRoot).TrimEnd('\', '/')
$previous = [IO.Path]::GetFullPath($PreviousRoot).TrimEnd('\', '/')
if (-not $IsWindows) { throw 'Workspace junction compatibility is supported on Windows only.' }
if ($previous -ieq $currentRoot -or $currentRoot.StartsWith($previous + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $previous.StartsWith($currentRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Old and current workspace paths must be distinct, non-nested directories.'
}
if (-not (Test-Path -LiteralPath (Join-Path $currentRoot 'DOSEmulator.slnx') -PathType Leaf)) {
    throw 'The current project root does not contain DOSEmulator.slnx.'
}
$existing = Get-Item -LiteralPath $previous -Force -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.LinkType -ne 'Junction' -or [IO.Path]::GetFullPath([string]$existing.Target).TrimEnd('\', '/') -ine $currentRoot) {
        throw 'The previous location is occupied; no directory, link or file will be replaced.'
    }
} elseif (-not $Inspect) {
    $parent = Split-Path -Parent $previous
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    New-Item -ItemType Junction -Path $previous -Target $currentRoot | Out-Null
    $existing = Get-Item -LiteralPath $previous -Force
    if ($existing.LinkType -ne 'Junction' -or [IO.Path]::GetFullPath([string]$existing.Target).TrimEnd('\', '/') -ine $currentRoot) {
        throw 'The compatibility junction could not be verified.'
    }
}
[pscustomobject]@{
    previousRoot = $previous
    currentRoot = $currentRoot
    compatible = $null -ne $existing
    inspectOnly = [bool]$Inspect
    machineDefinitionsChanged = $false
    mediaOpened = $false
} | ConvertTo-Json
