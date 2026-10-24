param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
Push-Location -LiteralPath $workspaceRoot
try {
    dotnet artifacts\m1-build\Release\net10.0\DOSEmulator.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw "Regression suite failed ($LASTEXITCODE)." }
    New-Item -ItemType Directory -Path artifacts\boot -Force | Out-Null
    dotnet artifacts\m1-build\Release\net10.0\DOSEmulator.Headless.dll --report artifacts\boot\boot-result.json --frame artifacts\boot\boot.ppm
    if ($LASTEXITCODE -ne 0) { throw "Headless boot failed ($LASTEXITCODE)." }
    $boot = Get-Content -Raw artifacts\boot\boot-result.json | ConvertFrom-Json
    if ($boot.bootMarker -ne 48879 -or $boot.ataSectorsRead -ne 1 -or $null -ne $boot.status.fault) { throw 'Boot evidence failed.' }
} finally { Pop-Location }
