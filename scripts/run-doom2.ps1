#requires -Version 7.4
[CmdletBinding()]
param([switch]$PrepareOnly)
. (Join-Path $PSScriptRoot 'common.ps1')
$playRoot=Join-Path $script:DosWorkspaceRoot 'artifacts\doom2-play'
New-Item -ItemType Directory -Path $playRoot -Force | Out-Null
$lock=$null
try {
    # Keep two manual launches from choosing/using the same writable session.
    $lock=[IO.File]::Open((Join-Path $playRoot 'session.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $manifestPath=Join-Path $playRoot 'session.json'
    if(-not (Test-Path -LiteralPath $manifestPath)){
        $acceptedRoot=Join-Path $script:DosWorkspaceRoot 'artifacts\dos-extender-m3'
        $sample=$null
        foreach($candidate in @(Get-ChildItem -LiteralPath $acceptedRoot -Directory -Filter 'acceptance-*' | Sort-Object LastWriteTimeUtc -Descending)){
            $summaryPath=Join-Path $candidate.FullName 'acceptance-summary.json'
            if(-not (Test-Path -LiteralPath $summaryPath)){continue}
            $summary=Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
            if(-not $summary.passed -or -not $summary.originalsUnchanged){continue}
            $image=Join-Path $candidate.FullName 'private-doom-installed.img'
            $overlay=Join-Path $candidate.FullName 'configure-HIMEM.cow'
            if(-not (Test-Path -LiteralPath $image) -or -not (Test-Path -LiteralPath $overlay)){continue}
            $imageEvidence=@($summary.originalFileHashes | Where-Object Path -EQ $image)
            $overlayEvidence=@($summary.originalFileHashes | Where-Object Path -EQ $overlay)
            if($imageEvidence.Count -ne 1 -or $overlayEvidence.Count -ne 1){continue}
            if((Get-DosFileHash -LiteralPath $image).Hash -ne $imageEvidence[0].Hash -or
               (Get-DosFileHash -LiteralPath $overlay).Hash -ne $overlayEvidence[0].Hash){continue}
            $sample=[pscustomobject]@{image=$image;overlay=$overlay;imageHash=$imageEvidence[0].Hash;overlayHash=$overlayEvidence[0].Hash}
            break
        }
        if(-not $sample){throw 'Kein unveraendertes, abgenommenes privates Doom-II-Testimage gefunden. Bitte zuerst die privaten DOS4GW-Testmedien vorbereiten.'}
        $sessionName='session-'+[guid]::NewGuid().ToString('N')
        $sessionRoot=Join-Path $playRoot $sessionName
        New-Item -ItemType Directory -Path $sessionRoot | Out-Null
        Copy-Item -LiteralPath $sample.image -Destination (Join-Path $sessionRoot 'dos-doom2.img')
        Copy-Item -LiteralPath $sample.overlay -Destination (Join-Path $sessionRoot 'play.cow')
        if((Get-DosFileHash -LiteralPath (Join-Path $sessionRoot 'dos-doom2.img')).Hash -ne $sample.imageHash -or
           (Get-DosFileHash -LiteralPath (Join-Path $sessionRoot 'play.cow')).Hash -ne $sample.overlayHash){throw 'Private Medienkopie stimmt nicht mit der Vorlage ueberein.'}
        $state=[pscustomobject]@{schema=1;sessionDirectory=$sessionName;sourceImage=$sample.image;sourceOverlay=$sample.overlay;
            baseSha256=$sample.imageHash;initialOverlaySha256=$sample.overlayHash;experimentalInteractivePlay=$true}
        $state | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
    }
    $state=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if($state.schema -ne 1 -or $state.sessionDirectory -notmatch '^session-[0-9a-f]{32}$'){throw 'Ungueltiges privates Doom-II-Sitzungsmanifest.'}
    $sessionRoot=Join-Path $playRoot $state.sessionDirectory
    $disk=Join-Path $sessionRoot 'dos-doom2.img';$cow=Join-Path $sessionRoot 'play.cow'
    if(-not (Test-Path -LiteralPath $cow) -or (Get-DosFileHash -LiteralPath $disk).Hash -ne $state.baseSha256){throw 'Private Doom-II-Medien fehlen oder das schreibgeschuetzte Basisimage wurde veraendert.'}
    # An existing play.cow deliberately keeps the user's settings and saves.
    $profile=Join-Path $script:DosWorkspaceRoot 'profiles\virtual486-dev-v1.json'
    $exe=Join-Path $script:DosWorkspaceRoot 'artifacts\m1-build\Release\net10.0-windows\DOSEmulator.exe'
    if($PrepareOnly){
        [pscustomobject]@{prepared=$true;disk=$disk;overlay=$cow;profile=$profile;
            command='doom2 -config doomtest.cfg -nosound';sourceImage=$state.sourceImage;sourceOverlay=$state.sourceOverlay;
            sourceImageUnchanged=((Get-DosFileHash -LiteralPath $state.sourceImage).Hash -eq $state.baseSha256);
            sourceOverlayUnchanged=((Get-DosFileHash -LiteralPath $state.sourceOverlay).Hash -eq $state.initialOverlaySha256);
            nativeWindowStarted=$false;interactivePlayAccepted=$false} | ConvertTo-Json
        return
    }
    if(-not (Test-Path -LiteralPath $exe)){& (Join-Path $PSScriptRoot 'build.ps1')}
    Write-Host 'Experimenteller Doom-II-Spieltest: MS-DOS startet mit HIMEM und 8 MiB RAM.'
    Write-Host 'Bei C:\> eingeben: doom2 -config doomtest.cfg -nosound'
    Write-Host 'Spielstaende und Einstellungen bleiben im eigenen privaten Overlay erhalten.'
    & $exe --disk $disk --overlay $cow --profile $profile
} finally {if($lock){$lock.Dispose()}}
