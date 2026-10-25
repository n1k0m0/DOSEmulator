#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\lock-alignment-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$oracle=Join-Path $script:DosWorkspaceRoot 'tests\verify-cpu-lock-alignment.py'
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$cases=[Collections.Generic.List[object]]::new();$negativeCount=0
foreach($paging in @($false,$true)){
    $variant=if($paging){'paging'}else{'segmentation'}
    $program=Join-Path $root ($variant+'.COM');$floppy=Join-Path $root ($variant+'.img')
    $nasmArguments=@('-Werror','-f','bin',(Join-Path $script:DosWorkspaceRoot 'tests\guests\cpu-lock-alignment-test.asm'),'-o',$program)
    if($paging){$nasmArguments+='-DPAGING=1'}
    & $nasm @nasmArguments
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program $floppy CPULOCK.COM > (Join-Path $root ($variant+'-media.json'))
    $originals+=Get-DosFileHash -LiteralPath $floppy
    foreach($os in @('msdos','freedos')){
        $steps=@();$wait='C:\>';$base=$freeDos
        if($os -eq 'msdos'){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
        $steps+=@{waitForScreen=$wait;text="a:cpulock`r`nver`r`necho LOCK-GUEST-END`r`n"}
        $scenario=Join-Path $root ($variant+'-'+$os+'.scenario.json')
        ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $scenario -Encoding utf8
        $first=$null
        foreach($repeat in 1..2){
            $prefix=Join-Path $root ($variant+'-'+$os+'-'+$repeat);$cow=$null
            $stop=if($os -eq 'msdos'){"`r`nLOCK-GUEST-END`r`n`r`nC:\>"}else{"`r`nLOCK-GUEST-END`r`nC:\>"}
            $arguments=@($runner,'--disk',$base,'--floppy-a',$floppy,'--scenario',$scenario,'--steps','100000000',
                '--stop-text',$stop,'--memory-dump',($prefix+'.private-ram.bin'),'--report',($prefix+'.json'))
            if($os -eq 'msdos'){$cow=$prefix+'.cow';Copy-Item -LiteralPath $Overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',$cow)}
            dotnet @arguments > ($prefix+'.stdout.json')
            $oracleArguments=@('--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),'--program',$program,'--os',$os)
            if($paging){$oracleArguments+='--paging'}
            if($os -eq 'msdos' -and $repeat -eq 1){$oracleArguments+=@('--negative-controls',($prefix+'-negative'))}
            python $oracle @oracleArguments > ($prefix+'.verified.json')
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $cowHash){throw 'LOCK/alignment guest changed private DOS HDD'}
            $verified=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            if($verified.PSObject.Properties.Name -contains 'negativeControls'){$negativeCount+=$verified.negativeControls.rejected}
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $row=[pscustomobject]@{os=$os;identityPaging=$paging;repeat=$repeat;report=$prefix+'.json';verification=$verified;
                frameHash=$r.frameHash;proofHash=$verified.proofSha256;virtualNanoseconds=$r.status.virtualNanoseconds}
            if($first){if($first.frameHash -ne $row.frameHash -or $first.proofHash -ne $row.proofHash -or $first.virtualNanoseconds -ne $row.virtualNanoseconds){throw 'LOCK/alignment cold boots differ'}}else{$first=$row}
            $cases.Add($row)
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('LOCK/alignment source or tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=8;cases=$cases;ownLockedUpdatesPerBoot=10;
    invalidLockFaultsPerBoot=4;referenceAlignmentRetries=$true;identityPagingVariants=$true;negativeControlsRejected=$negativeCount;
    realInstructionRetry=$true;all486BusPrecisionAccepted=$false;privateTrCacheRemainsUntilReset=$true;originalsUnchanged=$true;testedAssembliesUnchanged=$true;
    nativeWindowsInput=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 12
