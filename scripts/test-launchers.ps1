# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $script:DosWorkspaceRoot ('artifacts/launcher-tests/' + [guid]::NewGuid().ToString('N')) }
$output = Resolve-DosPath $OutputDirectory
$fixture = Join-Path $output 'own workspace with spaces [test]'
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
$launchers = @(Get-ChildItem -LiteralPath $script:DosWorkspaceRoot -Filter 'run*.cmd' -File)
foreach ($file in $launchers) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $fixture $file.Name) }
foreach ($name in @('common.ps1','runtime.ps1','start-variant.ps1','run.ps1','run-doom2.ps1','run-freedos.ps1','run-freedos-floppy.ps1','invoke-powershell.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture ('scripts/' + $name))
}
$release = Join-Path $fixture 'artifacts/current/versions/own-test/Release'
$modules = [ordered]@{}
foreach ($name in @('DOSEmulator.Contracts','DOSEmulator.Core','DOSEmulator.Cpu.X86','DOSEmulator.Hardware','DOSEmulator.Runtime')) {
    foreach ($framework in @('net10.0','net10.0-windows')) {
        $path = Join-Path $release ($framework + '/' + $name + '.dll')
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'Own non-executable launcher inspection fixture: ' + $name)
        $modules[$name] = (Get-DosFileHash -LiteralPath $path).Hash
    }
}
[IO.File]::WriteAllText((Join-Path $release 'net10.0-windows/DOSEmulator.exe'), 'This own inspection fixture must never execute.')
[ordered]@{schemaVersion=1;version='own-test';modules=$modules} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'artifacts/current/current.json')
foreach ($relative in @('Windows95-Drivers-C47r2-20261031/win95.dosebranch','Windows98/Windows98-SE.dosebranch','Windows95-Drivers-C47r2-20261031/win95-apm.dosebranch',
    'Windows-Realtime-C44b/Windows98.dosebranch','Windows95-Drivers-C47r2-20261031/win95-load.dosebranch','Windows-Load-C45/Windows98.dosebranch',
    'Windows95-Drivers-C47r2-20261031/win95-media.dosebranch','Games-Media-C46/Windows98.dosebranch')) {
    $directory = Join-Path $fixture ('artifacts/' + $relative)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'branch.dospc.json'), '{}')
}
[IO.File]::WriteAllText((Join-Path $fixture 'artifacts/Games-Media-C46/doom133.dospc.json'), '{}')
$results = [Collections.Generic.List[object]]::new()
foreach ($file in $launchers) {
    $info = [Diagnostics.ProcessStartInfo]::new($env:ComSpec)
    $info.Arguments = '/d /s /c ""' + (Join-Path $fixture $file.Name) + '" -Inspect"'
    $info.WorkingDirectory = $output; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw ('Own launcher inspection timed out: ' + $file.Name) }
        $text = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output ($file.Name + '.stdout.json')), $text)
        [IO.File]::WriteAllText((Join-Path $output ($file.Name + '.stderr.txt')), $errorText)
        if ($process.ExitCode -ne 0) { throw ('Launcher inspection failed: ' + $file.Name + ' / ' + $errorText) }
        $row = $text | ConvertFrom-Json
        if ($row.runtime -cne $release -or $row.nativeWindowStarted -ne $false -or $row.mediaOpened -ne $false) { throw ('Launcher did not use the common runtime safely: ' + $file.Name) }
        $results.Add([pscustomobject]@{launcher=$file.Name;runtime=$row.runtime;passed=$true;mediaOpened=$false;nativeWindowStarted=$false})
    } finally { $process.Dispose() }
}
[pscustomobject]@{passed=$true;launchers=$results;count=$results.Count;onlyOwnWorkspace=$true;actualUserMediaOpened=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json')
Write-Output ('All ' + $results.Count + ' root launchers resolve the same runtime without opening media or a window.')
