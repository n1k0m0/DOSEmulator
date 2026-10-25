#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){ & (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot }
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\vcpi-ems-m3\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$headless=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$oracle=Join-Path $script:DosWorkspaceRoot 'tests\verify-vcpi-ems.py'
$mediaOracle=Join-Path $script:DosWorkspaceRoot 'tests\verify-vcpi-ems-media.py'
$originals=@($Disk,$Overlay) | Get-DosFileHash
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$programs=@{vcpi='VCPIPM';ems='EMSBANK'}
$sources=@{vcpi='vcpi-protected-test.asm';ems='ems-bank-test.asm'}
foreach($kind in @('vcpi','ems')){
    $name=$programs[$kind]
    & $nasm -Werror -f bin (Join-Path $script:DosWorkspaceRoot ('tests\guests\'+$sources[$kind])) -o (Join-Path $root ($name+'.COM'))
    python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') (Join-Path $root ($name+'.COM')) (Join-Path $root ($name+'.img')) ($name+'.COM') > (Join-Path $root ($kind+'-media.json'))
    $originals+=Get-DosFileHash -LiteralPath (Join-Path $root ($name+'.img'))
}
# Real DOS COPY creates both IDE clients in a private installation copy.
$ownPrograms=Join-Path $root 'own-programs-private.cow'
Copy-Item -LiteralPath $Overlay -Destination $ownPrograms
foreach($kind in @('vcpi','ems')){
    $name=$programs[$kind]; $prefix=Join-Path $root ('copy-'+$kind)
    @(@{waitForScreen='C:\>';text="kezb us`r`n"},
      @{waitForScreen='C:\>keyb us';text="copy a:$name.com c:\$name.com`r`necho OWN-PROBE-COPIED`r`n"}) |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix+'.scenario.json') -Encoding utf8
    dotnet $headless --disk $Disk --overlay $ownPrograms --floppy-a (Join-Path $root ($name+'.img')) --scenario ($prefix+'.scenario.json') --steps 50000000 --stop-text "`r`nOWN-PROBE-COPIED`r`n`r`nC:\>" --report ($prefix+'.json') > ($prefix+'.stdout.json')
    $report=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($report.status.fault -or -not $report.scenario.complete -or $report.ataSectorsWritten -le 0){throw 'DOS did not copy the own memory probe'}
}
python $mediaOracle --base $Disk --original $Overlay --configured $ownPrograms --vcpi (Join-Path $root 'VCPIPM.COM') --ems (Join-Path $root 'EMSBANK.COM') --mode no-driver > (Join-Path $root 'own-programs.verified.json')
$originals+=Get-DosFileHash -LiteralPath $ownPrograms
$configured=@{}
foreach($mode in @('NOEMS','RAM')){
    $prefix=Join-Path $root ('configure-'+$mode)
    $configured[$mode]=$prefix+'.cow'
    Copy-Item -LiteralPath $ownPrograms -Destination $configured[$mode]
    @(@{waitForScreen='C:\>';text="kezb us`r`n"},
      @{waitForScreen='C:\>keyb us';text="copy config.sys config.pre`r`necho DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON >config.sys`r`necho DEVICE=C:\DOS\EMM386.EXE $mode >>config.sys`r`ntype config.pre >>config.sys`r`necho MEMORY-CONFIG-READY`r`n"}) |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix+'.scenario.json') -Encoding utf8
    dotnet $headless --disk $Disk --overlay $configured[$mode] --scenario ($prefix+'.scenario.json') --steps 50000000 --stop-text "`r`nMEMORY-CONFIG-READY`r`n`r`nC:\>" --report ($prefix+'.json') > ($prefix+'.stdout.json')
    $report=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($report.status.fault -or -not $report.scenario.complete -or $report.ataSectorsWritten -le 0){throw 'Private memory-manager configuration did not finish'}
    python $mediaOracle --base $Disk --original $Overlay --configured $configured[$mode] --vcpi (Join-Path $root 'VCPIPM.COM') --ems (Join-Path $root 'EMSBANK.COM') --mode $mode > ($prefix+'.verified.json')
    $originals+=Get-DosFileHash -LiteralPath $configured[$mode]
}
function Run-MemoryGuest([string]$Kind,[string]$Mode,[int]$RamMiB,[string]$Source,[int]$Number,[switch]$Unavailable){
    $name=$programs[$Kind]
    $prefix=Join-Path $root ("$Mode-$RamMiB-$Kind-$Source-$Number")
    $cow=$prefix+'.cow'
    $baseline=if($Mode -eq 'no-driver'){$ownPrograms}else{$configured[$Mode]}
    Copy-Item -LiteralPath $baseline -Destination $cow
    $hash=(Get-DosFileHash -LiteralPath $cow).Hash
    @{ramMiB=$RamMiB} | ConvertTo-Json | Set-Content -LiteralPath ($prefix+'.profile.json') -Encoding utf8
    $command=if($Source -eq 'floppy'){"a:$name"}else{$name}
    $marker=if($Kind -eq 'vcpi'){'VCPI-PROTECTED-PROOF='}else{'EMS-BANK-PROOF='}
    @(@{waitForScreen='C:\>';text="kezb us`r`n"},@{waitForScreen='C:\>keyb us';text="mem /c`r`n"},
      @{waitForDebug='C:\>mem /c';text="$command`r`n"},@{waitForDebug=$marker;text="ver`r`necho MEMORY-GUEST-END`r`n"}) |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix+'.scenario.json') -Encoding utf8
    $arguments=@($headless,'--disk',$Disk,'--overlay',$cow,'--profile',($prefix+'.profile.json'),
        '--scenario',($prefix+'.scenario.json'),'--steps','150000000','--stop-text',"`r`nMEMORY-GUEST-END`r`n`r`nC:\>",
        '--report',($prefix+'.json'),'--memory-dump',($prefix+'.private-ram.bin'))
    if($Source -eq 'floppy'){$arguments+=@('--floppy-a',(Join-Path $root ($name+'.img')))}
    dotnet @arguments > ($prefix+'.stdout.json')
    $verification=@($oracle,'--kind',$Kind,'--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),
        '--program',(Join-Path $root ($name+'.COM')),'--source',$Source,'--ram-mib',"$RamMiB")
    if($Unavailable){$verification+='--expect-unavailable'}
    python @verification > ($prefix+'.verified.json')
    if((Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'Memory client changed its private COW'}
    $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    $p=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
    return @{kind=$Kind;mode=$Mode;ramMiB=$RamMiB;source=$Source;prefix=$prefix;proof=$p;
        frameHash=$r.frameHash;virtualNanoseconds=$r.status.virtualNanoseconds}
}
$cases=@()
foreach($mode in @('NOEMS','RAM')){
    $kinds=if($mode -eq 'RAM'){@('vcpi','ems')}else{@('vcpi')}
    foreach($kind in $kinds){foreach($source in @('ide','floppy')){
        $pair=@(Run-MemoryGuest $kind $mode 8 $source 1; Run-MemoryGuest $kind $mode 8 $source 2)
        if($pair[0].frameHash -ne $pair[1].frameHash -or $pair[0].virtualNanoseconds -ne $pair[1].virtualNanoseconds -or $pair[0].proof.proofSha256 -ne $pair[1].proof.proofSha256){throw 'Repeated memory-client cold boots differ'}
        $cases+=$pair
    }}
}
foreach($ramMiB in @(4,16)){foreach($kind in @('vcpi','ems')){
    $pair=@(Run-MemoryGuest $kind RAM $ramMiB floppy 1; Run-MemoryGuest $kind RAM $ramMiB floppy 2)
    if($pair[0].frameHash -ne $pair[1].frameHash -or $pair[0].virtualNanoseconds -ne $pair[1].virtualNanoseconds -or $pair[0].proof.proofSha256 -ne $pair[1].proof.proofSha256){throw 'RAM-size cold boots differ'}
    $cases+=$pair
}}
$unavailable=@(Run-MemoryGuest vcpi no-driver 8 ide 1 -Unavailable; Run-MemoryGuest ems NOEMS 8 ide 1 -Unavailable)
foreach($kind in @('vcpi','ems')){
    $case=$cases | Where-Object {$_.kind -eq $kind -and $_.ramMiB -eq 8} | Select-Object -First 1
    python $oracle --kind $kind --report ($case.prefix+'.json') --memory ($case.prefix+'.private-ram.bin') --program (Join-Path $root ($programs[$kind]+'.COM')) --negative-controls (Join-Path $root ($kind+'-negative-controls')) > (Join-Path $root ($kind+'-negative-validation.json'))
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'Protected media or tested assembly changed'}}
$summary=@{passed=$true;directory=$root;configurationColdBoots=4;positiveColdBoots=$cases.Count;
    unavailableControls=$unavailable.Count;negativeOracleControls=16;ramMiB=@(4,8,16);cases=$cases;
    options=@('NOEMS','RAM');originalsUnchanged=$true;configuredOnlyInPrivateCopies=$true;
    vcpiProtectedClientAccepted=$true;emsBankingAccepted=$true;hardwareTaskSwitchAccepted=$false;
    dosExtenderAccepted=$false;floppyWritesUnderEmm386Accepted=$false;milestoneM3Accepted=$false}
$summary | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 9
