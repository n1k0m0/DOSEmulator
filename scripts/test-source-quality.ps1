# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspaceRoot ('artifacts\source-quality\check-' + [guid]::NewGuid().ToString('N')) }
$output = Resolve-DosPath $OutputDirectory
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location -LiteralPath $workspaceRoot
try {
    dotnet build tests/DOSEmulator.SourceAudit -c Release -o (Join-Path $output 'audit-tool') --ignore-failed-sources -p:NuGetAudit=false -p:UseSharedCompilation=false -v:q *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'C# syntax/documentation audit build failed; see build.log.' }
    dotnet (Join-Path $output 'audit-tool\DOSEmulator.SourceAudit.dll') $workspaceRoot --check > (Join-Path $output 'csharp.json')
    if ($LASTEXITCODE -ne 0) { throw 'C# source quality check failed; see csharp.json.' }
    python tests/verify-source-quality.py > (Join-Path $output 'other-sources.json')
    if ($LASTEXITCODE -ne 0) { throw 'Source header/Python documentation check failed; see other-sources.json.' }
    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
        $tokens = $null; $errors = $null
        $tree = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ('PowerShell syntax error in ' + $file.Name) }
        $lines = [IO.File]::ReadAllLines($file.FullName)
        foreach ($function in $tree.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
            $count++
            $line = $function.Extent.StartLineNumber - 2
            while ($line -ge 0 -and -not $lines[$line].Trim()) { $line-- }
            if ($line -lt 0 -or -not $lines[$line].Trim().StartsWith('#')) { throw ($file.Name + ': function ' + $function.Name + ' lacks a purpose comment.') }
        }
    }
    [pscustomobject]@{ passed=$true; powerShellFunctions=$count; csharp=(Get-Content -LiteralPath (Join-Path $output 'csharp.json') -Raw | ConvertFrom-Json); otherSources=(Get-Content -LiteralPath (Join-Path $output 'other-sources.json') -Raw | ConvertFrom-Json) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json')
    Write-Output ('Source quality passed: ' + (Join-Path $output 'summary.json'))
} finally { Pop-Location }
