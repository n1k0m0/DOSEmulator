# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild, [string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk, [Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\cpu-system-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash
$profile=Join-Path $root 'profile.json'
@{name='486 real-mode system-register guest acceptance'} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
$probe=Join-Path $root 'CPUSYS.COM'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\cpu-system-test.asm') -o $probe
$floppy=Join-Path $root 'only-own-cpu-system-probe.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy CPUSYS.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$cases=@()
foreach($os in @('msdos','freedos')){
    $steps=@(); $base=$freeDos; $wait='C:\>'
    if($os -eq 'msdos'){$base=$Disk; $steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
    $steps+=@{waitForScreen=$wait;text="a:cpusys`r`nver`r`necho CPU-SYSTEM-RUN-END`r`n"}
    $plan=Join-Path $root ($os+'.scenario.json')
    ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
    $runs=@(); $proofs=@()
    foreach($number in 1..2){
        $prefix=Join-Path $root ($os+'-'+$number); $cow=$null
        $stop=if($os -eq 'msdos'){"`r`nCPU-SYSTEM-RUN-END`r`n`r`nC:\>"}else{"`r`nCPU-SYSTEM-RUN-END`r`nC:\>"}
        $arguments=@((Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'),'--disk',$base,'--profile',$profile,
            '--floppy-a',$floppy,'--scenario',$plan,'--steps','30000000','--stop-text',$stop,
            '--report',($prefix+'.json'),'--memory-dump',($prefix+'.private-ram.bin'))
        if($os -eq 'msdos'){
            $cow=$prefix+'.cow'; Copy-Item -LiteralPath $Overlay -Destination $cow
            $hash=(Get-DosFileHash -LiteralPath $cow).Hash; $arguments+=@('--overlay',$cow)
        }
        dotnet @arguments > ($prefix+'.stdout.json')
        python (Join-Path $script:DosWorkspaceRoot 'tests\verify-cpu-system.py') --report ($prefix+'.json') --memory ($prefix+'.private-ram.bin') --program $probe --os $os > ($prefix+'.verified.json')
        if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'CPU system probe changed private COW'}
        $runs+=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        $proofs+=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $proofs[0].proofSha256 -ne $proofs[1].proofSha256 -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'CPU system cold boots differ'}
    $cases+=@{os=$os;coldBoots=2;frameHash=$runs[0].frameHash;proofSha256=$proofs[0].proofSha256;
        systemStateRestored=$true;guestReturned=$true;virtualNanoseconds=$runs[0].status.virtualNanoseconds}
}
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'CPU system protected media changed'}}
foreach($item in $assemblies){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'CPU system tested assembly changed'}}
$summary=@{passed=$true;directory=$root;headlessColdBoots=4;cases=$cases;originalsUnchanged=$true;
    onlyOwnProgramOnFloppy=$true;protectedMode=$false;paging=$false;x87Arithmetic=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
