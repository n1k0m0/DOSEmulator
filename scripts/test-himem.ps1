#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk, [Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\himem-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay) | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$probe=Join-Path $root 'XMSTEST.COM'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\xms-memory-test.asm') -o $probe
$floppy=Join-Path $root 'only-own-xms-client.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy XMSTEST.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$configured=Join-Path $root 'configured-private.cow'
Copy-Item -LiteralPath $Overlay -Destination $configured
$configure=Join-Path $root 'configure.scenario.json'
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';
    text="copy config.sys config.pre`r`necho DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON >config.sys`r`ntype config.pre >>config.sys`r`necho HIMEM-CONFIG-READY`r`n"}) |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configure -Encoding utf8
$headless=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
dotnet $headless --disk $Disk --overlay $configured --scenario $configure --steps 30000000 --stop-text "`r`nHIMEM-CONFIG-READY`r`n`r`nC:\>" --report (Join-Path $root 'configure.json') > (Join-Path $root 'configure.stdout.json')
$configReport=Get-Content -LiteralPath (Join-Path $root 'configure.json') -Raw | ConvertFrom-Json
if($configReport.status.fault -or -not $configReport.scenario.complete -or $configReport.ataSectorsWritten -le 0 -or -not $configReport.status.debugOutput.EndsWith("`r`nHIMEM-CONFIG-READY`r`n`r`nC:\>")){throw 'Private HIMEM configuration did not finish'}
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-himem-media.py') $Disk $Overlay $configured > (Join-Path $root 'configuration.verified.json')
$originals+=Get-DosFileHash -LiteralPath $configured
$plan=Join-Path $root 'xms.scenario.json'
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="mem /c`r`na:xmstest`r`nver`r`necho HIMEM-XMS-RUN-END`r`n"}) |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
$cases=@()
foreach($number in 1..2){
    $prefix=Join-Path $root ('himem-'+$number); $cow=$prefix+'.cow'
    Copy-Item -LiteralPath $configured -Destination $cow
    $hash=(Get-DosFileHash -LiteralPath $cow).Hash
    dotnet $headless --disk $Disk --overlay $cow --floppy-a $floppy --scenario $plan --steps 120000000 --stop-text "`r`nHIMEM-XMS-RUN-END`r`n`r`nC:\>" --report ($prefix+'.json') --memory-dump ($prefix+'.private-ram.bin') > ($prefix+'.stdout.json')
    python (Join-Path $script:DosWorkspaceRoot 'tests\verify-xms-memory.py') --report ($prefix+'.json') --memory ($prefix+'.private-ram.bin') > ($prefix+'.verified.json')
    if((Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'HIMEM read-only guest test changed COW'}
    $cases+=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
}
if($cases[0].frameHash -ne $cases[1].frameHash -or $cases[0].status.virtualNanoseconds -ne $cases[1].status.virtualNanoseconds -or $cases[0].cpu.protectedModeEntries -ne $cases[1].cpu.protectedModeEntries){throw 'HIMEM cold boots differ'}
$negative=Join-Path $root 'no-driver';$negativeCow=$negative+'.cow'
Copy-Item -LiteralPath $Overlay -Destination $negativeCow
$hash=(Get-DosFileHash -LiteralPath $negativeCow).Hash
dotnet $headless --disk $Disk --overlay $negativeCow --floppy-a $floppy --scenario $plan --steps 30000000 --stop-text "`r`nHIMEM-XMS-RUN-END`r`n`r`nC:\>" --report ($negative+'.json') --memory-dump ($negative+'.private-ram.bin') > ($negative+'.stdout.json')
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-xms-memory.py') --report ($negative+'.json') --memory ($negative+'.private-ram.bin') --expect-absent > ($negative+'.verified.json')
if((Get-DosFileHash -LiteralPath $negativeCow).Hash -ne $hash){throw 'Missing-driver control changed COW'}
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'HIMEM protected media changed'}}
foreach($item in $assemblies){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'HIMEM tested assembly changed'}}
$summary=@{passed=$true;directory=$root;configurationColdBoots=1;himemColdBoots=2;missingDriverControls=1;
    system='Original German MS-DOS 6.22 HIMEM.SYS';testmem='ON';originalsUnchanged=$true;configuredOnlyInPrivateCopy=$true;
    frameHash=$cases[0].frameHash;virtualNanoseconds=$cases[0].status.virtualNanoseconds;
    protectedModeEntries=$cases[0].cpu.protectedModeEntries;protectedModeExits=$cases[0].cpu.protectedModeExits;
    onlyOwnProgramOnFloppy=$true;virtual8086=$false;paging=$false;x87Arithmetic=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
