#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk;$Overlay=Resolve-DosPath $Overlay
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\opl2-csm-level-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash
$profile=Join-Path $root 'profile.json'
@{name='CSM total-level latch DOS acceptance';soundCard='adlib-opl2-development'} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
$probe=Join-Path $root 'CSMLEVEL.COM'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\opl2-csm-level-test.asm') -o $probe
$floppy=Join-Path $root 'only-own-csm-level-probe.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy CSMLEVEL.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$cases=@()
foreach($os in @('msdos','freedos')){
    $steps=@();$base=$freeDos;$wait='C:\>'
    if($os -eq 'msdos'){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
    $steps+=@{waitForScreen=$wait;text="a:csmlevel`r`nver`r`necho CSM-LEVEL-RUN-END`r`n"}
    $plan=Join-Path $root ($os+'.scenario.json')
    ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
    $runs=@()
    foreach($number in 1..2){
        $prefix=Join-Path $root ($os+'-'+$number);$cow=$null
        $stop=if($os -eq 'msdos'){"`r`nCSM-LEVEL-RUN-END`r`n`r`nC:\>"}else{"`r`nCSM-LEVEL-RUN-END`r`nC:\>"}
        $arguments=@((Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'),'--disk',$base,'--profile',$profile,
            '--floppy-a',$floppy,'--scenario',$plan,'--steps','120000000','--stop-text',$stop,'--report',($prefix+'.json'),
            '--audio-out',($prefix+'.wav'),'--audio-io-trace',($prefix+'.io.jsonl'))
        if($os -eq 'msdos'){
            $cow=$prefix+'.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
            $hash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',$cow)
        }
        dotnet @arguments > ($prefix+'.stdout.json')
        $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
        python (Join-Path $script:DosWorkspaceRoot 'tests\verify-opl2-csm-levels.py') --report ($prefix+'.json') --audio ($prefix+'.wav') --io ($prefix+'.io.jsonl') > ($prefix+'.verified.json')
        if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'CSM level probe changed COW'}
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].audio.dataSha256 -ne $runs[1].audio.dataSha256 -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'CSM level cold boots differ'}
    $cases+=@{os=$os;coldBoots=2;pcmSha256=$runs[0].audio.dataSha256;frameHash=$runs[0].frameHash;pcmFrames=$runs[0].audio.recordedFrames;guestReturned=$true}
}
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'CSM level protected media changed'}}
$summary=@{passed=$true;directory=$root;headlessColdBoots=4;cases=$cases;originalsUnchanged=$true;onlyOwnProgramOnFloppy=$true;siliconComparison=$false}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
