# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[switch]$Vbe,[switch]$Targeted,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory)][string]$Disk,[Parameter(Mandatory)][string]$Overlay,
    [string]$ConfiguredDisk,[string]$NoEmsOverlay,[string]$RamOverlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$buildRoot=Get-DosBuildRoot -OutputRoot $OutputRoot
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$Disk=Resolve-DosPath $Disk; $Overlay=Resolve-DosPath $Overlay
$configured=@($ConfiguredDisk,$NoEmsOverlay,$RamOverlay | Where-Object { $_ })
if($configured.Count -ne 0 -and $configured.Count -ne 3){throw 'Supply all three optional manager configuration paths together.'}
$systems=@([pscustomobject]@{name='msdos';disk=$Disk;overlay=$Overlay},
    [pscustomobject]@{name='freedos';disk=(Join-Path $script:DosWorkspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img');overlay=$null})
if($configured.Count -eq 3){$systems+=@(
    [pscustomobject]@{name='emm386-noems';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $NoEmsOverlay)},
    [pscustomobject]@{name='emm386-ram';disk=(Resolve-DosPath $ConfiguredDisk);overlay=(Resolve-DosPath $RamOverlay)})}
foreach($s in $systems){foreach($p in @($s.disk,$s.overlay | Where-Object { $_ })){if($p -match '(?i)doom2-play'){throw 'User play media are excluded.'}}}
$kind=if($Vbe){'vbe-m3'}else{'et4000-m3'}
$root=Join-Path $script:DosWorkspaceRoot ('artifacts\'+$kind+'\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$assemblies=Get-ChildItem -LiteralPath (Join-Path $buildRoot 'Release\net10.0') -File | Where-Object Extension -eq '.dll' | Get-DosFileHash
$assemblies | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'tested-assemblies.json') -Encoding utf8
$originals=@($systems | ForEach-Object { $_.disk; if($_.overlay){$_.overlay} }) | Select-Object -Unique | Get-DosFileHash
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$nasm=Join-Path $script:DosWorkspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
$profile=Join-Path $script:DosWorkspaceRoot 'profiles\virtual486-et4000-dev-v1.json'
$modes=@(@{code=0x2e;name='ET640';vbe=0x101},@{code=0x30;name='ET800';vbe=0x103},@{code=0x38;name='ET1024';vbe=0x105})
foreach($mode in $modes){
    $assemblerArgs=@('-Werror','-f','bin',('-DTEST_MODE='+$mode.code),'-I',(Join-Path $script:DosWorkspaceRoot 'tests/guests/'))
    if($Vbe){$mode.name=$mode.name.Replace('ET','VBE');$assemblerArgs+=('-DTEST_VBE='+$mode.vbe)}
    $assemblerArgs+=@((Join-Path $script:DosWorkspaceRoot 'tests\guests\et4000-test.asm'),'-o',(Join-Path $root ($mode.name+'.COM')))
    & $nasm @assemblerArgs
    if($LASTEXITCODE -ne 0){throw 'ET4000 probe assembly failed'}
}
$floppy=Join-Path $root 'only-own-probes.img'
$mediaArgs=@((Join-Path $script:DosWorkspaceRoot 'tests\prepare-et4000-media.py'),$root,$floppy)
if($Vbe){$mediaArgs+='--vbe'}
python @mediaArgs > (Join-Path $root 'media.json')
if($LASTEXITCODE -ne 0){throw 'ET4000 own FAT12 preparation failed'}
$originals+=Get-DosFileHash -LiteralPath $floppy
$rows=[Collections.Generic.List[object]]::new();$returns=[Collections.Generic.List[object]]::new();$negative=0
foreach($s in $systems){
    # Targeted: one full 1024x768 oracle and all three guest-checked return modes per DOS configuration.
    $selectedModes = if($Targeted){@($modes[-1])+@(@{code=0;name='RETURN'})}else{@($modes)+@(@{code=0;name='RETURN'})}
    foreach($mode in $selectedModes){
        $plan=@();$prompt='C:\>'
        if($s.name -ne 'freedos'){$plan+=@{waitForScreen=$prompt;text="kezb us`r`n"};$prompt='C:\>keyb us'}
        $text=if($mode.code){'a:'+$mode.name.ToLowerInvariant()+"`r`n"}else{
            (($modes | ForEach-Object {'a:'+$_.name.ToLowerInvariant()+" /a`r`n"}) -join '')+"ver`r`necho ET4000-RETURN-END`r`n"
        }
        $plan+=@{waitForScreen=$prompt;text=$text}
        $scenario=Join-Path $root ($s.name+'-'+$mode.name+'.scenario.json')
        ConvertTo-Json -InputObject $plan -Depth 5 | Set-Content -LiteralPath $scenario -Encoding utf8
        $first=$null
        $repeatCount = if($Targeted){1}else{2}
        foreach($repeat in 1..$repeatCount){
            $prefix=Join-Path $root ($s.name+'-'+$mode.name+'-'+$repeat);$cow=$null;$cowHash=$null
            $arguments=@($runner,'--profile',$profile,'--disk',$s.disk,'--floppy-a',$floppy,'--scenario',$scenario,'--steps','500000000','--report',($prefix+'.json'))
            if($mode.code){$arguments+=@('--stop-text','ET4000-READY','--frame',($prefix+'.ppm'),'--memory-dump',($prefix+'.private-ram.bin'),'--video-io-trace',($prefix+'.io.jsonl'))}
            else{$arguments+=@('--stop-text',$(if($s.name -eq 'freedos'){"`r`nET4000-RETURN-END`r`nC:\>"}else{"`r`nET4000-RETURN-END`r`n`r`nC:\>"}))}
            if($s.overlay){$cow=$prefix+'.cow';Copy-Item -LiteralPath $s.overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow).Hash;$arguments+=@('--overlay',$cow)}
            dotnet @arguments > ($prefix+'.stdout.json')
            if($LASTEXITCODE -ne 0){throw 'ET4000 guest process failed'}
            $r=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
            if($r.status.fault -or -not $r.scenario.complete -or $r.scenario.inputBytesSent -ne $r.scenario.inputBytesTotal -or $r.status.debugOutput.Contains('ET4000-FAILED') -or $r.ataSectorsWritten -ne 0 -or $r.floppySectorsWritten -ne 0){throw 'ET4000 guest fault, input failure or media write'}
            if($mode.code){
                $oracle=@('--report',($prefix+'.json'),'--memory',($prefix+'.private-ram.bin'),'--frame',($prefix+'.ppm'),'--io',($prefix+'.io.jsonl'),
                    '--program',(Join-Path $root ($mode.name+'.COM')),'--mode',$(if($Vbe){$mode.vbe}else{$mode.code}))
                if($Vbe){$oracle+=@('--rom',(Join-Path $script:DosWorkspaceRoot 'artifacts/firmware/dose-bios.bin'))}
                if($repeat -eq 1){$oracle+='--negative-controls'}
                if($s.name -like 'emm386-*'){$oracle+='--manager'}
                $checker=if($Vbe){'tests/verify-vbe.py'}else{'tests/verify-et4000.py'}
                python (Join-Path $script:DosWorkspaceRoot $checker) @oracle > ($prefix+'.verified.json')
                if($LASTEXITCODE -ne 0){throw 'Independent ET4000 pixel/port/RAM oracle failed'}
                $v=Get-Content -LiteralPath ($prefix+'.verified.json') -Raw | ConvertFrom-Json
                $negative+=$v.negativeControlsRejected
                $proofHash=if($Vbe){$v.legacyAndWholePixelOracle.proofHash}else{$v.proofHash}
                $row=[pscustomobject]@{system=$s.name;mode=$mode.code;vbeMode=$(if($Vbe){$mode.vbe}else{0});repeat=$repeat;report=$prefix+'.json';verification=$v;virtualNanoseconds=$r.status.virtualNanoseconds;frameHash=$r.frameHash;proofHash=$proofHash}
                $rows.Add($row)
            }else{
                if($r.graphicsMode -or $r.width -ne 640 -or $r.height -ne 400 -or ([regex]::Matches($r.status.debugOutput,'ET4000-DOS-RETURN-OK')).Count -ne 3 -or $r.video.segmentSelect -ne 0){throw 'ET4000 three-mode DOS return incomplete'}
                if($s.name -like 'emm386-*' -and (-not $r.cpu.pagingEnabled -or $r.cpu.virtual8086Instructions -le 0)){throw 'Missing actual EMM386 return path'}
                $row=[pscustomobject]@{system=$s.name;repeat=$repeat;report=$prefix+'.json';virtualNanoseconds=$r.status.virtualNanoseconds;frameHash=$r.frameHash}
                $returns.Add($row)
            }
            if($first){foreach($key in @('virtualNanoseconds','frameHash')){if($row.$key -ne $first.$key){throw ('Nonrepeatable ET4000 '+$key)}}}else{$first=$row}
            if($cow -and (Get-DosFileHash -LiteralPath $cow).Hash -ne $cowHash){throw 'ET4000 guest modified its private installed HDD'}
        }
    }
}
foreach($item in @($originals)+@($assemblies)){if((Get-DosFileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Protected input or tested DLL changed: '+$item.Path)}}
$scope=if($Vbe){'ET4000AX ISA 1 MiB, VBE 1.2-format banked subset 00/01/02/03/05 plus legacy regression; no full VBE/chip conformance, LFB or high-color'}else{'ET4000AX ISA 1 MiB legacy bank/pixel/DOS regression; this probe does not exercise the VBE subset'}
$summary=[pscustomobject]@{passed=$true;directory=$root;headlessColdBoots=$rows.Count+$returns.Count;graphicsColdBoots=$rows.Count;returnColdBoots=$returns.Count;vbeProbe=$Vbe.IsPresent;
    cases=$rows;returns=$returns;negativeControlsRejected=$negative;testedAssembliesUnchanged=$true;originalsUnchanged=$true;nativeInputSent=$false;
    scope=$scope;targeted=$Targeted.IsPresent;fullGraphicsResolutionMatrix=(-not $Targeted.IsPresent)}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 12
