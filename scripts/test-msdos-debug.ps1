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
$modes=@([pscustomobject]@{name='none';disk=$Disk;overlay=$Overlay})
if($configured.Count -eq 3){
    $ConfiguredDisk=Resolve-DosPath $ConfiguredDisk;$NoEmsOverlay=Resolve-DosPath $NoEmsOverlay;$RamOverlay=Resolve-DosPath $RamOverlay
    $modes+=@([pscustomobject]@{name='NOEMS';disk=$ConfiguredDisk;overlay=$NoEmsOverlay},[pscustomobject]@{name='RAM';disk=$ConfiguredDisk;overlay=$RamOverlay})
}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\debug-m3\msdebug-acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$oracle=Join-Path $script:DosWorkspaceRoot 'tests\verify-msdos-debug.py'
$originals=@($modes | ForEach-Object { $_.disk; $_.overlay }) | Select-Object -Unique | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$steps=@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="c:\dos\debug`r`n"},
    @{waitForScreen='-';text="e 100 b8 34 12 40 90`r`nt=100`r`nt`r`nr`r`nq`r`nver`r`necho MSDEBUG-END`r`n"})
$plan=Join-Path $root 'debug.scenario.json'
$steps | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
$cases=[Collections.Generic.List[object]]::new();$negativeCount=0
foreach($mode in $modes){
    $first=$null
    foreach($repeat in 1..2){
        $prefix=Join-Path $root ($mode.name+'-'+$repeat);$cow=$prefix+'.cow'
        Copy-Item -LiteralPath $mode.overlay -Destination $cow;$hash=(Get-DosFileHash -LiteralPath $cow).Hash
        dotnet $runner --disk $mode.disk --overlay $cow --scenario $plan --steps 150000000 --stop-text "`r`nMSDEBUG-END`r`n`r`nC:\>" --memory-dump ($prefix+'.private-ram.bin') --report ($prefix+'.json') > ($prefix+'.stdout.json')
        $arguments=@($oracle,'--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),'--base',$mode.disk,'--overlay',$cow,'--mode',$mode.name)
        if($repeat -eq 1){$arguments+=@('--negative-controls',($prefix+'-negative'))}
        python @arguments > ($prefix+'.verified.json')
        if((Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'Original DEBUG changed its private disk overlay'}
        $verified=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        if($verified.PSObject.Properties.Name -contains 'negativeControls'){$negativeCount+=$verified.negativeControls.rejected}
        $report=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        $row=[pscustomobject]@{mode=$mode.name;repeat=$repeat;report=$prefix+'.json';verification=$verified;
            frameHash=$report.frameHash;virtualNanoseconds=$report.status.virtualNanoseconds;debugOutput=$report.status.debugOutput}
        if($first){if($first.frameHash -ne $row.frameHash -or $first.virtualNanoseconds -ne $row.virtualNanoseconds -or $first.debugOutput -cne $row.debugOutput){throw 'Original DEBUG cold boots differ'}}else{$first=$row}
        $cases.Add($row)
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'DEBUG source, overlay or tested DLL changed'}}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$cases.Count;cases=$cases;
    negativeControlsRejected=$negativeCount;originalMicrosoftDebug=$true;singleStepCommandsPerBoot=2;
    originalMediaAndBaselinesUnchanged=$true;nativeWindowsInput=$false;completeDebuggerCompatibility=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 10
