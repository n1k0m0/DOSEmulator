#requires -Version 7.4
# Dot-sourced in each project script: preferences stay in that script's scope.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$script:DosWorkspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-DosPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [string]$BaseDirectory = (Get-Location).ProviderPath)
    if (-not $BaseDirectory) { throw 'Run this script from a filesystem directory.' }
    return [IO.Path]::GetFullPath($Path, $BaseDirectory)
}

function Get-DosBuildRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputRoot)
    return Resolve-DosPath -Path $OutputRoot -BaseDirectory $script:DosWorkspaceRoot
}

function Get-DosFileHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)][object[]]$LiteralPath,
        [ValidateSet('SHA256')][string]$Algorithm = 'SHA256'
    )
    process {
        foreach ($item in $LiteralPath) {
            $path = if ($item -is [IO.FileInfo]) { $item.FullName } elseif ($item -is [string]) { $item } else {
                throw 'Get-DosFileHash expects literal file paths or FileInfo objects.'
            }
            $fullPath = Resolve-DosPath -Path $path
            $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            [pscustomobject]@{ Algorithm = $Algorithm; Hash = $hash; Path = $fullPath }
        }
    }
}
