param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$OutputRoot = 'artifacts\m1-build')
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
try {
    $env:MSBuildEnableWorkloadResolver = 'false'
    Push-Location -LiteralPath $workspaceRoot
    & (Join-Path $PSScriptRoot 'build-firmware.ps1')
    $buildArguments = @('build','DOSEmulator.slnx','-c',$Configuration,'--ignore-failed-sources','-p:NuGetAudit=false','-p:UseSharedCompilation=false','-m:1','-nr:false','-v:minimal')
    if ($OutputRoot) { $buildArguments += ('-p:BaseOutputPath=' + [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\') }
    dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
} finally {
    Pop-Location
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
}
