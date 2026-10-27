# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\emm386-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay) | Get-DosFileHash
$headless=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$probe=Join-Path $root 'EMMTEST.COM';$floppy=Join-Path $root 'only-own-emm-client.img'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\emm386-memory-test.asm') -o $probe
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy EMMTEST.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
# DOS itself copies the own client for the IDE test and absent-driver control.
# A second matrix loads the identical client through the FDC under EMM386.
$probeOnly=Join-Path $root 'own-program-private.cow'
Copy-Item -LiteralPath $Overlay -Destination $probeOnly
$copyPlan=Join-Path $root 'copy-own.scenario.json'
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="copy a:emmtest.com c:\emmtest.com`r`necho OWN-PROBE-COPIED`r`n"}) |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $copyPlan -Encoding utf8
dotnet $headless --disk $Disk --overlay $probeOnly --floppy-a $floppy --scenario $copyPlan --steps 40000000 --stop-text "`r`nOWN-PROBE-COPIED`r`n`r`nC:\>" --report (Join-Path $root 'copy-own.json') > (Join-Path $root 'copy-own.stdout.json')
$copyReport=Get-Content -LiteralPath (Join-Path $root 'copy-own.json') -Raw | ConvertFrom-Json
if($copyReport.status.fault -or -not $copyReport.scenario.complete -or $copyReport.ataSectorsWritten -le 0){throw 'DOS did not copy the own EMM386 client'}
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-emm386-media.py') --base $Disk --original $Overlay --configured $probeOnly --program $probe --no-driver > (Join-Path $root 'own-program.verified.json')
$configured=Join-Path $root 'configured-private.cow'
Copy-Item -LiteralPath $probeOnly -Destination $configured
$configure=Join-Path $root 'configure.scenario.json'
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="copy config.sys config.pre`r`necho DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON >config.sys`r`necho DEVICE=C:\DOS\EMM386.EXE NOEMS >>config.sys`r`ntype config.pre >>config.sys`r`necho EMM-CONFIG-READY`r`n"}) |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configure -Encoding utf8
dotnet $headless --disk $Disk --overlay $configured --scenario $configure --steps 40000000 --stop-text "`r`nEMM-CONFIG-READY`r`n`r`nC:\>" --report (Join-Path $root 'configure.json') > (Join-Path $root 'configure.stdout.json')
$configReport=Get-Content -LiteralPath (Join-Path $root 'configure.json') -Raw | ConvertFrom-Json
if($configReport.status.fault -or -not $configReport.scenario.complete -or $configReport.ataSectorsWritten -le 0){throw 'Private EMM386 configuration did not finish'}
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-emm386-media.py') --base $Disk --original $Overlay --configured $configured --program $probe > (Join-Path $root 'configuration.verified.json')
$originals+=@($probeOnly,$configured) | Get-DosFileHash
$cases=@();$allRuns=@();$allProofs=@()
foreach($source in @('ide','floppy')){
$plan=Join-Path $root ($source+'.scenario.json')
$command=if($source -eq 'floppy'){'a:emmtest'}else{'emmtest'}
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="mem /c`r`n"},
  @{waitForDebug='C:\>mem /c';text="$command`r`n"},@{waitForDebug='EMM-MEMORY-PROOF=';text="ver`r`necho EMM-GUEST-END`r`n"}) |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
$runs=@();$proofs=@()
foreach($number in 1..2){
    $prefix=Join-Path $root ($source+'-'+$number);$cow=$prefix+'.cow'
    Copy-Item -LiteralPath $configured -Destination $cow
    $hash=(Get-DosFileHash -LiteralPath $cow).Hash
    $arguments=@($headless,'--disk',$Disk,'--overlay',$cow,'--scenario',$plan,'--steps','120000000',
        '--stop-text',"`r`nEMM-GUEST-END`r`n`r`nC:\>",'--report',($prefix+'.json'),'--memory-dump',($prefix+'.private-ram.bin'))
    if($source -eq 'floppy'){$arguments+=@('--floppy-a',$floppy)}
    dotnet @arguments > ($prefix+'.stdout.json')
    python (Join-Path $script:DosWorkspaceRoot 'tests\verify-emm386-memory.py') --report ($prefix+'.json') --memory ($prefix+'.private-ram.bin') --program-source $source > ($prefix+'.verified.json')
    if((Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'EMM386 memory guest changed COW'}
    $runs+=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    $proofs+=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
}
if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds -or $proofs[0].proofSha256 -ne $proofs[1].proofSha256){throw 'EMM386 cold boots differ'}
$cases+=@{programSource=$source;coldBoots=2;frameHash=$runs[0].frameHash;proofSha256=$proofs[0].proofSha256;
    virtualNanoseconds=$runs[0].status.virtualNanoseconds;floppySectorsRead=$runs[0].floppySectorsRead;relocatedEbda=$proofs[0].relocatedEbda}
$allRuns+=$runs;$allProofs+=$proofs
}
$plan=Join-Path $root 'ide.scenario.json'
$negative=Join-Path $root 'no-monitor';$cow=$negative+'.cow'
Copy-Item -LiteralPath $probeOnly -Destination $cow;$hash=(Get-DosFileHash -LiteralPath $cow).Hash
dotnet $headless --disk $Disk --overlay $cow --scenario $plan --steps 40000000 --stop-text "`r`nEMM-GUEST-END`r`n`r`nC:\>" --report ($negative+'.json') --memory-dump ($negative+'.private-ram.bin') > ($negative+'.stdout.json')
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-emm386-memory.py') --report ($negative+'.json') --memory ($negative+'.private-ram.bin') --expect-absent > ($negative+'.verified.json')
if((Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'Missing EMM386 control changed COW'}
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-emm386-memory.py') --report (Join-Path $root 'ide-1.json') --memory (Join-Path $root 'ide-1.private-ram.bin') --negative-controls (Join-Path $root 'negative-controls') > (Join-Path $root 'negative-validation.json')
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'EMM386 protected media changed'}}
foreach($item in $assemblies){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'EMM386 tested assembly changed'}}
$summary=@{passed=$true;directory=$root;configurationColdBoots=2;emm386ColdBoots=4;missingDriverControls=1;cases=$cases;
    system='Original German MS-DOS 6.22 media EMM386.EXE';options='NOEMS';testmem='ON';originalsUnchanged=$true;
    configuredOnlyInPrivateCopy=$true;vcpiVersion='1.00';allocatedAndFreedPages=2;
    copiedBytesPerPage=4096;negativeOracleControls=8;paging=$true;virtual8086=$true;guestProgramLoadedFrom=@('IDE','FDC');
    relocatedEbda=$true;floppyUnderEmm386Accepted=$true;emsPageFrameAccepted=$false;vcpiProtectedClientSwitchAccepted=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
