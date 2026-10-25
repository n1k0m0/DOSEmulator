#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$WriteOverlay, [switch]$Floppy)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
$outputRoot=Join-Path $workspaceRoot 'artifacts\m1-build'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $outputRoot }
$image=Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
if ($Floppy) { $image = Join-Path $workspaceRoot 'artifacts\freedos-m2\floppy\FD14-minimal.img' }
$drive = if ($Floppy) { 'a' } else { 'c' }
$prompt = $drive.ToUpperInvariant() + ':\>'
$acceptanceRoot=Join-Path $workspaceRoot ('artifacts\freedos-m1\gui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $acceptanceRoot -Force | Out-Null
$commands=Join-Path $acceptanceRoot 'commands.txt'
$inputText = "ver`r`ndir`r`ntype fdconfig.sys`r`n"
if ($WriteOverlay) { $inputText += "echo M2-GUI-WRITE-OK>${drive}:\guiprobe.txt`r`ntype ${drive}:\guiprobe.txt`r`n" }
$inputText += "echo M1-INTERACTIVE-OK`r`n"
[IO.File]::WriteAllText($commands,$inputText)
$exe=Join-Path $outputRoot 'Release\net10.0-windows\DOSEmulator.exe'
$mediaOption = if ($Floppy) { '--floppy-a' } else { '--disk' }
$overlayOption = if ($Floppy) { '--floppy-a-overlay' } else { '--overlay' }
$arguments=@($mediaOption,('"{0}"' -f $image),'--data-dir',('"{0}"' -f (Join-Path $acceptanceRoot 'settings')),'--acceptance-test',('"{0}"' -f $acceptanceRoot),'--type-file',('"{0}"' -f $commands))
if ($WriteOverlay) { $arguments += @($overlayOption,('"{0}"' -f (Join-Path $acceptanceRoot 'gui.cow'))) }
$process=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'FreeDOS GUI acceptance timed out.' }
if ($process.ExitCode -ne 0) { throw "FreeDOS GUI exited with $($process.ExitCode)." }
$result=Get-Content -Raw -LiteralPath (Join-Path $acceptanceRoot 'smoke-result.json') | ConvertFrom-Json
if ($result.status.fault -or $result.captures.Count -ne 6 -or $result.inputBytesSent -ne $result.inputBytesTotal -or -not $result.status.debugOutput.Contains("`r`nM1-INTERACTIVE-OK`r`n$prompt")) { throw 'FreeDOS GUI evidence incomplete.' }
if ($WriteOverlay -and ((-not $Floppy -and -not $result.status.diskWritable) -or -not $result.status.debugOutput.Contains("`r`nM2-GUI-WRITE-OK`r`n"))) { throw 'FreeDOS GUI did not write and read its test file.' }
foreach ($capture in $result.captures) {
    if (-not (Test-Path -LiteralPath $capture)) { throw "Missing capture: $capture" }
    $graphics = Get-Content -Raw -LiteralPath ($capture + '.json') | ConvertFrom-Json
    if ($graphics.error -ne 'NoError' -or $graphics.footerBrightPixels -lt 100) { throw "Incomplete OpenGL capture: $capture" }
}
Write-Output "FreeDOS GUI passed; visually inspect six captures: $acceptanceRoot"
