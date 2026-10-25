#requires -Version 7.4
[CmdletBinding()]
param([switch]$Gui)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $workspaceRoot ('artifacts\script-tests\acceptance-' + [guid]::NewGuid().ToString('N'))
$foreignDirectory = Join-Path $testRoot 'caller with spaces [ä]'
New-Item -ItemType Directory -Path $foreignDirectory -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw "Script regression failed: $Name" }
    $checks.Add($Name)
}
function Child([string]$File, [string[]]$Arguments, [string]$Name, [hashtable]$Environment = @{}, [string]$RawArguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $foreignDirectory
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    if ($RawArguments) { $info.Arguments = $RawArguments }
    else { foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) } }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "Private script test timed out: $Name" }
        $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $testRoot ($Name+'.stdout.log')), $out)
        [IO.File]::WriteAllText((Join-Path $testRoot ($Name+'.stderr.log')), $err)
        return [pscustomobject]@{ exitCode=$process.ExitCode; stdout=$out; stderr=$err }
    } finally { $process.Dispose() }
}
function Batch([string]$Path, [string]$Arguments, [string]$Name, [hashtable]$Environment = @{}) {
    # cmd /s /c requires a separate pair of outer quotes. These paths are our
    # own filesystem test fixtures, not assembled commands from guest content.
    return Child -File $env:ComSpec -Name $Name -Environment $Environment -RawArguments ('/d /s /c ""'+$Path+'" '+$Arguments+'"')
}

$files = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)
foreach ($file in $files) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) ($file.Name+' parses')
    Check ($ast.ScriptRequirements.RequiredPSVersion -eq [version]'7.4') ($file.Name+' declares its PowerShell runtime')
    $oldHashes=$ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Get-FileHash'},$true)
    Check ($oldHashes.Count -eq 0) ($file.Name+' has no Get-FileHash dependency')
}

$abc=Join-Path $foreignDirectory 'hash [ä] literal.bin'; [IO.File]::WriteAllBytes($abc,[Text.Encoding]::ASCII.GetBytes('abc'))
$empty=Join-Path $foreignDirectory 'empty.bin'; [IO.File]::WriteAllBytes($empty,[byte[]]@())
$expected='BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD'
Check ((Get-DosFileHash -LiteralPath $abc).Hash -eq $expected) 'SHA256 known abc vector and literal brackets/unicode'
Check ((Get-DosFileHash -LiteralPath $empty).Hash -eq 'E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855') 'SHA256 empty vector'
$hashes=@(@($abc,$empty) | Get-DosFileHash)
Check ($hashes.Count -eq 2 -and $hashes[0].Path -ceq $abc -and $hashes[0].Hash -eq $expected) 'path-array pipeline keeps full literal paths'
$fileHash=Get-Item -LiteralPath $abc | Get-DosFileHash
Check ($fileHash.Path -ceq $abc -and $fileHash.Hash -eq $expected) 'FileInfo pipeline uses FullName rather than display name'
Push-Location -LiteralPath $foreignDirectory
try {
    Check ((Resolve-DosPath -Path 'new overlay [ä].cow') -ceq (Join-Path $foreignDirectory 'new overlay [ä].cow')) 'new user paths use PowerShell caller directory'
    Check ((Get-DosBuildRoot -OutputRoot 'artifacts\m1-build') -ceq (Join-Path $workspaceRoot 'artifacts\m1-build')) 'relative build root uses repository from foreign caller'
    Check ((Get-DosBuildRoot -OutputRoot $foreignDirectory) -ceq $foreignDirectory) 'absolute build root is not appended to repository'
    Check ((Get-DosFileHash -LiteralPath 'hash [ä] literal.bin').Hash -eq $expected) 'relative hash uses caller rather than process working directory'
} finally { Pop-Location }

