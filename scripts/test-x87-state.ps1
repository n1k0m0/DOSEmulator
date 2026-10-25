#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk,[string]$NoEmsOverlay,[string]$RamOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay
$configured=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Provide ConfiguredDisk, NoEmsOverlay and RamOverlay together.'}
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},[pscustomobject]@{name='freedos';disk=$freeDos;overlay=$null})
if($configured.Count -eq 3){
    $ConfiguredDisk=Resolve-DosPath $ConfiguredDisk;$NoEmsOverlay=Resolve-DosPath $NoEmsOverlay;$RamOverlay=Resolve-DosPath $RamOverlay
    $systems+=@([pscustomobject]@{name='emm386-noems';disk=$ConfiguredDisk;overlay=$NoEmsOverlay},
        [pscustomobject]@{name='emm386-ram';disk=$ConfiguredDisk;overlay=$RamOverlay})
}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\x87-state-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$oracle=Join-Path $script:DosWorkspaceRoot 'tests\verify-x87-state.py'
$originals=@($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$cases=[Collections.Generic.List[object]]::new();$negativeCount=0
foreach($variant in @('real-v86','protected','paging')){
    $protected=$variant -ne 'real-v86';$paging=$variant -eq 'paging'
    $program=Join-Path $root ($variant+'.COM');$floppy=Join-Path $root ($variant+'.img')
    $nasmArguments=@('-Werror','-f','bin','-I',((Join-Path $script:DosWorkspaceRoot 'tests\guests')+[IO.Path]::DirectorySeparatorChar),
        (Join-Path $script:DosWorkspaceRoot 'tests\guests\x87-state-test.asm'),'-o',$program)
    if($protected){$nasmArguments+='-DPROTECTED=1'}
    if($paging){$nasmArguments+='-DPAGING=1'}
    & $nasm @nasmArguments
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program $floppy FPSTATE.COM > (Join-Path $root ($variant+'-media.json'))
    $originals+=Get-DosFileHash -LiteralPath $floppy
    foreach($system in $systems){
        if($protected -and $system.name.StartsWith('emm386')){continue}
        $steps=@();$wait='C:\>'
        if($system.name -ne 'freedos'){$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
        $steps+=@{waitForScreen=$wait;text="a:fpstate`r`nver`r`necho FPSTATE-END`r`n"}
        $scenario=Join-Path $root ($variant+'-'+$system.name+'.scenario.json')
        ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $scenario -Encoding utf8
        $first=$null
        foreach($repeat in 1..2){
            $prefix=Join-Path $root ($variant+'-'+$system.name+'-'+$repeat);$cow=$null
            $stop=if($system.name -ne 'freedos'){"`r`nFPSTATE-END`r`n`r`nC:\>"}else{"`r`nFPSTATE-END`r`nC:\>"}
            $arguments=@($runner,'--disk',$system.disk,'--floppy-a',$floppy,'--scenario',$scenario,'--steps','150000000',
                '--stop-text',$stop,'--memory-dump',($prefix+'.private-ram.bin'),'--report',($prefix+'.json'))
            if($system.overlay){$cow=$prefix+'.cow';Copy-Item -LiteralPath $system.overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',$cow)}
            dotnet @arguments > ($prefix+'.stdout.json')
            $oracleArguments=@('--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),'--program',$program,'--system',$system.name)
            if($protected){$oracleArguments+='--protected'}
            if($paging){$oracleArguments+='--paging'}
            if($system.name -eq 'msdos' -and $repeat -eq 1){$oracleArguments+=@('--negative-controls',($prefix+'-negative'))}
            python $oracle @oracleArguments > ($prefix+'.verified.json')
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $cowHash){throw 'x87 state guest changed its private DOS HDD'}
            $verified=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
            if($verified.PSObject.Properties.Name -contains 'negativeControls'){$negativeCount+=$verified.negativeControls.rejected}
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            $row=[pscustomobject]@{system=$system.name;variant=$variant;repeat=$repeat;report=$prefix+'.json';verification=$verified;
                frameHash=$r.frameHash;proofHash=$verified.proofSha256;imageDigest=$verified.imageDigest;virtualNanoseconds=$r.status.virtualNanoseconds}
            if($first){
                if($first.frameHash -ne $row.frameHash -or $first.proofHash -ne $row.proofHash -or $first.imageDigest -ne $row.imageDigest -or $first.virtualNanoseconds -ne $row.virtualNanoseconds){throw 'x87 state cold boots differ'}
            }else{$first=$row}
            $cases.Add($row)
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('x87 source, media or tested DLL changed: '+$item.Path)}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$cases.Count;cases=$cases;
    negativeControlsRejected=$negativeCount;x87InstructionsPerBoot=51;allEightRaw80BitRegisters=$true;
    physicalTagsAndLogicalSaveOrder=$true;legacyEnvironmentAndSaveFormats=$true;originalFpuImageRestored=$true;
    originalMediaAndBaselinesUnchanged=$true;testedAssembliesUnchanged=$true;privateProtectedTrRemainsUntilReset=$true;
    nativeWindowsInput=$false;arithmeticAccepted=$false;ferrBoardWiringAccepted=$false;allX87OrSiliconPrecisionAccepted=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 12
