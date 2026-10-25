#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$captureDirectory = Join-Path $workspaceRoot ('artifacts\gui-' + [guid]::NewGuid().ToString('N'))
$testDataDirectory = Join-Path $captureDirectory 'settings'
New-Item -ItemType Directory -Path $testDataDirectory -Force | Out-Null
$emulatorExe = Join-Path $workspaceRoot 'artifacts\m1-build\Release\net10.0-windows\DOSEmulator.exe'
$testArguments = @('--data-dir', ('"{0}"' -f $testDataDirectory), '--smoke-test', ('"{0}"' -f $captureDirectory))
$testProcess = Start-Process -FilePath $emulatorExe -ArgumentList $testArguments -PassThru -WindowStyle Hidden
if (-not $testProcess.WaitForExit(30000)) { $testProcess.Kill(); throw 'GUI test did not finish within 30 seconds.' }
if ($testProcess.ExitCode -ne 0) { throw "GUI process failed ($($testProcess.ExitCode))." }
$result = Get-Content -Raw -LiteralPath (Join-Path $captureDirectory 'smoke-result.json') | ConvertFrom-Json
if ($result.status.fault -or $result.captures.Count -ne 6) { throw 'GUI smoke result failed.' }
foreach ($capture in $result.captures) { if (-not (Test-Path -LiteralPath $capture)) { throw "Missing capture: $capture" } }
Write-Output "GUI smoke passed. Inspect all six captures in $captureDirectory"