$hostExe=Join-Path $PSHOME 'pwsh.exe'
$commonPath=Join-Path $PSScriptRoot 'common.ps1'
$failure=Join-Path $testRoot 'native-failure.ps1'; $unreachable=Join-Path $testRoot 'must-not-exist.txt'
[IO.File]::WriteAllText($failure,@'
#requires -Version 7.4
. ($args[0])
& $env:ComSpec /d /c 'exit 23' | Out-Null
[IO.File]::WriteAllText($args[1], 'incorrect continuation')
'@)
$failed=Child -File $hostExe -Arguments @('-NoProfile','-File',$failure,$commonPath,$unreachable) -Name 'native-failure'
Check ($failed.exitCode -ne 0 -and -not (Test-Path -LiteralPath $unreachable)) 'failed piped native command stops the script'

$firmwareHash=(Get-DosFileHash -LiteralPath (Join-Path $workspaceRoot 'src\DOSEmulator.Runtime\ReferenceFirmware.Image.g.cs')).Hash
$withoutHash=Join-Path $testRoot 'without-hash-cmdlet.ps1'
[IO.File]::WriteAllText($withoutHash,@'
#requires -Version 7.4
function Get-FileHash { throw 'The old command must never be used.' }
& ($args[0])
'@)
$built=Child -File $hostExe -Arguments @('-NoProfile','-File',$withoutHash,(Join-Path $PSScriptRoot 'build-firmware.ps1')) -Name 'firmware-without-hash-cmdlet'
Check ($built.exitCode -eq 0) 'firmware builds without Get-FileHash from foreign directory'
Check ((Get-DosFileHash -LiteralPath (Join-Path $workspaceRoot 'src\DOSEmulator.Runtime\ReferenceFirmware.Image.g.cs')).Hash -eq $firmwareHash) 'firmware bytes/source manifest remain unchanged'

foreach ($launcher in @('run.cmd','run-freedos.cmd','run-freedos-floppy.cmd')) {
    $help=Batch -Path (Join-Path $workspaceRoot $launcher) -Arguments '-?' -Name ($launcher+'-help')
    Check ($help.exitCode -eq 0 -and $help.stdout.Contains('NoBuild') -and -not $help.stderr.Contains('Requires')) ($launcher+' selects a supported host and forwards arguments')
}
$badArgs=Batch -Path (Join-Path $workspaceRoot 'run.cmd') -Arguments '-DefinitelyInvalidSwitch' -Name 'bad-launcher-argument'
Check ($badArgs.exitCode -ne 0) 'CMD propagates PowerShell parameter errors'
$badProfile=Batch -Path (Join-Path $workspaceRoot 'run.cmd') -Arguments '-NoBuild -Profile "missing profile [ä].json"' -Name 'bad-launcher-profile'
Check ($badProfile.exitCode -ne 0) 'CMD propagates script errors rather than reporting success'

$legacy=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$oldHost=Child -File $legacy -Arguments @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'build-firmware.ps1')) -Name 'legacy-host'
Check ($oldHost.exitCode -ne 0 -and ($oldHost.stdout+$oldHost.stderr).Contains('7.4')) 'Windows PowerShell 5.1 reports required runtime before touching firmware'
$noHost=Batch -Path (Join-Path $PSScriptRoot 'invoke-powershell.cmd') -Arguments ('"'+(Join-Path $PSScriptRoot 'run.ps1')+'" -?') -Name 'missing-host' -Environment @{
    PATH=(Join-Path $env:SystemRoot 'System32'); ProgramFiles=$foreignDirectory; ProgramW6432=$foreignDirectory; LOCALAPPDATA=$foreignDirectory }
Check ($noHost.exitCode -ne 0 -and $noHost.stderr.Contains('PowerShell 7.4')) 'missing PowerShell has actionable error and no legacy fallback'
$storeHost=Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh.exe'
if (Test-Path -LiteralPath $storeHost) {
    $store=Batch -Path (Join-Path $workspaceRoot 'run.cmd') -Arguments '-?' -Name 'store-host-fallback' -Environment @{
        PATH=(Join-Path $env:SystemRoot 'System32'); ProgramFiles=$foreignDirectory; ProgramW6432=$foreignDirectory }
    Check ($store.exitCode -eq 0 -and $store.stdout.Contains('SmokeTestDirectory')) 'Microsoft Store PowerShell fallback works without pwsh on PATH'
}

if ($Gui) {
    $smoke=Join-Path $testRoot 'real CMD GUI [ä]'
    $started=Batch -Path (Join-Path $workspaceRoot 'run.cmd') -Arguments ('-NoBuild -SmokeTestDirectory "'+$smoke+'"') -Name 'real-launcher-gui'
    Check ($started.exitCode -eq 0) 'real run.cmd starts and finishes its private native GUI test'
    $result=Get-Content -LiteralPath (Join-Path $smoke 'smoke-result.json') -Raw | ConvertFrom-Json
    Check (-not $result.status.fault -and $result.captures.Count -eq 6) 'real CMD launch has six complete GUI captures and no guest fault'
    foreach ($capture in $result.captures) {
        $graphics=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
        Check ($graphics.error -eq 'NoError' -and $graphics.footerBrightPixels -gt 100) 'native CMD capture has complete GL frame and footer'
    }
}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;powerShell=$PSVersionTable.PSVersion.ToString();scriptsParsed=$files.Count;
    checks=$checks.Count;cases=$checks;gui=[bool]$Gui;firmwareUnchanged=$true;originalMediaUsed=$false}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
