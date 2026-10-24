param([switch]$Download, [string]$Runner)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$originalDirectory = Join-Path $workspaceRoot 'artifacts\freedos-floppy-original'
$archivePath = Join-Path $originalDirectory 'FD14-FloppyEdition.zip'
$originalPath = Join-Path $originalDirectory 'FD14BOOT144.img'
$testDirectory = Join-Path $workspaceRoot 'artifacts\freedos-m2\floppy'
$imagePath = Join-Path $testDirectory 'FD14-minimal.img'
New-Item -ItemType Directory -Path $originalDirectory,$testDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $archivePath)) {
    if (-not $Download) { throw 'Official FreeDOS floppy archive missing. Use -Download.' }
    Invoke-WebRequest -Uri 'https://download.freedos.org/1.4/FD14-FloppyEdition.zip' -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne '45B1FA7C52DD996C3BFA5E352FFCD410781B952A6AD629F15A4C9EC4BBAEFC5A') { throw 'FreeDOS floppy archive checksum mismatch.' }
if (-not (Test-Path -LiteralPath $originalPath)) {
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('144m/x86BOOT.img'),$originalPath) } finally { $archive.Dispose() }
}
if ((Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash -ne '552F7CBB0625960C050A3E682A4D2121CC2FFDFA9DC9D592CCD49EDF179333DC') { throw 'FreeDOS original floppy checksum mismatch.' }
if (-not (Test-Path -LiteralPath $imagePath)) {
    if (-not $Runner) { $Runner = Join-Path $workspaceRoot 'artifacts\m1-build\Release\net10.0\DOSEmulator.Headless.dll' }
    dotnet $Runner --prepare-freedos-floppy $originalPath --output $imagePath
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the private minimal FreeDOS floppy.' }
}
$metadata = Get-Content -Raw -LiteralPath ($imagePath + '.json') | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash -ne $metadata.derivedHash) { throw 'Private floppy base changed.' }
Write-Output $imagePath
