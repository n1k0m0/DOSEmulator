# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Nasm = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$probeRepo = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$probeSource = Join-Path $probeRepo 'tests/guest/windows95-probe.asm'
$probeArtifactRoot = [System.IO.Path]::GetFullPath((Join-Path $probeRepo 'artifacts')) + [System.IO.Path]::DirectorySeparatorChar
$probeOutput = [System.IO.Path]::GetFullPath($Output, $probeRepo)
if (-not $probeOutput.StartsWith($probeArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $probeOutput -match '[/\\]doom2-play([/\\]|$)' -or [System.IO.Path]::GetExtension($probeOutput) -ine '.exe') {
    throw 'The new guest EXE must have an explicit private artifacts path outside personal overlays.'
}
$probeListing = [System.IO.Path]::ChangeExtension($probeOutput, '.lst')
$probeReport = $probeOutput + '.build.json'
foreach ($probeTarget in @($probeOutput, $probeListing, $probeReport)) {
    if (Test-Path -LiteralPath $probeTarget) { throw "An output already exists: $probeTarget" }
}
if (-not $Nasm) { $Nasm = Join-Path $probeRepo 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe' }
$probeNasm = [System.IO.Path]::GetFullPath($Nasm, $probeRepo)
if (-not (Test-Path -LiteralPath $probeNasm -PathType Leaf)) { throw "NASM not found: $probeNasm" }
New-Item -ItemType Directory -Path (Split-Path -Parent $probeOutput) -Force | Out-Null

# This invokes only the assembler. The generated PE is never loaded or executed on the host.
& $probeNasm '-Werror' '-f' 'bin' $probeSource '-o' $probeOutput '-l' $probeListing
if ($LASTEXITCODE -ne 0) { throw "Win95 guest assembly failed ($LASTEXITCODE)." }
$script:probeBytes = [System.IO.File]::ReadAllBytes($probeOutput)
if ($script:probeBytes.Length -lt 1024 -or $script:probeBytes.Length -gt 1048576) { throw 'Unexpected own PE length.' }

# Bound every raw generated-image read before decoding untrusted offsets.
function Assert-ProbeRange {
    param([long]$Offset, [long]$Length)
    if ($Offset -lt 0 -or $Length -lt 0 -or $Offset + $Length -gt $script:probeBytes.Length) {
        throw "Generated PE read exceeds the file: $Offset+$Length"
    }
}
# Decode one bounded little-endian PE field without signed narrowing.
function Get-ProbeU16 {
    param([long]$Offset)
    Assert-ProbeRange $Offset 2
    return [long][System.BitConverter]::ToUInt16($script:probeBytes, [int]$Offset)
}
# Retain the full unsigned DWORD value in a PowerShell Int64.
function Get-ProbeU32 {
    param([long]$Offset)
    Assert-ProbeRange $Offset 4
    return [long][System.BitConverter]::ToUInt32($script:probeBytes, [int]$Offset)
}
# Read a bounded NUL-terminated import name, rejecting an unterminated string.
function Get-ProbeAscii {
    param([long]$Offset)
    for ($probeLength = 0; $probeLength -lt 256; $probeLength++) {
        Assert-ProbeRange ($Offset + $probeLength) 1
        if ($script:probeBytes[[int]($Offset + $probeLength)] -eq 0) {
            return [System.Text.Encoding]::ASCII.GetString($script:probeBytes, [int]$Offset, $probeLength)
        }
    }
    throw 'Unterminated generated import name.'
}
# Translate only bytes backed by the single explicit initialized PE section.
function Get-ProbeRaw {
    param([long]$Rva, [long]$Length = 1)
    if ($Rva -lt 4096 -or $Length -lt 0 -or $Rva + $Length -gt 4096 + $script:probeRawSize) {
        throw "RVA has no initialized source bytes: $Rva+$Length"
    }
    $probeRaw = $Rva - 4096 + 512
    Assert-ProbeRange $probeRaw $Length
    return [int]$probeRaw
}

if ((Get-ProbeU16 0) -ne 0x5a4d) { throw 'Missing DOS MZ signature.' }
$probePe = Get-ProbeU32 0x3c
if ($probePe -ne 0x80 -or (Get-ProbeU32 $probePe) -ne 0x00004550 -or
    (Get-ProbeU16 ($probePe + 4)) -ne 0x014c -or (Get-ProbeU16 ($probePe + 6)) -ne 1 -or
    (Get-ProbeU16 ($probePe + 20)) -ne 0xe0 -or (Get-ProbeU16 ($probePe + 22)) -ne 0x0103) {
    throw 'The guest must be an original single-section nonrelocating PE32/i386 executable.'
}
$probeOptional = $probePe + 24
if ((Get-ProbeU16 $probeOptional) -ne 0x010b -or
    (Get-ProbeU32 ($probeOptional + 28)) -ne 0x00400000 -or
    (Get-ProbeU32 ($probeOptional + 32)) -ne 4096 -or
    (Get-ProbeU32 ($probeOptional + 36)) -ne 512 -or
    (Get-ProbeU16 ($probeOptional + 40)) -ne 4 -or
    (Get-ProbeU16 ($probeOptional + 48)) -ne 4 -or
    (Get-ProbeU16 ($probeOptional + 50)) -ne 0 -or
    (Get-ProbeU16 ($probeOptional + 68)) -ne 2 -or
    (Get-ProbeU32 ($probeOptional + 60)) -ne 512 -or
    (Get-ProbeU32 ($probeOptional + 92)) -ne 16) {
    throw 'Unexpected classic Win32 GUI optional header.'
}
$probeSection = $probeOptional + 224
$probeVirtualSize = Get-ProbeU32 ($probeSection + 8)
$script:probeRawSize = Get-ProbeU32 ($probeSection + 16)
if ((Get-ProbeU32 ($probeSection + 12)) -ne 4096 -or
    (Get-ProbeU32 ($probeSection + 20)) -ne 512 -or
    (Get-ProbeU32 ($probeSection + 36)) -ne 0xe0000060L -or
    $script:probeRawSize % 512 -ne 0 -or $probeVirtualSize -gt $script:probeRawSize -or
    512 + $script:probeRawSize -ne $script:probeBytes.Length -or
    (Get-ProbeU32 ($probeOptional + 56)) -ne (($probeVirtualSize + 8191) -band -4096L)) {
    throw 'Inconsistent initialized section extent, protections or SizeOfImage.'
}
$probeEntryRva = Get-ProbeU32 ($probeOptional + 16)
$null = Get-ProbeRaw $probeEntryRva
$probeImportRva = Get-ProbeU32 ($probeOptional + 104)
if ((Get-ProbeU32 ($probeOptional + 108)) -ne 100) { throw 'Expected exactly four import descriptors and a terminator.' }
$probeImportsOffset = Get-ProbeRaw $probeImportRva 100
$probeExpectedImports = [ordered]@{
    'KERNEL32.DLL' = @('GetVersion','GetVersionExA','GetCommandLineA','GetModuleHandleA','GetLastError','GetTickCount','CreateFileA','WriteFile','ReadFile','CloseHandle','FlushFileBuffers','ExitProcess')
    'USER32.DLL' = @('GetSystemMetrics','LoadCursorA','RegisterClassA','CreateWindowExA','ShowWindow','UpdateWindow','GetMessageA','TranslateMessage','DispatchMessageA','DefWindowProcA','BeginPaint','EndPaint','FillRect','GetClientRect','ClientToScreen','SetTimer','KillTimer','DestroyWindow','PostQuitMessage')
    'GDI32.DLL' = @('CreateSolidBrush','DeleteObject','GetDeviceCaps')
    'WINMM.DLL' = @('waveOutGetNumDevs','waveOutOpen','waveOutPrepareHeader','waveOutWrite','waveOutUnprepareHeader','waveOutClose','waveOutReset')
}
$probeActualImports = [ordered]@{}
for ($probeDescriptor = 0; $probeDescriptor -lt 4; $probeDescriptor++) {
    $probeRow = $probeImportsOffset + 20 * $probeDescriptor
    if ((Get-ProbeU32 ($probeRow + 4)) -ne 0 -or (Get-ProbeU32 ($probeRow + 8)) -ne 0) {
        throw 'Bound or timestamped import descriptors are outside this own probe.'
    }
    $probeDll = Get-ProbeAscii (Get-ProbeRaw (Get-ProbeU32 ($probeRow + 12)))
    if (-not $probeExpectedImports.Contains($probeDll) -or $probeActualImports.Contains($probeDll)) { throw "Unexpected DLL: $probeDll" }
    $probeLookup = Get-ProbeU32 $probeRow
    $probeIat = Get-ProbeU32 ($probeRow + 16)
    $probeNames = [System.Collections.Generic.List[string]]::new()
    for ($probeThunk = 0; $probeThunk -lt 64; $probeThunk++) {
        $probeNameRva = Get-ProbeU32 (Get-ProbeRaw ($probeLookup + 4 * $probeThunk) 4)
        if ((Get-ProbeU32 (Get-ProbeRaw ($probeIat + 4 * $probeThunk) 4)) -ne $probeNameRva) { throw 'Import IAT and lookup disagree.' }
        if ($probeNameRva -eq 0) { break }
        if (($probeNameRva -band 2147483648L) -ne 0) { throw 'Ordinal imports are not used by this explicit guest ABI.' }
        $probeNameOffset = Get-ProbeRaw $probeNameRva 3
        if ((Get-ProbeU16 $probeNameOffset) -ne 0) { throw 'Unexpected import hint.' }
        $probeNames.Add((Get-ProbeAscii ($probeNameOffset + 2)))
    }
    $probeWanted = @($probeExpectedImports[$probeDll])
    if ($probeThunk -eq 64 -or $probeNames.Count -ne $probeWanted.Count) { throw "Import count differs for $probeDll" }
    for ($probeIndex = 0; $probeIndex -lt $probeWanted.Count; $probeIndex++) {
        if ($probeNames[$probeIndex] -cne $probeWanted[$probeIndex]) { throw "Import name differs for $probeDll at $probeIndex" }
    }
    $probeActualImports[$probeDll] = $probeNames.ToArray()
}
for ($probeIndex = 80; $probeIndex -lt 100; $probeIndex += 4) {
    if ((Get-ProbeU32 ($probeImportsOffset + $probeIndex)) -ne 0) { throw 'Missing null import descriptor.' }
}

$probeSignature = [System.Text.Encoding]::ASCII.GetBytes('D95META1')
$probeMetaMatches = [System.Collections.Generic.List[int]]::new()
for ($probeIndex = 512; $probeIndex -le $script:probeBytes.Length - 76; $probeIndex++) {
    $probeMatches = $true
    for ($probeByte = 0; $probeByte -lt 8; $probeByte++) {
        if ($script:probeBytes[$probeIndex + $probeByte] -ne $probeSignature[$probeByte]) { $probeMatches = $false; break }
    }
    if ($probeMatches) { $probeMetaMatches.Add($probeIndex) }
}
if ($probeMetaMatches.Count -ne 1) { throw 'Missing or ambiguous own observation metadata.' }
$probeMeta = $probeMetaMatches[0]
if ((Get-ProbeU16 ($probeMeta + 8)) -ne 1 -or (Get-ProbeU16 ($probeMeta + 10)) -ne 16) { throw 'Unknown observation ABI.' }
$probeMetaNames = @('entry','windowProc','proof','proofBytes','waveHeader','waveData','waveBytes','expectedData','readData','dataBytes','proofHwnd','activeWave','writingProof','pollWave','paintWindow','writeProof')
$probeSymbols = [ordered]@{}
for ($probeIndex = 0; $probeIndex -lt 16; $probeIndex++) {
    $probeValue = Get-ProbeU32 ($probeMeta + 12 + 4 * $probeIndex)
    $probeSymbols[$probeMetaNames[$probeIndex]] = $probeValue
    if ($probeIndex -notin @(3,6,9)) { $null = Get-ProbeRaw $probeValue }
}
if ($probeSymbols.entry -ne $probeEntryRva -or $probeSymbols.proofBytes -ne 512 -or
    $probeSymbols.waveBytes -ne 8000 -or $probeSymbols.dataBytes -ne 1027) { throw 'Observation metadata sizes or entry differ.' }
$probeProofRaw = Get-ProbeRaw $probeSymbols.proof 512
if ([System.Text.Encoding]::ASCII.GetString($script:probeBytes, $probeProofRaw, 8) -cne 'DOSE95P1' -or
    (Get-ProbeU32 ($probeProofRaw + 8)) -ne 1 -or (Get-ProbeU32 ($probeProofRaw + 12)) -ne 512) { throw 'Invalid fixed proof ABI.' }
$null = Get-ProbeRaw $probeSymbols.waveHeader 32
$null = Get-ProbeRaw $probeSymbols.waveData 8000
$null = Get-ProbeRaw $probeSymbols.expectedData 1027
$null = Get-ProbeRaw $probeSymbols.readData 1027
$probeManifest = [ordered]@{
    kind = 'own-Windows95-PE32-guest-probe'
    source = $probeSource
    sourceSha256 = (Get-FileHash -LiteralPath $probeSource -Algorithm SHA256).Hash
    output = $probeOutput
    outputSha256 = (Get-FileHash -LiteralPath $probeOutput -Algorithm SHA256).Hash
    bytes = $script:probeBytes.Length
    listing = $probeListing
    listingSha256 = (Get-FileHash -LiteralPath $probeListing -Algorithm SHA256).Hash
    machine = 'i386'
    peFormat = 'PE32'
    imageBase = 4194304
    entryRva = $probeEntryRva
    subsystem = 'Windows GUI 4.0'
    imports = $probeActualImports
    symbolsRva = $probeSymbols
    proof = @{ path = 'C:\DOSE95.BIN'; bytes = 512; signature = 'DOSE95P1'; version = 1; writerResults = 'previous complete attempt only; current-file presence/bytes must be checked externally' }
    data = @{ path = 'C:\DOSE95.DAT'; bytes = 1027; formula = '(i*73 + (i>>3)*19 + 0x5A) & 255' }
    outputWave = @{ switch = '/wave'; rate = 8000; channels = 1; bits = 8; bytes = 8000; formula = '11 + 6*min(i%80,79-(i%80))'; starts = 'after ShowWindow and first synchronous UpdateWindow'; completion = 'actual WHDR_DONE before any forced reset' }
    execution = 'assembled only; no host or guest execution by this script'
}
$probeJson = ($probeManifest | ConvertTo-Json -Depth 12) + "`n"
$probeReportStream = [System.IO.FileStream]::new($probeReport, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
try {
    $probeJsonBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($probeJson)
    $probeReportStream.Write($probeJsonBytes, 0, $probeJsonBytes.Length)
    $probeReportStream.Flush($true)
} finally { $probeReportStream.Dispose() }
$probeManifest | ConvertTo-Json -Depth 12
