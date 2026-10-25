#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build')
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\fdc-vds-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$oracle=Join-Path $script:DosWorkspaceRoot 'tests\verify-fdc-vds.py'
$source=Join-Path $script:DosWorkspaceRoot 'Disks and CDs (DO NOT PUT INTO GITHUB)\Microsoft MS-DOS 6.22 [German] (3.5-1.44mb)\Disk01.img'
$originals=@($Disk,$Overlay,$source) | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
python (Join-Path $script:DosWorkspaceRoot 'tests\verify-msdos-install.py') $Disk $Overlay $source > (Join-Path $root 'source.verified.json')
$base=Join-Path $root 'private-installed.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\materialize-private-disk.py') $Disk $Overlay $base > (Join-Path $root 'base.verified.json')
$originals+=Get-DosFileHash -LiteralPath $base
$scratch=Join-Path $root 'private-scratch.img'; $bytes=[byte[]]::new(1474560); [Array]::Fill[byte]($bytes,0x39)
$stream=[IO.File]::Open($scratch,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
try{$stream.Write($bytes)}finally{$stream.Dispose()}
$originals+=Get-DosFileHash -LiteralPath $scratch
$configured=@{}; $media=@{}; $program=@{}
foreach($mode in @('NOEMS','RAM')){
    $prefix=Join-Path $root ('configure-'+$mode); $configured[$mode]=$prefix+'.cow'
    @(@{waitForScreen='C:\>';text="kezb us`r`n"},
      @{waitForScreen='C:\>keyb us';text="copy config.sys config.pre`r`necho DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON >config.sys`r`necho DEVICE=C:\DOS\EMM386.EXE $mode >>config.sys`r`ntype config.pre >>config.sys`r`necho VDS-CONFIG-READY`r`n"}) |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix+'.scenario.json') -Encoding utf8
    dotnet $runner --disk $base --overlay $configured[$mode] --scenario ($prefix+'.scenario.json') --steps 50000000 --stop-text "`r`nVDS-CONFIG-READY`r`n`r`nC:\>" --report ($prefix+'.json') > ($prefix+'.stdout.json')
    $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($r.status.fault -or -not $r.scenario.complete -or $r.ataSectorsWritten -le 0){throw 'DOS did not write the private EMM386 configuration'}
    python (Join-Path $script:DosWorkspaceRoot 'tests\verify-dos-extender-config.py') --base $base --overlay $configured[$mode] --mode $mode > ($prefix+'.verified.json')
    $originals+=Get-DosFileHash -LiteralPath $configured[$mode]
    $program[$mode]=Join-Path $root ($mode+'.COM'); $media[$mode]=Join-Path $root ($mode+'.img')
    $nasmArguments=@('-Werror','-f','bin',(Join-Path $script:DosWorkspaceRoot 'tests\guests\fdc-vds-test.asm'),'-o',$program[$mode])
    if($mode -eq 'RAM'){$nasmArguments+='-DEMS=1'}
    & $nasm @nasmArguments
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $program[$mode] $media[$mode] VDSFDC.COM > (Join-Path $root ($mode+'-media.verified.json'))
    $originals+=Get-DosFileHash -LiteralPath $media[$mode]
}
$scenario=Join-Path $root 'probe.scenario.json'
@(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="ver`r`na:vdsfdc`r`necho VDS-GUEST-RETURN`r`n"}) |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $scenario -Encoding utf8
$results=[Collections.Generic.List[object]]::new();$negativeCount=0
foreach($mode in @('unavailable','NOEMS','RAM')){
    $repeats=if($mode -eq 'unavailable'){1}else{2}; $variant=if($mode -eq 'RAM'){'RAM'}else{'NOEMS'}
    $first=$null
    for($repeat=1;$repeat -le $repeats;$repeat++){
        $prefix=Join-Path $root ($mode+'-'+$repeat);$cow=$prefix+'.cow';$hdd=$prefix+'-hdd.cow'
        if($mode -ne 'unavailable'){Copy-Item -LiteralPath $configured[$mode] -Destination $hdd}
        dotnet $runner --disk $base --overlay $hdd --floppy-a $media[$variant] --floppy-b $scratch --floppy-b-overlay $cow --scenario $scenario --steps 150000000 --stop-text "`r`nVDS-GUEST-RETURN`r`n`r`nC:\>" --memory-dump ($prefix+'.ram') --report ($prefix+'.json') --floppy-io-trace ($prefix+'.io.jsonl') > ($prefix+'.stdout.json')
        $negativeArgs=@()
        if($mode -ne 'unavailable' -and $repeat -eq 1){$negativeArgs=@('--negative-controls',($prefix+'-negative'))}
        python $oracle --report ($prefix+'.json') --memory ($prefix+'.ram') --program $program[$variant] --base $scratch --cow $cow --trace ($prefix+'.io.jsonl') --mode $mode @negativeArgs > ($prefix+'.verified.json')
        $verified=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
        $negativeCount+=if($verified.PSObject.Properties.Name -contains 'negativeControls'){$verified.negativeControls.rejected}else{0}
        $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        if(-not $r.status.debugOutput.Contains('MS-DOS Version 6.22')){throw 'Missing original DOS version response'}
        if($mode -ne 'unavailable' -and (Get-DosFileHash -LiteralPath $hdd).Hash -ne (Get-DosFileHash -LiteralPath $configured[$mode]).Hash){throw 'Probe modified the installed HDD'}
        $row=[pscustomobject]@{mode=$mode;repeat=$repeat;report=$prefix+'.json';verification=$verified;virtualNanoseconds=$r.status.virtualNanoseconds;overlayHash=(Get-DosFileHash -LiteralPath $cow).Hash}
        if($first){if($first.overlayHash -ne $row.overlayHash -or $first.virtualNanoseconds -ne $row.virtualNanoseconds){throw 'FDC/VDS cold boot is not deterministic'}}else{$first=$row}
        $results.Add($row)
    }
}
$formats=[Collections.Generic.List[object]]::new()
foreach($mode in @('NOEMS','RAM')){
    $log=Join-Path $root ($mode+'-format.stdout.json')
    & (Join-Path $PSScriptRoot 'test-msdos-format.ps1') -SkipBuild -OutputRoot $buildRoot -Disk $base -Overlay $configured[$mode] > $log
    $summary=Get-Content -LiteralPath $log -Raw | ConvertFrom-Json
    if(-not $summary.passed -or $summary.coldBoots -ne 3){throw 'Original FORMAT.COM under EMM386 did not pass'}
    $formats.Add([pscustomobject]@{mode=$mode;summary=$summary})
}
foreach($item in @($originals)+@($assemblies)){
    if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Protected source or tested assembly changed: '+$item.Path)}
}
$summary=[pscustomobject]@{passed=$true;directory=$root;coldBoots=13;configurationColdBoots=2;probeColdBoots=5;formatColdBoots=6;
    originalEmm386=$true;nativeWindowsInput=$false;negativeControlsRejected=$negativeCount;vds=$results;originalMicrosoftFormat=$formats;
    sourceImagesUnchanged=$true;testedAssembliesUnchanged=$true;scope='8-MiB 486 / EMM386 NOEMS and RAM; 1.44-MiB FDC; bounded VDS clients, not all VDS/devices'}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
