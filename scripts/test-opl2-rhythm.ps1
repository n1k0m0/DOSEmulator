#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[switch]$Gui,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay,
    [string]$ReferenceExe='artifacts\references\ymfm\opl2-reference.exe')
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk
$Overlay=Resolve-DosPath $Overlay
$ReferenceExe=Resolve-DosPath $ReferenceExe
if(-not (Test-Path -LiteralPath $ReferenceExe)){throw 'Supply an independently built YMFM adapter; see docs/hardware/OPL2.md'}
$freeDos=Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\opl2-rhythm-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash
$probe=Join-Path $root 'OPLRHY.COM'
& (Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe') -Werror -f bin (Join-Path $script:DosWorkspaceRoot 'tests\guests\opl2-rhythm-test.asm') -o $probe
$floppy=Join-Path $root 'only-own-rhythm-probe.img'
python (Join-Path $script:DosWorkspaceRoot 'tests\prepare-font-media.py') $probe $floppy OPLRHY.COM > (Join-Path $root 'probe-media.json')
$originals+=Get-DosFileHash -LiteralPath $floppy
$cases=@();$natives=@()
foreach($card in @('adlib-opl2-development','sb2-opl2-development')){
    $profile=Join-Path $root ($card+'.profile.json')
    @{name='OPL2 percussion DOS acceptance';soundCard=$card} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding utf8
    foreach($os in @('msdos','freedos')){
        $name=$os+'-'+$card;$steps=@();$base=$freeDos;$wait='C:\>'
        if($os -eq 'msdos'){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}
        $option=if($card -eq 'sb2-opl2-development'){' /s'}else{''}
        $steps+=@{waitForScreen=$wait;text="a:oplrhy$option`r`nver`r`necho RHYTHM-RUN-END`r`n"}
        $plan=Join-Path $root ($name+'.scenario.json')
        ConvertTo-Json -InputObject $steps -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
        $runs=@()
        foreach($number in 1..2){
            $prefix=Join-Path $root ($name+'-'+$number);$cow=$null
            $stop=if($os -eq 'msdos'){"`r`nRHYTHM-RUN-END`r`n`r`nC:\>"}else{"`r`nRHYTHM-RUN-END`r`nC:\>"}
            $arguments=@((Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'),'--disk',$base,'--profile',$profile,
                '--floppy-a',$floppy,'--scenario',$plan,'--steps','120000000','--stop-text',$stop,'--report',($prefix+'.json'),
                '--audio-out',($prefix+'.wav'),'--audio-io-trace',($prefix+'.io.jsonl'))
            if($os -eq 'msdos'){
                $cow=$prefix+'.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
                $hash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',$cow)
            }
            dotnet @arguments > ($prefix+'.stdout.json')
            $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            if(-not $result.status.debugOutput.Contains('RHYTHM-DOS-RETURN-OK')){throw "Rhythm guest $name/$number failed"}
            python (Join-Path $script:DosWorkspaceRoot 'tests\verify-opl2-rhythm.py') --report ($prefix+'.json') --audio ($prefix+'.wav') --io ($prefix+'.io.jsonl') --reference-exe $ReferenceExe > ($prefix+'.verified.json')
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'Rhythm probe changed COW'}
            $runs+=$result
        }
        if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].audio.dataSha256 -ne $runs[1].audio.dataSha256 -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'Rhythm cold boots differ'}
        $cases+=@{name=$name;coldBoots=2;pcmSha256=$runs[0].audio.dataSha256;frameHash=$runs[0].frameHash;pcmFrames=$runs[0].audio.recordedFrames;guestReturned=$true}
        if($Gui -and $card -eq 'sb2-opl2-development'){
            $guiRoot=Join-Path $root ('gui-'+$os);$cow=$null
            $arguments=@('--disk',('"'+$base+'"'),'--profile',('"'+$profile+'"'),'--floppy-a',('"'+$floppy+'"'),
                '--scenario',('"'+$plan+'"'),'--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),
                '--acceptance-test',('"'+$guiRoot+'"'),'--capture-after',('"'+$stop+'"'))
            if($os -eq 'msdos'){
                $cow=Join-Path $root 'gui-msdos.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
                $hash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',('"'+$cow+'"'))
            }
            $process=Start-Process -FilePath (Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
            if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Native rhythm acceptance timeout'}
            if($process.ExitCode -ne 0){throw 'Native rhythm acceptance failed'}
            $native=Get-Content -LiteralPath (Join-Path $guiRoot 'smoke-result.json') -Raw | ConvertFrom-Json
            if($native.status.fault -or $native.audio.fault -or -not $native.audio.available -or $native.captures.Count -ne 6 -or $native.inputBytesSent -ne $native.inputBytesTotal -or -not $native.status.debugOutput.EndsWith($stop)){throw 'Native rhythm evidence incomplete'}
            foreach($capture in $native.captures){
                $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
                if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100){throw 'Native rhythm GL capture failed'}
            }
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $hash){throw 'Native rhythm probe changed COW'}
            $natives+=@{os=$os;captures=6;hostAudioAvailable=$true;guestReturned=$true}
        }
    }
}
foreach($item in $originals){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw 'Rhythm protected media changed'}}
$summary=@{passed=$true;directory=$root;headlessColdBoots=8;cases=$cases;nativeResults=$natives;originalsUnchanged=$true;onlyOwnProgramOnFloppy=$true;independentYmfmComparison=$true;siliconComparison=$false}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
