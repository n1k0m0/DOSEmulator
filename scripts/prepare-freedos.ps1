param([switch]$Download, [string]$Runner)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$archivePath = Join-Path $workspaceRoot 'artifacts\downloads\FD14-LiteUSB.zip'
$originalDirectory = Join-Path $workspaceRoot 'artifacts\freedos-original'
$originalPath = Join-Path $originalDirectory 'FD14LITE.img'
$testDirectory = Join-Path $workspaceRoot 'artifacts\freedos-m1'
$imagePath = Join-Path $testDirectory 'FD14LITE-minimal.img'
New-Item -ItemType Directory -Path (Split-Path $archivePath),$testDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $archivePath)) {
    if (-not $Download) { throw 'FreeDOS archive missing. Use -Download to fetch the official test medium.' }
    Invoke-WebRequest -Uri 'https://download.freedos.org/1.4/FD14-LiteUSB.zip' -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne '857DCD2EBF9D3D094320154DB5FB5B830ACBA6FB98F981A95A0CA7AB3350338B') { throw 'FreeDOS archive checksum mismatch.' }
if (-not (Test-Path -LiteralPath $originalPath)) { Expand-Archive -LiteralPath $archivePath -DestinationPath $originalDirectory }
if ((Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash -ne 'F539D456B792594BC3CA59D4E0F4C23D4F1FEE73370C1390B2DA245400718D36') { throw 'FreeDOS original image checksum mismatch.' }
if (-not (Test-Path -LiteralPath $imagePath)) {
    if (-not $Runner) { $Runner = Join-Path $workspaceRoot 'artifacts\m1-build\Release\net10.0\DOSEmulator.Headless.dll' }
    dotnet $Runner --prepare-freedos $originalPath --output $imagePath
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the private FreeDOS test image.' }
}
$metadata = Get-Content -Raw -LiteralPath ($imagePath + '.json') | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash -ne $metadata.derivedHash) { throw 'Derived read-only test image has changed.' }
Write-Output $imagePath
