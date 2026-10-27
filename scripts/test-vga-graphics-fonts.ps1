# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipBuild,[switch]$Gui,[string]$OutputRoot='artifacts\m1-build',
    [Parameter(Mandatory=$true)][string]$Disk,[Parameter(Mandatory=$true)][string]$Overlay)
. (Join-Path $PSScriptRoot 'common.ps1')
$workspaceRoot=Split-Path -Parent $PSScriptRoot
$buildRoot = Get-DosBuildRoot -OutputRoot $OutputRoot
$Disk=(Resolve-Path -LiteralPath $Disk).Path
$Overlay=(Resolve-Path -LiteralPath $Overlay).Path
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $buildRoot}
$runner=Join-Path $buildRoot 'Release\net10.0\DOSEmulator.Headless.dll'
$freeDos=Join-Path $workspaceRoot 'artifacts\freedos-m1\FD14LITE-minimal.img'
$testRoot=Join-Path $workspaceRoot ('artifacts\vga-graphics-font-m2\acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originals=@($Disk,$Overlay,$freeDos) | Get-DosFileHash -Algorithm SHA256
$assembler=Join-Path $workspaceRoot 'artifacts\tools\nasm-3.02\nasm-3.02\nasm.exe'
foreach($mode in @('12','13')){
    & $assembler -Werror -f bin ('-DTEST_MODE=0x'+$mode) (Join-Path $workspaceRoot 'tests\guests\graphics-font-test.asm') -o (Join-Path $testRoot ('GFX'+$mode+'.COM'))
    if($LASTEXITCODE -ne 0){throw 'Own graphics font assembly failed'}
}
$floppy=Join-Path $testRoot 'only-own-graphics-programs.img'
python (Join-Path $workspaceRoot 'tests\prepare-graphics-font-media.py') (Join-Path $testRoot 'GFX12.COM') (Join-Path $testRoot 'GFX13.COM') $floppy > (Join-Path $testRoot 'probe-media.json')
if($LASTEXITCODE -ne 0){throw 'Own two-program graphics floppy preparation failed'}
$floppyHash=(Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash
# Writes an explicit private guest-input plan with bounded waits and required output markers.
function Scenario($Name,$Steps){
    $path=Join-Path $testRoot ($Name+'.scenario.json')
    ConvertTo-Json -InputObject $Steps -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
# Runs the actual headless guest with explicit private media, captures evidence and rejects execution failures.
function Guest($Name,$Base,$Cow,$Plan,[bool]$Capture){
    $prefix=Join-Path $testRoot $Name
    $arguments=@($runner,'--disk',$Base,'--floppy-a',$floppy,'--scenario',$Plan,'--steps','150000000','--report',($prefix+'.json'))
    if($Cow){$arguments+=@('--overlay',$Cow)}
    if($Capture){$arguments+=@('--stop-text','GFX-FONT-READY','--frame',($prefix+'.ppm'),'--memory-dump',($prefix+'.private-memory.bin'),'--video-io-trace',($prefix+'.video.jsonl'))}
    dotnet @arguments > ($prefix+'.stdout.json')
    if($LASTEXITCODE -ne 0){throw "Graphics font guest $Name failed"}
    $result=Get-Content -LiteralPath ($prefix+'.json') -Raw | ConvertFrom-Json
    if($result.status.fault -or -not $result.scenario.complete -or $result.scenario.inputBytesSent -ne $result.scenario.inputBytesTotal -or $result.status.debugOutput.Contains('GFX-FONT-FAILED') -or $result.ataSectorsWritten -ne 0 -or $result.floppySectorsWritten -ne 0){throw "Graphics font guest $Name incomplete or wrote a disk"}
    return $result
}
$cases=@()
foreach($mode in @('12','13')){foreach($points in @(8,14,16)){$cases+=@{name="msdos-$mode-font$points";mode=$mode;points=$points;msdos=$true;gui=(($mode -eq '12' -and $points -in @(14,16)) -or ($mode -eq '13' -and $points -eq 8))}}}
$cases+=@(@{name='freedos-12-font16';mode='12';points=16;msdos=$false;gui=$false},@{name='freedos-13-font8';mode='13';points=8;msdos=$false;gui=$false})
$results=@()
foreach($case in $cases){
    $steps=@()
    $base=$freeDos
    if($case.msdos){$base=$Disk;$steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'}else{$wait='C:\>'}
    $steps+=@{waitForScreen=$wait;text="a:gfx$($case.mode) /$($case.points)`r`n"}
    $plan=Scenario $case.name $steps
    $runs=@()
    foreach($number in 1..2){
        $name=$case.name+'-'+$number
        $cow=$null
        if($case.msdos){$cow=Join-Path $testRoot ($name+'.cow');Copy-Item -LiteralPath $Overlay -Destination $cow;$cowHash=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash}
        $result=Guest $name $base $cow $plan $true
        if(-not $result.status.debugOutput.Contains('GFX-FONT-SERVICES-OK') -or -not $result.status.debugOutput.Contains('GFX-FONT-256-GLYPHS-OK') -or $result.floppySectorsRead -eq 0 -or -not $result.graphicsMode -or $result.indexed256 -ne ($case.mode -eq '13')){throw 'Actual guest graphics font checks did not complete'}
        if($case.msdos){
            $prefix=Join-Path $testRoot $name
            python (Join-Path $workspaceRoot 'tests\verify-graphics-font-frame.py') --base $base --overlay $cow --frame ($prefix+'.ppm') --report ($prefix+'.json') --memory ($prefix+'.private-memory.bin') --mode $case.mode --points $case.points > ($prefix+'.verified.json')
            if($LASTEXITCODE -ne 0){throw 'Independent complete graphics CPI/pixel check failed'}
            if((Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $cowHash){throw 'Read-only graphics font guest changed its COW'}
        }
        $runs+=$result
    }
    if($runs[0].frameHash -ne $runs[1].frameHash -or $runs[0].status.virtualNanoseconds -ne $runs[1].status.virtualNanoseconds){throw 'Graphics font cold boots differ'}
    $results+=[pscustomobject]@{name=$case.name;mode=$case.mode;points=$case.points;width=$runs[0].width;height=$runs[0].height;coldBoots=2;frameHash=$runs[0].frameHash;nanoseconds=$runs[0].status.virtualNanoseconds;everyPixelVerified=[bool]$case.msdos;all256GlyphsReadBack=$true}
    if($Gui -and $case.gui){
        $guiRoot=Join-Path $testRoot ('gui-'+$case.name)
        $guiCow=Join-Path $testRoot ('gui-'+$case.name+'.cow');Copy-Item -LiteralPath $Overlay -Destination $guiCow
        $before=(Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash
        $exe=Join-Path $buildRoot 'Release\net10.0-windows\DOSEmulator.exe'
        $arguments=@('--disk',('"'+$Disk+'"'),'--overlay',('"'+$guiCow+'"'),'--floppy-a',('"'+$floppy+'"'),'--scenario',('"'+$plan+'"'),
            '--data-dir',('"'+(Join-Path $guiRoot 'settings')+'"'),'--acceptance-test',('"'+$guiRoot+'"'),'--capture-after','GFX-FONT-READY')
        $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Native graphics font acceptance timed out'}
        if($process.ExitCode -ne 0){throw 'Native graphics font acceptance failed'}
        $native=Get-Content -LiteralPath (Join-Path $guiRoot 'smoke-result.json') -Raw | ConvertFrom-Json
        if($native.status.fault -or -not $native.scenario.complete -or $native.captures.Count -ne 6 -or -not $native.status.debugOutput.Contains('GFX-FONT-256-GLYPHS-OK')){throw 'Native graphics font checks incomplete'}
        foreach($capture in $native.captures){
            $gl=Get-Content -LiteralPath ($capture+'.json') -Raw | ConvertFrom-Json
            if($gl.error -ne 'NoError' -or $gl.footerBrightPixels -lt 100 -or $gl.guestWidth -ne $runs[0].width -or $gl.guestHeight -ne $runs[0].height){throw 'Native graphics font GL capture incomplete'}
        }
        if((Get-DosFileHash -LiteralPath $guiCow -Algorithm SHA256).Hash -ne $before){throw 'Native graphics font guest changed its COW'}
    }
}
# In each actual DOS shell: both resolutions and several font sizes, normal
# program termination, real VER/ECHO commands after every graphics return.
$returns=@()
foreach($os in @('msdos','freedos')){
    $cow=$null;$base=$freeDos;$steps=@();$wait='C:\>'
    if($os -eq 'msdos'){
        $base=$Disk;$cow=Join-Path $testRoot 'msdos-live-return.cow';Copy-Item -LiteralPath $Overlay -Destination $cow
        $before=(Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash
        $steps+=@{waitForScreen='C:\>';text="kezb us`r`n"};$wait='C:\>keyb us'
    }
    foreach($transition in @(@{mode='13';points=8},@{mode='12';points=16},@{mode='13';points=14},@{mode='12';points=8})){
        $tag="GFX-RETURN-$($transition.mode)-$($transition.points)-OK"
        $steps+=@{waitForScreen=$wait;text="a:gfx$($transition.mode) /$($transition.points) /a`r`nver`r`necho $tag`r`n"}
        $wait=$tag
    }
    $plan=Scenario ($os+'-live-return') $steps
    $returned=Guest ($os+'-live-return') $base $cow $plan $false
    if(-not $returned.text.Contains('GFX-RETURN-12-8-OK') -or -not $returned.text.TrimEnd().EndsWith('C:\>') -or ([regex]::Matches($returned.status.debugOutput,'GFX-FONT-DOS-RETURN-OK')).Count -ne 4){throw 'Resolution/font switches did not return cleanly to DOS'}
    if($cow -and (Get-DosFileHash -LiteralPath $cow -Algorithm SHA256).Hash -ne $before){throw 'Live return changed its COW'}
    $returns+=[pscustomobject]@{os=$os;transitions=4;cleanPrompt=$true;noWrites=$true;scenario=$returned.scenario}
}
foreach($original in $originals){if((Get-DosFileHash -LiteralPath $original.Path -Algorithm SHA256).Hash -ne $original.Hash){throw 'Original DOS source changed'}}
if((Get-DosFileHash -LiteralPath $floppy -Algorithm SHA256).Hash -ne $floppyHash){throw 'Own graphics program floppy changed'}
$summary=[pscustomobject]@{passed=$true;directory=$testRoot;cases=$results;headlessColdBoots=18;nativeProcesses=$(if($Gui){3}else{0});nativeCaptures=$(if($Gui){18}else{0});returns=$returns;originalsUnchanged=$true;onlyOwnProgramsOnFloppy=$true}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'acceptance-summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
